using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>
/// Thread-safe singleton that tracks per-provider usage stats (tokens, requests, rate-limits, errors).
/// Automatically registers new providers on first request — no need to pre-configure.
/// </summary>
internal sealed class UsageTrackerService
{
    private readonly ConcurrentDictionary<string, ProviderUsageStats> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<UsageSnapshot> _snapshots = new();
    private readonly TimeSpan _snapshotRetention = TimeSpan.FromHours(24);
    private readonly TimeSpan _snapshotInterval = TimeSpan.FromSeconds(60);
    private DateTime _lastSnapshotTime = DateTime.UtcNow;
    private readonly object _snapshotLock = new();

    /// <summary>
    /// Records a successful request, accumulating tokens and updating rate-limit headers.
    /// </summary>
    internal void RecordRequest(
        string providerName,
        long promptTokens,
        long completionTokens,
        long totalTokens,
        Dictionary<string, string?>? responseHeaders = null)
    {
        var stats = _stats.GetOrAdd(providerName, _ => new ProviderUsageStats(providerName));

        lock (stats.Lock)
        {
            stats.TotalRequests++;
            stats.TotalPromptTokens += promptTokens;
            stats.TotalCompletionTokens += completionTokens;
            stats.TotalTokens += totalTokens;
            stats.LastRequestTime = DateTime.UtcNow;
            stats.LastRequestSuccess = true;
            stats.LastErrorMessage = null;

            // Parse rate-limit headers from upstream response
            if (responseHeaders is not null)
            {
                ParseRateLimitHeaders(stats, responseHeaders);
            }
        }

        TryTakeSnapshot();
    }

    /// <summary>
    /// Records a failed request (upstream error).
    /// </summary>
    internal void RecordError(string providerName, string? errorMessage, string? statusCode = null)
    {
        var stats = _stats.GetOrAdd(providerName, _ => new ProviderUsageStats(providerName));

        lock (stats.Lock)
        {
            stats.TotalRequests++;
            stats.LastRequestTime = DateTime.UtcNow;
            stats.LastRequestSuccess = false;
            stats.LastErrorMessage = errorMessage;
            stats.LastErrorTime = DateTime.UtcNow;
            stats.LastErrorStatusCode = statusCode;
        }

        TryTakeSnapshot();
    }

    /// <summary>
    /// Records rate-limit headers separately (useful when the response body is streamed
    /// and we only have headers available at the call site).
    /// </summary>
    internal void RecordRateLimitHeaders(string providerName, Dictionary<string, string?> headers)
    {
        var stats = _stats.GetOrAdd(providerName, _ => new ProviderUsageStats(providerName));
        lock (stats.Lock)
        {
            ParseRateLimitHeaders(stats, headers);
        }
    }

    /// <summary>
    /// Gets a snapshot of all provider stats plus aggregated totals.
    /// </summary>
    internal DashboardData GetAllStats()
    {
        // Start with all configured providers from ProviderRegistry
        var allProviderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add providers that have recorded stats
        foreach (string name in _stats.Keys)
        {
            allProviderNames.Add(name);
        }

        // Also include providers from the registry (even if no requests yet)
        // The call site should pass in known providers from ProviderRegistry.

        long totalRequests = 0;
        long totalPromptTokens = 0;
        long totalCompletionTokens = 0;
        long totalTokens = 0;

        var providers = new List<ProviderStatsEntry>();
        foreach (string name in allProviderNames.OrderBy(n => n))
        {
            if (_stats.TryGetValue(name, out var stats))
            {
                lock (stats.Lock)
                {
                    totalRequests += stats.TotalRequests;
                    totalPromptTokens += stats.TotalPromptTokens;
                    totalCompletionTokens += stats.TotalCompletionTokens;
                    totalTokens += stats.TotalTokens;

                    providers.Add(new ProviderStatsEntry
                    {
                        Name = name,
                        DisplayName = FormatDisplayName(name),
                        Stats = stats.Clone()
                    });
                }
            }
        }

        var recentSnapshots = _snapshots.ToArray();

        return new DashboardData
        {
            Providers = providers,
            Totals = new AggregateTotals
            {
                TotalRequests = totalRequests,
                TotalPromptTokens = totalPromptTokens,
                TotalCompletionTokens = totalCompletionTokens,
                TotalTokens = totalTokens
            },
            Snapshots = recentSnapshots
        };
    }

    /// <summary>
    /// Gets detailed stats for a single provider.
    /// </summary>
    internal ProviderUsageStats? GetProviderStats(string providerName)
    {
        return _stats.TryGetValue(providerName, out var stats) ? stats.Clone() : null;
    }

    /// <summary>
    /// Called periodically (e.g. by UsageSnapshotService) or on-demand after bursts.
    /// </summary>
    internal void TryTakeSnapshot()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - _lastSnapshotTime) < _snapshotInterval)
            return;

        lock (_snapshotLock)
        {
            if ((now - _lastSnapshotTime) < _snapshotInterval)
                return;

            _lastSnapshotTime = now;

            var point = new UsageSnapshot
            {
                Timestamp = now,
                ProviderTokens = _stats.ToDictionary(
                    kv => kv.Key,
                    kv =>
                    {
                        lock (kv.Value.Lock) { return kv.Value.TotalTokens; }
                    },
                    StringComparer.OrdinalIgnoreCase)
            };

            _snapshots.Enqueue(point);

            // Prune old entries
            DateTime cutoff = now - _snapshotRetention;
            while (_snapshots.TryPeek(out var oldest) && oldest.Timestamp < cutoff)
            {
                _snapshots.TryDequeue(out _);
            }
        }
    }

    /// <summary>
    /// Force-enumerate all known provider names (including those with zero stats).
    /// </summary>
    internal IEnumerable<string> KnownProviderNames => _stats.Keys;

    /// <summary>
    /// Ensure a provider is tracked even before the first request (e.g. configured but unused).
    /// </summary>
    internal void EnsureProvider(string providerName)
    {
        _stats.GetOrAdd(providerName, _ => new ProviderUsageStats(providerName));
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static string FormatDisplayName(string raw)
    {
        return raw switch
        {
            "deepseek" => "DeepSeek",
            "openai" => "OpenAI",
            "nvidia" => "NVIDIA NIM",
            "groq" => "Groq",
            "openrouter" => "OpenRouter",
            "moonshot" => "Kimi/Moonshot",
            "cerebras" => "Cerebras",
            "zenmux" => "ZenMux",
            "ollama" => "Ollama Cloud",
            "google" => "Google Gemini",
            _ => char.ToUpper(raw[0]) + raw[1..]
        };
    }

    private static void ParseRateLimitHeaders(ProviderUsageStats stats, Dictionary<string, string?> headers)
    {
        foreach (var kv in headers)
        {
            string key = kv.Key.ToLowerInvariant();
            string? val = kv.Value;

            if (string.IsNullOrEmpty(val))
                continue;

            // OpenAI / DeepSeek / Groq style
            if (key == "x-ratelimit-limit-requests" && int.TryParse(val, out int limitRequests))
                stats.RateLimitRequests = limitRequests;
            else if (key == "x-ratelimit-remaining-requests" && int.TryParse(val, out int remainingRequests))
                stats.RateLimitRemainingRequests = remainingRequests;
            else if (key == "x-ratelimit-limit-tokens" && int.TryParse(val, out int limitTokens))
                stats.RateLimitTokens = limitTokens;
            else if (key == "x-ratelimit-remaining-tokens" && int.TryParse(val, out int remainingTokens))
                stats.RateLimitRemainingTokens = remainingTokens;
            else if (key == "x-ratelimit-reset-requests")
            {
                // Can be seconds (number) or epoch timestamp or ISO date
                if (long.TryParse(val, out long resetSeconds))
                {
                    // If the value is small (< 1 year in seconds), treat as seconds from now
                    if (resetSeconds < 31_536_000)
                        stats.RateLimitReset = DateTime.UtcNow.AddSeconds(resetSeconds);
                    else
                        stats.RateLimitReset = DateTimeOffset.FromUnixTimeSeconds(resetSeconds).UtcDateTime;
                }
                else if (DateTime.TryParse(val, out DateTime resetDate))
                {
                    stats.RateLimitReset = resetDate.ToUniversalTime();
                }
            }
            // OpenRouter credits
            else if (key == "x-credits-remaining" && double.TryParse(val, out double credits))
            {
                stats.CreditsRemaining = credits;
            }
        }
    }
}

// ── Data model types ─────────────────────────────────────────────────────────

internal sealed class ProviderUsageStats
{
    public string ProviderName { get; }
    internal readonly object Lock = new();

    // Tokens
    public long TotalRequests;
    public long TotalPromptTokens;
    public long TotalCompletionTokens;
    public long TotalTokens;

    // Rate limits
    public int? RateLimitRequests;
    public int? RateLimitRemainingRequests;
    public int? RateLimitTokens;
    public int? RateLimitRemainingTokens;
    public DateTime? RateLimitReset;

    // OpenRouter
    public double? CreditsRemaining;

    // Errors / status
    public DateTime? LastRequestTime;
    public bool LastRequestSuccess = true;
    public string? LastErrorMessage;
    public DateTime? LastErrorTime;
    public string? LastErrorStatusCode;

    internal ProviderUsageStats(string providerName)
    {
        ProviderName = providerName;
    }

    /// <summary>Creates a thread-safe snapshot clone of this instance.</summary>
    internal ProviderUsageStats Clone()
    {
        lock (Lock)
        {
            return new ProviderUsageStats(ProviderName)
            {
                TotalRequests = TotalRequests,
                TotalPromptTokens = TotalPromptTokens,
                TotalCompletionTokens = TotalCompletionTokens,
                TotalTokens = TotalTokens,
                RateLimitRequests = RateLimitRequests,
                RateLimitRemainingRequests = RateLimitRemainingRequests,
                RateLimitTokens = RateLimitTokens,
                RateLimitRemainingTokens = RateLimitRemainingTokens,
                RateLimitReset = RateLimitReset,
                CreditsRemaining = CreditsRemaining,
                LastRequestTime = LastRequestTime,
                LastRequestSuccess = LastRequestSuccess,
                LastErrorMessage = LastErrorMessage,
                LastErrorTime = LastErrorTime,
                LastErrorStatusCode = LastErrorStatusCode
            };
        }
    }
}

internal sealed record ProviderStatsEntry
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required ProviderUsageStats Stats { get; init; }
}

internal sealed record AggregateTotals
{
    public long TotalRequests { get; init; }
    public long TotalPromptTokens { get; init; }
    public long TotalCompletionTokens { get; init; }
    public long TotalTokens { get; init; }
}

internal sealed record UsageSnapshot
{
    public required DateTime Timestamp { get; init; }
    public required Dictionary<string, long> ProviderTokens { get; init; }
}

internal sealed record DashboardData
{
    public required List<ProviderStatsEntry> Providers { get; init; }
    public required AggregateTotals Totals { get; init; }
    public required UsageSnapshot[] Snapshots { get; init; }
}