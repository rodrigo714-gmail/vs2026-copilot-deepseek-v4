using System.Collections.Concurrent;

internal sealed class UsageTracker
{
    private readonly ConcurrentDictionary<string, ModelUsage> _stats = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _startedUtc = DateTime.UtcNow;

    internal void RecordSuccess(string provider, string model, double latencyMs, int promptTokens, int completionTokens, int? cachedTokens = null)
    {
        var pricing = PricingCatalog.Get(provider, model);
        decimal cost = pricing?.EstimateCost(promptTokens, completionTokens, cachedTokens) ?? 0m;
        string key = $"{provider}:{model}";
        _stats.AddOrUpdate(key, _ => NewUsage(promptTokens, completionTokens, cachedTokens, latencyMs, cost, 1, 0),
            (_, ex) => ex with { TotalRequests = ex.TotalRequests + 1, SuccessCount = ex.SuccessCount + 1, TotalPromptTokens = ex.TotalPromptTokens + promptTokens, TotalCompletionTokens = ex.TotalCompletionTokens + completionTokens, TotalCachedTokens = ex.TotalCachedTokens + (cachedTokens ?? 0), TotalCostUsd = ex.TotalCostUsd + cost, LatencySumMs = ex.LatencySumMs + latencyMs, LatencyMinMs = Math.Min(ex.LatencyMinMs, latencyMs), LatencyMaxMs = Math.Max(ex.LatencyMaxMs, latencyMs), LastSeenUtc = DateTime.UtcNow });
    }

    internal void RecordFailure(string provider, string model, double latencyMs, int statusCode)
    {
        string key = $"{provider}:{model}";
        _stats.AddOrUpdate(key, _ => new ModelUsage(TotalRequests: 1, SuccessCount: 0, FailCount: 1, TotalPromptTokens: 0, TotalCompletionTokens: 0, TotalCachedTokens: 0, TotalCostUsd: 0m, LatencySumMs: latencyMs, LatencyMinMs: latencyMs, LatencyMaxMs: latencyMs, FirstSeenUtc: DateTime.UtcNow, LastSeenUtc: DateTime.UtcNow),
            (_, ex) => ex with { TotalRequests = ex.TotalRequests + 1, FailCount = ex.FailCount + 1, LatencySumMs = ex.LatencySumMs + latencyMs, LatencyMinMs = Math.Min(ex.LatencyMinMs, latencyMs), LatencyMaxMs = Math.Max(ex.LatencyMaxMs, latencyMs), LastSeenUtc = DateTime.UtcNow });
    }

    internal UsageReport GetReport()
    {
        var models = new List<ModelReport>();
        decimal totalCost = 0;
        foreach ((string key, ModelUsage mu) in _stats)
        {
            int colon = key.IndexOf(':');
            string provider = colon > 0 ? key[..colon] : key;
            string model = colon > 0 && colon < key.Length - 1 ? key[(colon + 1)..] : key;
            var pricing = PricingCatalog.Get(provider, model);
            double avgLatency = mu.SuccessCount > 0 ? mu.LatencySumMs / mu.SuccessCount : mu.LatencySumMs / Math.Max(1, mu.TotalRequests);
            double successRate = mu.TotalRequests > 0 ? (double)mu.SuccessCount / mu.TotalRequests : 0;
            totalCost += mu.TotalCostUsd;
            models.Add(new ModelReport(provider, model, mu.TotalRequests, mu.SuccessCount, mu.FailCount, Math.Round(successRate * 100, 1), Math.Round(avgLatency, 0), mu.LatencyMinMs, mu.LatencyMaxMs, mu.TotalPromptTokens, mu.TotalCompletionTokens, mu.TotalCachedTokens, Math.Round(mu.TotalCostUsd, 6), pricing?.InputPerMillion ?? 0, pricing?.OutputPerMillion ?? 0, pricing?.CachedInputPerMillion, pricing?.ArenaElo, pricing?.ArenaWebDev, pricing?.ArenaAgentWinRate, pricing?.EstimatedTps, pricing?.Tier ?? "unknown", pricing?.Notes ?? ""));
        }
        return new UsageReport(_startedUtc, DateTime.UtcNow, Math.Round(totalCost, 4), models.OrderByDescending(m => m.TotalCostUsd).ThenBy(m => m.Provider).ToArray());
    }

    private static ModelUsage NewUsage(int pt, int ct, int? cached, double latMs, decimal cost, long s, long f) => new(TotalRequests: 1, SuccessCount: s, FailCount: f, TotalPromptTokens: pt, TotalCompletionTokens: ct, TotalCachedTokens: cached ?? 0, TotalCostUsd: cost, LatencySumMs: latMs, LatencyMinMs: latMs, LatencyMaxMs: latMs, FirstSeenUtc: DateTime.UtcNow, LastSeenUtc: DateTime.UtcNow);
    internal void Reset() { _stats.Clear(); _startedUtc = DateTime.UtcNow; }
}

internal readonly record struct ModelUsage(long TotalRequests, long SuccessCount, long FailCount, long TotalPromptTokens, long TotalCompletionTokens, long TotalCachedTokens, decimal TotalCostUsd, double LatencySumMs, double LatencyMinMs, double LatencyMaxMs, DateTime FirstSeenUtc, DateTime LastSeenUtc);

internal readonly record struct ModelReport(string Provider, string Model, long TotalRequests, long SuccessCount, long FailCount, double SuccessRatePct, double AvgLatencyMs, double MinLatencyMs, double MaxLatencyMs, long TotalPromptTokens, long TotalCompletionTokens, long TotalCachedTokens, decimal TotalCostUsd, decimal PriceInputPerM, decimal PriceOutputPerM, decimal? PriceCachedPerM, int? ArenaElo, int? ArenaWebDev, decimal? ArenaAgentWinRate, int? EstimatedTps, string Tier, string Notes);

internal readonly record struct UsageReport(DateTime SinceUtc, DateTime UntilUtc, decimal TotalCostUsd, ModelReport[] Models);
