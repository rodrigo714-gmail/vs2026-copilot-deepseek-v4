using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Per-day, per-provider totals. Kept flat so the file stays readable by hand.</summary>
internal sealed class ProviderDayUsage
{
    [JsonPropertyName("requests")] public long Requests { get; set; }
    [JsonPropertyName("prompt_tokens")] public long PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public long CompletionTokens { get; set; }
    [JsonPropertyName("errors")] public long Errors { get; set; }
    [JsonPropertyName("rate_limited")] public long RateLimited { get; set; }
    [JsonPropertyName("quota_exhausted")] public long QuotaExhausted { get; set; }
    [JsonPropertyName("cost_usd")] public double CostUsd { get; set; }

    [JsonIgnore] public long TotalTokens => PromptTokens + CompletionTokens;
}

internal sealed class UsageRollupFile
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("updated_at")] public string? UpdatedAt { get; set; }

    /// <summary>date (yyyy-MM-dd) → provider → totals.</summary>
    [JsonPropertyName("days")]
    public Dictionary<string, Dictionary<string, ProviderDayUsage>> Days { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A durable daily rollup of token usage, so a monthly free-tier budget still means something
/// after the proxy restarts.
///
/// Everything else in this process is in-memory and resets on restart, which is fine for latency
/// and RPM but useless for a quota that resets once a month. This keeps a small aggregate — one
/// row per (day, provider) — in a JSON file rather than a database: eleven providers over a year
/// is a few thousand rows with a single writer, which is far below the point where SQLite would
/// earn a native dependency in a project that currently has none.
///
/// A store that cannot write degrades to memory-only with a warning. Failing to persist usage
/// must never stop the proxy from serving requests.
/// </summary>
internal sealed class UsageRollupStore
{
    private const int RetentionDays = 400;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ProviderDayUsage>> _days =
        new(StringComparer.Ordinal);

    private readonly string? _filePath;
    private readonly object _writeLock = new();
    private readonly Func<DateTimeOffset> _clock;
    private int _dirty;

    internal string? FilePath => _filePath;
    internal bool IsPersistent => _filePath is not null;

    public UsageRollupStore(Func<DateTimeOffset>? clock = null, string? dataDirectory = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);

        string dir = dataDirectory
            ?? Environment.GetEnvironmentVariable("PROXY_DATA_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "data");

        try
        {
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "usage-rollup.json");
            Load();
        }
        catch (Exception ex)
        {
            // Read-only mounts and locked-down containers are normal. Carry on in memory.
            Console.WriteLine($"[USAGE] Usage rollup is memory-only: '{dir}' is not writable ({ex.GetType().Name}: {ex.Message})");
            _filePath = null;
        }
    }

    private string Today => _clock().ToString("yyyy-MM-dd");

    internal void RecordRequest(string provider, long promptTokens, long completionTokens, double costUsd)
    {
        Mutate(provider, u =>
        {
            u.Requests++;
            u.PromptTokens += promptTokens;
            u.CompletionTokens += completionTokens;
            u.CostUsd += costUsd;
        });
    }

    internal void RecordFailure(string provider, UpstreamFailureKind kind)
    {
        Mutate(provider, u =>
        {
            u.Errors++;
            if (kind == UpstreamFailureKind.RateLimit) u.RateLimited++;
            if (kind == UpstreamFailureKind.QuotaExhausted) u.QuotaExhausted++;
        });
    }

    private void Mutate(string provider, Action<ProviderDayUsage> apply)
    {
        var day = _days.GetOrAdd(Today, _ => new ConcurrentDictionary<string, ProviderDayUsage>(StringComparer.OrdinalIgnoreCase));
        var usage = day.GetOrAdd(provider, _ => new ProviderDayUsage());

        lock (usage)
        {
            apply(usage);
        }
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>Totals for the current calendar month, per provider.</summary>
    internal IReadOnlyDictionary<string, ProviderDayUsage> CurrentMonthByProvider()
    {
        string prefix = _clock().ToString("yyyy-MM");
        var totals = new Dictionary<string, ProviderDayUsage>(StringComparer.OrdinalIgnoreCase);

        foreach ((string date, var providers) in _days)
        {
            if (!date.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            foreach ((string provider, ProviderDayUsage usage) in providers)
            {
                if (!totals.TryGetValue(provider, out ProviderDayUsage? sum))
                    totals[provider] = sum = new ProviderDayUsage();

                lock (usage)
                {
                    sum.Requests += usage.Requests;
                    sum.PromptTokens += usage.PromptTokens;
                    sum.CompletionTokens += usage.CompletionTokens;
                    sum.Errors += usage.Errors;
                    sum.RateLimited += usage.RateLimited;
                    sum.QuotaExhausted += usage.QuotaExhausted;
                    sum.CostUsd += usage.CostUsd;
                }
            }
        }
        return totals;
    }

    /// <summary>Totals for the current UTC-local day, per provider — for daily free-tier budgets.</summary>
    internal IReadOnlyDictionary<string, ProviderDayUsage> CurrentDayByProvider()
    {
        if (!_days.TryGetValue(Today, out var today))
            return new Dictionary<string, ProviderDayUsage>(StringComparer.OrdinalIgnoreCase);

        var copy = new Dictionary<string, ProviderDayUsage>(StringComparer.OrdinalIgnoreCase);
        foreach ((string provider, ProviderDayUsage usage) in today)
        {
            lock (usage)
            {
                copy[provider] = new ProviderDayUsage
                {
                    Requests = usage.Requests,
                    PromptTokens = usage.PromptTokens,
                    CompletionTokens = usage.CompletionTokens,
                    Errors = usage.Errors,
                    RateLimited = usage.RateLimited,
                    QuotaExhausted = usage.QuotaExhausted,
                    CostUsd = usage.CostUsd
                };
            }
        }
        return copy;
    }

    internal long TotalTokensThisMonth() => CurrentMonthByProvider().Values.Sum(u => u.TotalTokens);

    /// <summary>
    /// Writes the rollup if anything changed. Atomic: the file is written to a sibling
    /// <c>.tmp</c> and moved into place, so a crash mid-write cannot leave a half-written file
    /// that would fail to parse on the next start.
    /// </summary>
    internal void Flush(bool force = false)
    {
        if (_filePath is null) return;
        if (Interlocked.Exchange(ref _dirty, 0) == 0 && !force) return;

        try
        {
            Prune();

            var file = new UsageRollupFile { UpdatedAt = _clock().UtcDateTime.ToString("o") };
            foreach ((string date, var providers) in _days.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var perProvider = new Dictionary<string, ProviderDayUsage>(StringComparer.OrdinalIgnoreCase);
                foreach ((string provider, ProviderDayUsage usage) in providers)
                {
                    lock (usage)
                    {
                        perProvider[provider] = new ProviderDayUsage
                        {
                            Requests = usage.Requests,
                            PromptTokens = usage.PromptTokens,
                            CompletionTokens = usage.CompletionTokens,
                            Errors = usage.Errors,
                            RateLimited = usage.RateLimited,
                            QuotaExhausted = usage.QuotaExhausted,
                            CostUsd = usage.CostUsd
                        };
                    }
                }
                file.Days[date] = perProvider;
            }

            lock (_writeLock)
            {
                string tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, RollupJson));
                File.Move(tmp, _filePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[USAGE] Failed to write the usage rollup: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Load()
    {
        if (_filePath is null || !File.Exists(_filePath))
            return;

        try
        {
            UsageRollupFile? file = JsonSerializer.Deserialize<UsageRollupFile>(File.ReadAllText(_filePath), RollupJson);
            if (file?.Days is null)
                return;

            foreach ((string date, var providers) in file.Days)
            {
                var day = _days.GetOrAdd(date, _ => new ConcurrentDictionary<string, ProviderDayUsage>(StringComparer.OrdinalIgnoreCase));
                foreach ((string provider, ProviderDayUsage usage) in providers)
                    day[provider] = usage;
            }
        }
        catch (Exception ex)
        {
            // A corrupt rollup is a lost statistic, never a failed startup. Say so loudly, then
            // start from empty rather than crashing the proxy on boot.
            Console.WriteLine($"[USAGE] Could not read '{_filePath}' ({ex.GetType().Name}: {ex.Message}). Starting from an empty rollup.");
            _days.Clear();
        }
    }

    private void Prune()
    {
        string cutoff = _clock().AddDays(-RetentionDays).ToString("yyyy-MM-dd");
        foreach (string date in _days.Keys)
        {
            if (string.CompareOrdinal(date, cutoff) < 0)
                _days.TryRemove(date, out _);
        }
    }

    private static readonly JsonSerializerOptions RollupJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}
