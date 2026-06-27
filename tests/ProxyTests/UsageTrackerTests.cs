namespace ProxyTests;

public sealed class UsageTrackerTests
{
    [Fact]
    public void RecordSuccess_TracksTokensAndCost()
    {
        var tracker = new UsageTracker();
        tracker.RecordSuccess("deepseek", "deepseek-v4-flash", 1500, 5000, 1000);
        var report = tracker.GetReport();
        Assert.Single(report.Models);
        var m = report.Models[0];
        Assert.Equal("deepseek", m.Provider);
        Assert.Equal("deepseek-v4-flash", m.Model);
        Assert.Equal(1, m.TotalRequests);
        Assert.Equal(5000, m.TotalPromptTokens);
        Assert.Equal(1000, m.TotalCompletionTokens);
        Assert.True(m.TotalCostUsd > 0);
    }

    [Fact]
    public void RecordSuccess_MultipleCalls_Aggregates()
    {
        var tracker = new UsageTracker();
        tracker.RecordSuccess("zai", "glm-5.2", 1000, 2000, 500);
        tracker.RecordSuccess("zai", "glm-5.2", 2000, 3000, 800);
        var report = tracker.GetReport();
        var m = report.Models[0];
        Assert.Equal(2, m.TotalRequests);
        Assert.Equal(5000, m.TotalPromptTokens);
        Assert.Equal(1300, m.TotalCompletionTokens);
        Assert.Equal(1000, m.AvgLatencyMs, 1);
    }

    [Fact]
    public void RecordFailure_TracksFailures()
    {
        var tracker = new UsageTracker();
        tracker.RecordFailure("moonshot", "kimi-k2.7-code", 500, 429);
        var report = tracker.GetReport();
        var m = report.Models[0];
        Assert.Equal(1, m.TotalRequests);
        Assert.Equal(0, m.SuccessCount);
        Assert.Equal(1, m.FailCount);
        Assert.Equal(0, m.SuccessRatePct);
    }

    [Fact]
    public void Reset_ClearsStats()
    {
        var tracker = new UsageTracker();
        tracker.RecordSuccess("deepseek", "deepseek-v4-pro", 100, 1000, 200);
        tracker.Reset();
        var report = tracker.GetReport();
        Assert.Empty(report.Models);
        Assert.Equal(0, report.TotalCostUsd);
    }

    [Fact]
    public void FreeTier_CostIsZero()
    {
        var tracker = new UsageTracker();
        tracker.RecordSuccess("zai", "glm-4.7-flash", 300, 5000, 1000);
        var report = tracker.GetReport();
        var m = report.Models[0];
        Assert.Equal(0, m.TotalCostUsd);
        Assert.Equal("free", m.Tier);
    }
}
