namespace ProxyTests;

/// <summary>
/// The rollup is what makes a monthly free-tier budget survive a restart, so the cases that
/// matter are: does it round-trip, does it aggregate the right window, and does a damaged file
/// degrade instead of taking the proxy down at boot.
///
/// Every test uses its own temp directory — a test that wrote the developer's real rollup would
/// silently corrupt their quota figures.
/// </summary>
public sealed class UsageRollupStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ai-proxy-hub-tests", Guid.NewGuid().ToString("N"));

    private UsageRollupStore Create(DateTimeOffset? now = null) =>
        new(clock: () => now ?? new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero), dataDirectory: _dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Usage_SurvivesARestart()
    {
        var first = Create();
        first.RecordRequest("groq", 1000, 500, 0.01);
        first.RecordRequest("groq", 200, 100, 0.002);
        first.Flush();

        // A brand-new store over the same directory is what a restart looks like.
        var second = Create();
        var month = second.CurrentMonthByProvider();

        Assert.True(month.ContainsKey("groq"));
        Assert.Equal(2, month["groq"].Requests);
        Assert.Equal(1200, month["groq"].PromptTokens);
        Assert.Equal(600, month["groq"].CompletionTokens);
        Assert.Equal(1800, month["groq"].TotalTokens);
        Assert.Equal(0.012, month["groq"].CostUsd, precision: 6);
    }

    [Fact]
    public void Flush_IsAtomic_AndLeavesNoTempFile()
    {
        var store = Create();
        store.RecordRequest("groq", 10, 5, 0);
        store.Flush();

        Assert.True(File.Exists(store.FilePath));
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void CorruptFile_StartsEmpty_AndDoesNotThrow()
    {
        var store = Create();
        store.RecordRequest("groq", 10, 5, 0);
        store.Flush();

        File.WriteAllText(store.FilePath!, "{ this is not json");

        // A damaged statistics file must never stop the proxy from booting.
        var recovered = Create();
        Assert.Empty(recovered.CurrentMonthByProvider());

        // And it recovers: new usage still records and persists.
        recovered.RecordRequest("nvidia", 7, 3, 0);
        recovered.Flush();
        Assert.Equal(10, Create().TotalTokensThisMonth());
    }

    [Fact]
    public void MonthTotals_ExcludeOtherMonths()
    {
        var july = Create(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));
        july.RecordRequest("groq", 1000, 0, 0);
        july.Flush();

        var august = Create(new DateTimeOffset(2026, 8, 1, 0, 30, 0, TimeSpan.Zero));
        Assert.Equal(0, august.TotalTokensThisMonth());       // July does not leak into August

        august.RecordRequest("groq", 55, 0, 0);
        Assert.Equal(55, august.TotalTokensThisMonth());
    }

    [Fact]
    public void DayTotals_CoverOnlyToday()
    {
        var day1 = Create(new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero));
        day1.RecordRequest("groq", 100, 0, 0);
        day1.Flush();

        var day2 = Create(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));
        Assert.Empty(day2.CurrentDayByProvider());

        // Both days still count toward the month.
        day2.RecordRequest("groq", 40, 0, 0);
        Assert.Equal(40, day2.CurrentDayByProvider()["groq"].TotalTokens);
        Assert.Equal(140, day2.TotalTokensThisMonth());
    }

    [Fact]
    public void Failures_AreCountedByKind()
    {
        var store = Create();
        store.RecordFailure("groq", UpstreamFailureKind.RateLimit);
        store.RecordFailure("groq", UpstreamFailureKind.QuotaExhausted);
        store.RecordFailure("groq", UpstreamFailureKind.QuotaExhausted);
        store.RecordFailure("groq", UpstreamFailureKind.Transient);

        var today = store.CurrentDayByProvider()["groq"];
        Assert.Equal(4, today.Errors);
        Assert.Equal(1, today.RateLimited);
        Assert.Equal(2, today.QuotaExhausted);
    }

    [Fact]
    public void OldDays_ArePrunedOnWrite()
    {
        var old = Create(new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero));
        old.RecordRequest("groq", 999, 0, 0);
        old.Flush();

        // More than the 400-day retention later, the ancient day is dropped on the next write.
        var now = Create(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));
        now.RecordRequest("groq", 1, 0, 0);
        now.Flush();

        string json = File.ReadAllText(now.FilePath!);
        Assert.DoesNotContain("2024-01-01", json);
        Assert.Contains("2026-07-31", json);
    }

    [Fact]
    public void UnwritableDirectory_DegradesToMemory_WithoutThrowing()
    {
        // A path that cannot be a directory because a file already sits there.
        string filePath = Path.Combine(_dir, "not-a-dir");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(filePath, "x");

        var store = new UsageRollupStore(dataDirectory: filePath);

        Assert.False(store.IsPersistent);
        store.RecordRequest("groq", 10, 5, 0);       // still counts in memory
        store.Flush();                               // and is a no-op rather than a crash
        Assert.Equal(15, store.TotalTokensThisMonth());
    }

    [Fact]
    public void Flush_WithNoChanges_IsANoOp()
    {
        var store = Create();
        store.RecordRequest("groq", 1, 1, 0);
        store.Flush();
        DateTime firstWrite = File.GetLastWriteTimeUtc(store.FilePath!);

        store.Flush();   // nothing changed since
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(store.FilePath!));
    }
}
