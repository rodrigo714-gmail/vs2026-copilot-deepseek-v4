namespace ProxyTests;

/// <summary>
/// The accounting rules are the point of this catalog: a shared pool counted once, an uncapped
/// tier listed but never summed, and a signup credit kept out of the recurring figure. Getting
/// any of those wrong is how free-tier totals get inflated several-fold.
/// </summary>
public sealed class FreeTierCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ai-proxy-hub-tests", Guid.NewGuid().ToString("N"));

    private FreeTierCatalogStore FromJson(string json)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "catalog.json");
        File.WriteAllText(path, json);
        return new FreeTierCatalogStore(path);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const int Days = 31;

    [Fact]
    public void SharedPool_IsCountedOnce_AtItsLargestMember()
    {
        // Six Gemini Flash variants served out of one budget. Summing them separately is the
        // mistake that turns 60M into 360M.
        var catalog = FromJson("""
            {
              "schema_version": 1, "curated_at": "2026-07-31",
              "providers": [
                {"provider":"a","pool_key":"shared","free_type":"recurring-monthly","monthly_tokens":60000000},
                {"provider":"b","pool_key":"shared","free_type":"recurring-monthly","monthly_tokens":40000000},
                {"provider":"c","pool_key":"shared","free_type":"recurring-monthly","monthly_tokens":10000000}
              ]
            }
            """);

        Assert.Equal(60_000_000, catalog.SteadyMonthlyTokens(["a", "b", "c"], Days));
    }

    [Fact]
    public void IndependentProviders_AreSummed()
    {
        var catalog = FromJson("""
            {
              "schema_version": 1, "curated_at": "2026-07-31",
              "providers": [
                {"provider":"a","pool_key":"pool-a","free_type":"recurring-monthly","monthly_tokens":1000},
                {"provider":"b","pool_key":"pool-b","free_type":"recurring-monthly","monthly_tokens":2000}
              ]
            }
            """);

        Assert.Equal(3000, catalog.SteadyMonthlyTokens(["a", "b"], Days));
    }

    [Fact]
    public void DailyAllowance_IsScaledByTheLengthOfTheMonth()
    {
        var catalog = FromJson("""
            {
              "schema_version": 1, "curated_at": "2026-07-31",
              "providers": [{"provider":"a","free_type":"recurring-daily","daily_tokens":1000}]
            }
            """);

        Assert.Equal(31_000, catalog.SteadyMonthlyTokens(["a"], 31));
        Assert.Equal(28_000, catalog.SteadyMonthlyTokens(["a"], 28));
    }

    [Fact]
    public void UncappedProviders_AreListedButNeverSummed()
    {
        // A provider with only a rate limit has real value that cannot honestly be expressed as
        // tokens per month; RPM x 24/7 would be a ceiling nobody reaches.
        var catalog = FromJson("""
            {
              "schema_version": 1, "curated_at": "2026-07-31",
              "providers": [
                {"provider":"capped","free_type":"recurring-monthly","monthly_tokens":5000},
                {"provider":"uncapped","free_type":"recurring-uncapped","requests_per_minute":30}
              ]
            }
            """);

        Assert.Equal(5000, catalog.SteadyMonthlyTokens(["capped", "uncapped"], Days));
        Assert.Equal(["uncapped"], catalog.UncappedProviders(["capped", "uncapped"]));
    }

    [Fact]
    public void SignupCredits_AreKeptOutOfTheRecurringTotal()
    {
        var catalog = FromJson("""
            {
              "schema_version": 1, "curated_at": "2026-07-31",
              "providers": [
                {"provider":"recurring","free_type":"recurring-monthly","monthly_tokens":1000},
                {"provider":"signup","free_type":"one-time-credit","credit_tokens":5000000}
              ]
            }
            """);

        Assert.Equal(1000, catalog.SteadyMonthlyTokens(["recurring", "signup"], Days));
        Assert.Equal(5_000_000, catalog.SignupCreditTokens(["recurring", "signup"]));
    }

    [Fact]
    public void UnconfiguredProviders_DoNotContribute()
    {
        var catalog = FromJson("""
            {
              "schema_version": 1, "curated_at": "2026-07-31",
              "providers": [
                {"provider":"configured","free_type":"recurring-monthly","monthly_tokens":1000},
                {"provider":"not-configured","free_type":"recurring-monthly","monthly_tokens":9000}
              ]
            }
            """);

        // Only providers with an API key present should count toward the available budget.
        Assert.Equal(1000, catalog.SteadyMonthlyTokens(["configured"], Days));
    }

    [Fact]
    public void MalformedCatalog_YieldsNoBudget_RatherThanWrongNumbers()
    {
        var catalog = FromJson("{ not json at all");

        Assert.Empty(catalog.Entries);
        Assert.Equal(0, catalog.SteadyMonthlyTokens(["a"], Days));
        Assert.Null(catalog.Get("a"));
    }

    [Fact]
    public void MissingFile_IsHandled()
    {
        var catalog = new FreeTierCatalogStore(Path.Combine(_dir, "does-not-exist.json"));
        Assert.Empty(catalog.Entries);
    }

    [Fact]
    public void TosAndMetadata_AreExposed()
    {
        // The privacy/terms cost of a free tier belongs next to the quota, not buried.
        var catalog = FromJson("""
            {
              "schema_version": 1, "curated_at": "2026-07-31",
              "providers": [{
                "provider":"a","free_type":"recurring-monthly","monthly_tokens":1,
                "tos":"caution","tos_note":"personal use only","signup_url":"https://example.test",
                "requests_per_minute":2,"verified_at":"2026-07-31"
              }]
            }
            """);

        FreeTierEntry? entry = catalog.Get("a");
        Assert.NotNull(entry);
        Assert.Equal("caution", entry.Tos);
        Assert.Equal("personal use only", entry.TosNote);
        Assert.Equal(2, entry.RequestsPerMinute);
        Assert.Equal("2026-07-31", entry.VerifiedAt);
        Assert.Equal("2026-07-31", catalog.CuratedAt);
    }

    // ── The catalog that actually ships ──────────────────────────────────────

    [Fact]
    public void ShippedCatalog_ParsesAndCoversEveryRegisteredProvider()
    {
        var catalog = new FreeTierCatalogStore();
        Assert.NotEmpty(catalog.Entries);

        // Every provider the proxy can route must have a free-tier verdict, even if that verdict
        // is "none" — an unlisted provider silently reports no quota at all in the dashboard.
        foreach (string provider in ProviderCapabilitiesRegistry.KnownProviders)
        {
            Assert.True(catalog.Get(provider) is not null,
                $"Provider '{provider}' is missing from config/free-tier/catalog.json.");
        }
    }

    [Fact]
    public void ShippedCatalog_HasNoEntriesForUnknownProviders()
    {
        var catalog = new FreeTierCatalogStore();

        foreach (FreeTierEntry entry in catalog.Entries)
        {
            Assert.True(ProviderCapabilitiesRegistry.IsKnownProvider(entry.Provider),
                $"config/free-tier/catalog.json lists '{entry.Provider}', which is not a registered provider.");
        }
    }

    [Fact]
    public void ShippedCatalog_RecordsWhenEachFigureWasVerified()
    {
        // Free tiers change constantly; a figure with no date is a figure nobody can trust.
        foreach (FreeTierEntry entry in new FreeTierCatalogStore().Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.VerifiedAt),
                $"Provider '{entry.Provider}' has no verified_at date.");
        }
    }
}
