using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>
/// Fetches billing/usage/quota data directly from each provider's dedicated API.
/// Runs on first access (lazy) and caches results with a configurable TTL.
/// </summary>
internal sealed class ProviderBillingService
{
    private readonly ProviderRegistry _providerRegistry;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, ProviderBillingInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);

    public ProviderBillingService(ProviderRegistry providerRegistry)
    {
        _providerRegistry = providerRegistry;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Returns billing/usage/quota info for all configured providers.
    /// </summary>
    internal async Task<Dictionary<string, ProviderBillingInfo>> GetAllBillingInfo()
    {
        var results = new Dictionary<string, ProviderBillingInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _providerRegistry.Providers)
        {
            var cached = _cache.GetOrAdd(provider.Name, _ => new ProviderBillingInfo { ProviderName = provider.Name });

            // Refresh cache if stale
            if (DateTime.UtcNow - cached.LastFetched > _cacheTtl)
            {
                try
                {
                    var fresh = await FetchBillingForProvider(provider);
                    if (fresh is not null)
                    {
                        fresh.LastFetched = DateTime.UtcNow;
                        _cache[provider.Name] = fresh;
                        results[provider.Name] = fresh;
                    }
                    else
                    {
                        results[provider.Name] = cached;
                    }
                }
                catch
                {
                    results[provider.Name] = cached;
                }
            }
            else
            {
                results[provider.Name] = cached;
            }
        }

        return results;
    }

    /// <summary>
    /// Returns billing info for a single provider (with cache lookup).
    /// </summary>
    internal async Task<ProviderBillingInfo?> GetBillingInfo(string providerName)
    {
        var provider = _providerRegistry.Providers.FirstOrDefault(p =>
            string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(provider.Name)) return null;

        if (_cache.TryGetValue(providerName, out var cached) && DateTime.UtcNow - cached.LastFetched <= _cacheTtl)
            return cached;

        var fresh = await FetchBillingForProvider(provider);
        if (fresh is not null)
        {
            fresh.LastFetched = DateTime.UtcNow;
            _cache[providerName] = fresh;
        }
        return fresh ?? cached;
    }

    private async Task<ProviderBillingInfo?> FetchBillingForProvider(ProviderInfo provider)
    {
        return provider.Name.ToLowerInvariant() switch
        {
            "deepseek" => await FetchDeepSeekBilling(provider),
            "openai" => await FetchOpenAiBilling(provider),
            "openrouter" => await FetchOpenRouterBilling(provider),
            "nvidia" => await FetchNvidiaBilling(provider),
            "groq" => await FetchGroqBilling(provider),
            "moonshot" => await FetchMoonshotBilling(provider),
            "cerebras" => await FetchCerebrasBilling(provider),
            "zenmux" => await FetchZenMuxBilling(provider),
            "ollama" => await FetchOllamaBilling(provider),
            "google" => await FetchGoogleBilling(provider),
            _ => null
        };
    }

    // ── Provider-specific billing API calls ──────────────────────────

    private async Task<ProviderBillingInfo?> FetchDeepSeekBilling(ProviderInfo provider)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var info = new ProviderBillingInfo
        {
            ProviderName = "deepseek",
            HasBillingApi = true,
            RawData = json
        };

        if (root.TryGetProperty("is_available", out var avail))
            info.IsAvailable = avail.GetBoolean();

        if (root.TryGetProperty("balance_infos", out var balances) && balances.ValueKind == JsonValueKind.Array)
        {
            var list = new List<BalanceEntry>();
            foreach (var b in balances.EnumerateArray())
            {
                var entry = new BalanceEntry();
                if (b.TryGetProperty("currency", out var cur)) entry.Currency = cur.GetString() ?? "USD";
                if (b.TryGetProperty("total_balance", out var tot)) entry.TotalBalance = decimal.TryParse(tot.GetString(), out var d) ? d : 0;
                if (b.TryGetProperty("granted_balance", out var gr)) entry.GrantedBalance = decimal.TryParse(gr.GetString(), out var d2) ? d2 : 0;
                if (b.TryGetProperty("topped_up_balance", out var tp)) entry.ToppedUpBalance = decimal.TryParse(tp.GetString(), out var d3) ? d3 : 0;
                list.Add(entry);
            }
            info.Balances = list;
        }

        return info;
    }

    private async Task<ProviderBillingInfo?> FetchOpenAiBilling(ProviderInfo provider)
    {
        var info = new ProviderBillingInfo
        {
            ProviderName = "openai",
            HasBillingApi = true
        };

        try
        {
            var subRequest = new HttpRequestMessage(HttpMethod.Get,
                "https://api.openai.com/v1/dashboard/billing/subscription");
            subRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
            var subResponse = await _httpClient.SendAsync(subRequest);
            if (subResponse.IsSuccessStatusCode)
            {
                var subJson = await subResponse.Content.ReadAsStringAsync();
                info.RawData = subJson;
                using var subDoc = JsonDocument.Parse(subJson);
                var subRoot = subDoc.RootElement;

                if (subRoot.TryGetProperty("hard_limit_usd", out var hl))
                    info.HardLimitUsd = hl.GetDecimal();
                if (subRoot.TryGetProperty("soft_limit_usd", out var sl))
                    info.SoftLimitUsd = sl.GetDecimal();
                if (subRoot.TryGetProperty("system_hard_limit_usd", out var shl))
                    info.SystemHardLimitUsd = shl.GetDecimal();
            }
        }
        catch { }

        try
        {
            var creditRequest = new HttpRequestMessage(HttpMethod.Get,
                "https://api.openai.com/v1/dashboard/billing/credit_grants");
            creditRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
            var creditResponse = await _httpClient.SendAsync(creditRequest);
            if (creditResponse.IsSuccessStatusCode)
            {
                var creditJson = await creditResponse.Content.ReadAsStringAsync();
                info.RawData = (info.RawData ?? "") + "\n---\n" + creditJson;
                using var creditDoc = JsonDocument.Parse(creditJson);
                var creditRoot = creditDoc.RootElement;

                if (creditRoot.TryGetProperty("total_granted", out var tg))
                    info.TotalGranted = tg.GetDecimal();
                if (creditRoot.TryGetProperty("total_used", out var tu))
                    info.TotalUsed = tu.GetDecimal();
                if (creditRoot.TryGetProperty("total_remaining", out var tr))
                    info.TotalRemaining = tr.GetDecimal();
            }
        }
        catch { }

        info.IsAvailable = info.TotalRemaining > 0 || info.HardLimitUsd > 0;
        return info;
    }

    private async Task<ProviderBillingInfo?> FetchOpenRouterBilling(ProviderInfo provider)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            "https://openrouter.ai/api/v1/auth/key");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var info = new ProviderBillingInfo
        {
            ProviderName = "openrouter",
            HasBillingApi = true,
            RawData = json
        };

        if (root.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("usage", out var usage))
                info.CreditsUsed = usage.GetDecimal();
            if (data.TryGetProperty("limit", out var limit))
                info.CreditsLimit = limit.GetDecimal();
            if (data.TryGetProperty("is_free_tier", out var free))
                info.IsFreeTier = free.GetBoolean();
            if (data.TryGetProperty("label", out var label))
                info.Label = label.GetString();
        }

        info.IsAvailable = true;
        return info;
    }

    private Task<ProviderBillingInfo?> FetchNvidiaBilling(ProviderInfo provider)
    {
        return Task.FromResult<ProviderBillingInfo?>(new ProviderBillingInfo
        {
            ProviderName = "nvidia",
            HasBillingApi = false,
            IsAvailable = true,
            Notes = "No public billing API. Usage tracked from response headers."
        });
    }

    private Task<ProviderBillingInfo?> FetchGroqBilling(ProviderInfo provider)
    {
        return Task.FromResult<ProviderBillingInfo?>(new ProviderBillingInfo
        {
            ProviderName = "groq",
            HasBillingApi = false,
            IsAvailable = true,
            Notes = "No public billing API. Rate-limit headers available on each response."
        });
    }

    private Task<ProviderBillingInfo?> FetchMoonshotBilling(ProviderInfo provider)
    {
        return Task.FromResult<ProviderBillingInfo?>(new ProviderBillingInfo
        {
            ProviderName = "moonshot",
            HasBillingApi = false,
            IsAvailable = true,
            Notes = "No public billing API."
        });
    }

    private Task<ProviderBillingInfo?> FetchCerebrasBilling(ProviderInfo provider)
    {
        return Task.FromResult<ProviderBillingInfo?>(new ProviderBillingInfo
        {
            ProviderName = "cerebras",
            HasBillingApi = false,
            IsAvailable = true,
            Notes = "No public billing API."
        });
    }

    private Task<ProviderBillingInfo?> FetchZenMuxBilling(ProviderInfo provider)
    {
        return Task.FromResult<ProviderBillingInfo?>(new ProviderBillingInfo
        {
            ProviderName = "zenmux",
            HasBillingApi = true,
            IsAvailable = true,
            Notes = "Credit-based usage tracked via response headers."
        });
    }

    private Task<ProviderBillingInfo?> FetchOllamaBilling(ProviderInfo provider)
    {
        return Task.FromResult<ProviderBillingInfo?>(new ProviderBillingInfo
        {
            ProviderName = "ollama",
            HasBillingApi = false,
            IsAvailable = true,
            Notes = "No public billing API."
        });
    }

    private Task<ProviderBillingInfo?> FetchGoogleBilling(ProviderInfo provider)
    {
        return Task.FromResult<ProviderBillingInfo?>(new ProviderBillingInfo
        {
            ProviderName = "google",
            HasBillingApi = false,
            IsAvailable = true,
            Notes = "Usage tracked via quota headers in response."
        });
    }
}

// ── Data model ─────────────────────────────────────────────────────────

internal sealed class ProviderBillingInfo
{
    public string ProviderName { get; set; } = "";
    public bool HasBillingApi { get; set; }
    public bool IsAvailable { get; set; } = true;
    public string? Notes { get; set; }
    public string? RawData { get; set; }
    public DateTime LastFetched { get; set; } = DateTime.MinValue;

    // DeepSeek
    public List<BalanceEntry>? Balances { get; set; }

    // OpenAI
    public decimal HardLimitUsd { get; set; }
    public decimal SoftLimitUsd { get; set; }
    public decimal SystemHardLimitUsd { get; set; }
    public decimal TotalGranted { get; set; }
    public decimal TotalUsed { get; set; }
    public decimal TotalRemaining { get; set; }

    // OpenRouter
    public decimal CreditsUsed { get; set; }
    public decimal CreditsLimit { get; set; }
    public bool IsFreeTier { get; set; }
    public string? Label { get; set; }

    // Helpers
    public string? PrimaryBalanceDisplay
    {
        get
        {
            if (Balances is { Count: > 0 })
            {
                var b = Balances[0];
                return $"{b.Currency} {b.TotalBalance:F2}";
            }
            if (TotalRemaining > 0) return $"${TotalRemaining:F2} remaining";
            if (CreditsLimit > 0) return $"{CreditsUsed:F2} / {CreditsLimit:F2} credits";
            if (HardLimitUsd > 0) return $"${HardLimitUsd:F2} limit";
            return null;
        }
    }
}

internal sealed class BalanceEntry
{
    public string Currency { get; set; } = "USD";
    public decimal TotalBalance { get; set; }
    public decimal GrantedBalance { get; set; }
    public decimal ToppedUpBalance { get; set; }
}