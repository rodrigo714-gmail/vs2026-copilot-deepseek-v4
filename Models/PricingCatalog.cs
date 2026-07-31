internal static class PricingCatalog
{
    private static readonly Dictionary<string, ModelPricing> _pricing = new(StringComparer.OrdinalIgnoreCase);

    static PricingCatalog()
    {
        Register("deepseek","deepseek-v4-flash",0.14m,0.28m,0.0028m,820,null,null,120,"paid");
        Register("deepseek","deepseek-v4-pro",0.435m,0.87m,0.0036m,880,null,null,60,"paid");
        Register("zai","glm-5.2",1.40m,4.40m,0.26m,1481,1593,4.40m,50,"paid","#2 WebDev Arena");
        Register("zai","glm-4.7-flash",0,0,null,null,null,null,150,"free","100% FREE");
        Register("zai","glm-4.7-flashx",0.07m,0.40m,null,null,null,null,120,"paid");
        Register("zai","glm-4.7",0.60m,2.20m,0.11m,null,null,null,80,"paid");
        Register("moonshot","kimi-k2.7-code",0.95m,4.00m,0.19m,null,null,null,80,"paid");
        Register("moonshot","kimi-k2.7-code-highspeed",1.90m,8.00m,0.38m,null,null,null,180,"paid");
        Register("moonshot","kimi-k2.6",0.95m,4.00m,0.16m,null,null,null,70,"paid");
        Register("google","models/gemini-3.5-flash",0.15m,0.60m,0.0375m,null,null,null,200,"paid");
        Register("google","models/gemini-3.5-pro",1.25m,5.00m,0.3125m,1486,null,null,60,"premium");
        Register("google","models/gemini-2.5-flash",0.30m,1.50m,0.075m,null,null,null,150,"paid");
        Register("google","models/gemini-2.5-pro",1.25m,10.00m,0.3125m,null,null,null,40,"premium");
        Register("groq","gpt-oss-20b",0.075m,0.30m,0.0375m,null,null,null,1000,"paid");
        Register("groq","gpt-oss-120b",0.15m,0.60m,0.075m,null,null,null,500,"paid");
        Register("groq","llama-3.3-70b",0.59m,0.79m,null,null,null,null,394,"paid");
        Register("openai","gpt-5.5",5.00m,30.00m,1.25m,1481,null,8.04m,60,"premium");
        Register("openai","gpt-5.4",2.50m,15.00m,0.625m,null,null,6.58m,80,"premium");
        Register("openai","gpt-5.4-mini",0.75m,4.50m,0.1875m,null,null,null,120,"paid");
        Register("openai","gpt-4.1",1.00m,4.00m,0.25m,null,null,null,80,"paid");
        Register("anthropic","claude-haiku-4.5",1.00m,5.00m,0.10m,null,null,null,80,"paid");
        Register("anthropic","claude-sonnet-4.6",3.00m,15.00m,0.30m,null,null,null,60,"premium");
        Register("anthropic","claude-opus-4.8",5.00m,25.00m,0.50m,1484,1565,8.89m,40,"premium");
        Register("anthropic","claude-fable-5",10.00m,50.00m,1.00m,1508,1654,14.00m,30,"premium","#1 en todo");
        Register("nvidia","nvidia/nemotron-3-120b-a12b",0,0,null,null,null,null,80,"free");
        Register("nvidia","deepseek-ai/deepseek-v4-pro",0,0,null,880,null,null,60,"free");
        Register("nvidia","z-ai/glm-5.1",0,0,null,null,null,null,50,"free");
        Register("cerebras",null,0.10m,0.40m,null,null,null,null,600,"paid","$50/mes Code Pro");

        // ── Provider-level fallbacks (model:* wildcards) ──────────────────────
        // Folded in from the former PricingCalculator._providerDefaults, which was a second,
        // parallel pricing table keyed by provider name. It never knew about `zai`, so every
        // Z.AI request was costed at $0 and the dashboard under-reported spend. Keeping one
        // table means a provider added to the registry cannot silently price at zero.
        Register("deepseek",null,1.00m,4.00m,null,null,null,null,null,"paid","provider default");
        Register("openai",null,5.00m,20.00m,null,null,null,null,null,"paid","provider default");
        Register("groq",null,0.50m,0.75m,null,null,null,null,null,"paid","provider default");
        Register("nvidia",null,2.00m,8.00m,null,null,null,null,null,"paid","provider default");
        Register("openrouter",null,1.00m,4.00m,null,null,null,null,null,"paid","provider default");
        Register("moonshot",null,1.50m,6.00m,null,null,null,null,null,"paid","provider default");
        Register("zenmux",null,0.50m,2.00m,null,null,null,null,null,"paid","provider default");
        Register("google",null,0.50m,2.00m,null,null,null,null,null,"paid","provider default");
        Register("zai",null,0.60m,2.20m,null,null,null,null,null,"paid","provider default");
        Register("ollama",null,0,0,null,null,null,null,null,"free","provider default");

        // Free-tier providers. Priced at 0 because their published free allowance is what these
        // are used for here — but they still need an entry, or EveryRegisteredProvider_HasAPriceFallback
        // fails and their usage would be silently invisible in the cost report.
        Register("mistral",null,0,0,null,null,null,null,null,"free","free Experiment tier (~2 RPM)");
        Register("siliconflow",null,0,0,null,null,null,null,null,"free","free models capped by requests/day");
        Register("cloudflare",null,0,0,null,null,null,null,null,"free","10k neurons/day, resets 00:00 UTC");
    }

    private static void Register(string p, string? m, decimal i, decimal o, decimal? c, int? elo, int? wd, decimal? awr, int? tps, string tier = "paid", string? notes = null)
    {
        string k = m is not null ? $"{p}:{m}" : $"{p}:*";
        _pricing[k] = new ModelPricing(p, m, i, o, c, elo, wd, awr, tps, tier, notes ?? "");
    }

    /// <summary>
    /// Resolves pricing: exact <c>provider:model</c>, then the provider's <c>provider:*</c>
    /// fallback, then — only for providers with no fallback registered — a cross-provider
    /// match on the model name alone.
    /// </summary>
    internal static ModelPricing? Get(string provider, string model)
    {
        if (_pricing.TryGetValue($"{provider}:{model}", out var x)) return x;
        if (_pricing.TryGetValue($"{provider}:*", out x)) return x;
        foreach (var kv in _pricing)
            if (kv.Key.EndsWith($":{model}", StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    /// <summary>
    /// Estimated cost in USD for a completion. Returns 0 when the model/provider pair has no
    /// registered price — an unknown price is reported as unknown, never guessed.
    /// </summary>
    /// <remarks>
    /// Note the argument order: <paramref name="provider"/> first, matching <see cref="Get"/>.
    /// The <c>PricingCalculator.CalculateCost</c> this replaces took <c>(model, provider)</c>.
    /// </remarks>
    internal static double EstimateCostUsd(string provider, string model, long promptTokens, long completionTokens)
    {
        ModelPricing? pricing = Get(provider, model);
        if (pricing is null) return 0;

        ModelPricing p = pricing.Value;
        if (p.Tier == "free") return 0;

        return (promptTokens / 1_000_000.0) * (double)p.InputPerMillion
             + (completionTokens / 1_000_000.0) * (double)p.OutputPerMillion;
    }

    internal static IEnumerable<ModelPricing> All => _pricing.Values;
}

internal readonly record struct ModelPricing(
    string Provider, string? ModelMatch,
    decimal InputPerMillion, decimal OutputPerMillion,
    decimal? CachedInputPerMillion,
    int? ArenaElo, int? ArenaWebDev, decimal? ArenaAgentWinRate,
    int? EstimatedTps, string Tier, string Notes)
{
    internal decimal EstimateCost(int promptTokens, int completionTokens, int? cachedTokens = null)
    {
        if (Tier == "free") return 0;
        return (promptTokens / 1_000_000m) * InputPerMillion + (completionTokens / 1_000_000m) * OutputPerMillion;
    }

    internal string Label => string.IsNullOrWhiteSpace(Notes)
        ? $"{Provider}/{ModelMatch ?? "default"} [{Tier}]"
        : $"{Provider}/{ModelMatch ?? "default"} [{Tier}] - {Notes}";
}
