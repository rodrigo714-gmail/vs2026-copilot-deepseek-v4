using System.Text.Json;

/// <summary>How a provider's free allowance behaves.</summary>
public enum FreeTierType
{
    /// <summary>No free tier — pay as you go.</summary>
    None,
    /// <summary>Resets every day.</summary>
    RecurringDaily,
    /// <summary>Resets every calendar month.</summary>
    RecurringMonthly,
    /// <summary>Permanently free but with no published token cap — rate-limited instead.</summary>
    RecurringUncapped,
    /// <summary>A signup credit that does not recur.</summary>
    OneTimeCredit
}

/// <summary>A provider's published free allowance, as read from <c>config/free-tier/catalog.json</c>.</summary>
internal sealed record FreeTierEntry(
    string Provider,
    string? PoolKey,
    FreeTierType FreeType,
    long? MonthlyTokens,
    long? DailyTokens,
    long? CreditTokens,
    int? RequestsPerMinute,
    int? RequestsPerDay,
    string Tos,
    string? TosNote,
    string? SignupUrl,
    string? SourceUrl,
    string? VerifiedAt,
    string? Notes)
{
    /// <summary>
    /// The allowance normalised to tokens per month, or null when there is nothing countable.
    /// Uncapped and one-time entries deliberately return null: a rate limit extrapolated to
    /// 24/7 is a fantasy, and a signup credit is not a monthly grant.
    /// </summary>
    internal long? MonthlyTokenEquivalent(int daysInMonth) => FreeType switch
    {
        FreeTierType.RecurringMonthly => MonthlyTokens,
        FreeTierType.RecurringDaily => DailyTokens is { } d ? d * daysInMonth : MonthlyTokens,
        _ => null
    };
}

/// <summary>
/// Loads the free-tier catalog: how much each provider gives away, on what cadence, and whether
/// its terms are comfortable with a self-hosted proxy.
///
/// This is data rather than C# so a quota can be re-verified and corrected without a rebuild —
/// free tiers change constantly, and a figure that needs a compiler to fix stays wrong.
///
/// Two accounting rules keep the headline honest, both learned from how these numbers are
/// usually inflated:
///
/// * <b>Shared pools are counted once.</b> Providers that serve several model variants out of
///   one budget (the Gemini Flash family, for instance) share a <c>pool_key</c>; summing the
///   variants separately would multiply the same allowance several-fold.
/// * <b>Uncapped tiers are listed but never summed.</b> A provider that is permanently free but
///   publishes only a rate limit has real value that cannot be expressed as tokens per month;
///   multiplying its RPM by 24/7 would produce a ceiling nobody will ever reach.
/// </summary>
internal sealed class FreeTierCatalogStore
{
    private readonly Dictionary<string, FreeTierEntry> _byProvider = new(StringComparer.OrdinalIgnoreCase);

    internal string CuratedAt { get; private set; } = "unknown";
    internal IReadOnlyCollection<FreeTierEntry> Entries => _byProvider.Values;

    public FreeTierCatalogStore(string? catalogPath = null)
    {
        string? path = catalogPath ?? FindCatalog();
        if (path is null)
        {
            Console.WriteLine("[FREE-TIER] No config/free-tier/catalog.json found — free-tier budgets will be unavailable.");
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("curated_at", out JsonElement curated) && curated.ValueKind == JsonValueKind.String)
                CuratedAt = curated.GetString() ?? "unknown";

            if (!root.TryGetProperty("providers", out JsonElement providers) || providers.ValueKind != JsonValueKind.Array)
            {
                Console.WriteLine($"[FREE-TIER] '{path}' has no 'providers' array — free-tier budgets will be unavailable.");
                return;
            }

            foreach (JsonElement p in providers.EnumerateArray())
            {
                FreeTierEntry? entry = ParseEntry(p, path);
                if (entry is not null)
                    _byProvider[entry.Provider] = entry;
            }
        }
        catch (Exception ex)
        {
            // Deliberately not a silent catch: a budget file that failed to parse means every
            // quota figure is silently wrong, which is worse than having none at all.
            Console.WriteLine($"[FREE-TIER] Could not parse '{path}' ({ex.GetType().Name}: {ex.Message}). Free-tier budgets will be unavailable.");
            _byProvider.Clear();
        }
    }

    private static FreeTierEntry? ParseEntry(JsonElement p, string path)
    {
        if (!p.TryGetProperty("provider", out JsonElement providerEl) || providerEl.ValueKind != JsonValueKind.String)
        {
            Console.WriteLine($"[FREE-TIER] Skipped an entry in '{path}' with no 'provider' field.");
            return null;
        }

        string provider = providerEl.GetString()!;
        string rawType = GetString(p, "free_type") ?? "none";
        FreeTierType type = rawType switch
        {
            "recurring-daily" => FreeTierType.RecurringDaily,
            "recurring-monthly" => FreeTierType.RecurringMonthly,
            "recurring-uncapped" => FreeTierType.RecurringUncapped,
            "one-time-credit" => FreeTierType.OneTimeCredit,
            "none" => FreeTierType.None,
            _ => FreeTierType.None
        };

        if (type == FreeTierType.None && rawType != "none")
            Console.WriteLine($"[FREE-TIER] Provider '{provider}' in '{path}' has an unknown free_type '{rawType}'; treated as 'none'.");

        return new FreeTierEntry(
            provider,
            GetString(p, "pool_key"),
            type,
            GetLong(p, "monthly_tokens"),
            GetLong(p, "daily_tokens"),
            GetLong(p, "credit_tokens"),
            (int?)GetLong(p, "requests_per_minute"),
            (int?)GetLong(p, "requests_per_day"),
            GetString(p, "tos") ?? "unknown",
            GetString(p, "tos_note"),
            GetString(p, "signup_url"),
            GetString(p, "source_url"),
            GetString(p, "verified_at"),
            GetString(p, "notes"));
    }

    internal FreeTierEntry? Get(string provider) =>
        _byProvider.TryGetValue(provider, out FreeTierEntry? entry) ? entry : null;

    /// <summary>
    /// The steady monthly allowance across the given providers, with each shared pool counted
    /// once at its largest member. Uncapped and one-time entries are excluded and reported
    /// separately by the caller.
    /// </summary>
    internal long SteadyMonthlyTokens(IEnumerable<string> providers, int daysInMonth)
    {
        var poolMax = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long loose = 0;

        foreach (string name in providers)
        {
            FreeTierEntry? entry = Get(name);
            if (entry?.MonthlyTokenEquivalent(daysInMonth) is not { } tokens || tokens <= 0)
                continue;

            if (entry.PoolKey is { } pool)
                poolMax[pool] = Math.Max(poolMax.TryGetValue(pool, out long existing) ? existing : 0, tokens);
            else
                loose += tokens;
        }

        return loose + poolMax.Values.Sum();
    }

    internal long SignupCreditTokens(IEnumerable<string> providers) =>
        providers.Select(Get)
                 .Where(e => e is { FreeType: FreeTierType.OneTimeCredit, CreditTokens: > 0 })
                 .Sum(e => e!.CreditTokens!.Value);

    internal IReadOnlyList<string> UncappedProviders(IEnumerable<string> providers) =>
        [.. providers.Where(p => Get(p)?.FreeType == FreeTierType.RecurringUncapped)];

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;

    private static string? FindCatalog()
    {
        foreach (string root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(root);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "config", "free-tier", "catalog.json");
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
        }
        return null;
    }
}
