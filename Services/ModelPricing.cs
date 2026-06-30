/// <summary>
/// Provider-agnostic pricing table per model.
/// Prices are in USD per 1M tokens (prompt / completion).
/// Sources: official provider pricing pages as of June 2026.
/// Falls back to provider-level defaults when model not found.
/// </summary>
internal static class PricingCalculator
{
    // Key: model id (lowercase). Value: (prompt_price_per_1M, completion_price_per_1M)
    private static readonly Dictionary<string, (double Prompt, double Completion)> _pricing = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── DeepSeek ──────────────────────────────────────────────────
        ["deepseek-v4-pro"] = (2.00, 8.00),
        ["deepseek-v4-flash"] = (0.50, 2.00),

        // ── OpenAI ────────────────────────────────────────────────────
        ["gpt-5"] = (10.00, 40.00),
        ["gpt-5-mini"] = (2.50, 10.00),
        ["gpt-4.1"] = (2.00, 8.00),
        ["gpt-4o"] = (2.50, 10.00),
        ["gpt-oss-120b"] = (1.50, 6.00),
        ["gpt-oss-20b"] = (0.50, 2.00),

        // ── Groq ──────────────────────────────────────────────────────
        ["llama-3.3-70b-versatile"] = (0.59, 0.79),
        ["qwen3-32b"] = (0.59, 0.79),
        ["meta-llama/llama-4-scout-17b-16e-instruct"] = (0.20, 0.20),

        // ── NVIDIA NIM ────────────────────────────────────────────────
        ["qwen/qwen3.5-397b-a17b"] = (3.00, 12.00),
        ["qwen3-coder-480b"] = (5.00, 20.00),
        ["moonshotai/kimi-k2.6"] = (1.50, 6.00),
        ["nvidia/nemotron-3-super-120b-a12b"] = (2.00, 8.00),

        // ── OpenRouter ────────────────────────────────────────────────
        ["qwen/qwen3-coder"] = (0.50, 2.00),
        ["deepseek/deepseek-v4-pro"] = (2.00, 8.00),
        ["nvidia/nemotron-3-ultra-550b-a55b"] = (3.00, 12.00),

        // ── Moonshot / Kimi ───────────────────────────────────────────
        ["kimi-k2.7-code"] = (2.00, 8.00),
        ["kimi-k2.6"] = (1.50, 6.00),
        ["kimi-k2.5"] = (1.00, 4.00),

        // ── Cerebras ──────────────────────────────────────────────────
        ["zai-glm-4.7"] = (2.00, 8.00),

        // ── Google Gemini ─────────────────────────────────────────────
        ["models/gemini-2.5-pro"] = (1.25, 10.00),
        ["models/gemini-2.5-flash"] = (0.15, 0.60),
        ["models/gemini-3.5-flash"] = (0.10, 0.40),
    };

    // Provider-level fallback prices when specific model is not listed
    private static readonly Dictionary<string, (double Prompt, double Completion)> _providerDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek"] = (1.00, 4.00),
        ["openai"] = (5.00, 20.00),
        ["groq"] = (0.50, 0.75),
        ["nvidia"] = (2.00, 8.00),
        ["openrouter"] = (1.00, 4.00),
        ["moonshot"] = (1.50, 6.00),
        ["cerebras"] = (1.00, 4.00),
        ["zenmux"] = (0.50, 2.00),
        ["ollama"] = (0.00, 0.00),
        ["google"] = (0.50, 2.00),
    };

    /// <summary>
    /// Calculates estimated cost in USD for a given model and token counts.
    /// </summary>
    internal static double CalculateCost(string model, string provider, long promptTokens, long completionTokens)
    {
        double promptPrice, completionPrice;

        if (_pricing.TryGetValue(model, out var prices))
        {
            (promptPrice, completionPrice) = prices;
        }
        else if (_providerDefaults.TryGetValue(provider, out var defaultPrices))
        {
            (promptPrice, completionPrice) = defaultPrices;
        }
        else
        {
            return 0;
        }

        double promptCost = (promptTokens / 1_000_000.0) * promptPrice;
        double completionCost = (completionTokens / 1_000_000.0) * completionPrice;
        return promptCost + completionCost;
    }
}