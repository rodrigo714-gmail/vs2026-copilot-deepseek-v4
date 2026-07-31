/// <summary>
/// Static registry of provider capabilities. Adding a new provider only requires:
/// 1. One entry in this dictionary.
/// 2. A <c>config/model-selection/{provider}.json</c> file.
/// No other code changes are needed — all routing, filtering, and discovery logic
/// reads from this registry.
/// </summary>
internal static class ProviderCapabilitiesRegistry
{
    private static readonly Dictionary<string, ProviderCapabilities> _capabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Direct providers (own their models, OpenAI-compatible API) ──────
        ["deepseek"] = new(
            Category: ProviderCategory.Direct,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: true,
            SupportsTopK: false,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://api.deepseek.com",
            EnvPrefix: "DEEPSEEK",
            DisplayName: "DeepSeek",
            Billing: ProviderBillingProbe.DeepSeekBalance),

        ["openai"] = new(
            Category: ProviderCategory.Direct,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: true,
            SupportsTopK: false,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://api.openai.com",
            EnvPrefix: "OPENAI",
            DisplayName: "OpenAI",
            Billing: ProviderBillingProbe.OpenAiDashboard),

        ["moonshot"] = new(
            Category: ProviderCategory.Direct,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: true,
            SupportsTopK: false,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://api.moonshot.ai",
            EnvPrefix: "MOONSHOT",
            DisplayName: "Kimi/Moonshot",
            BillingNote: "No public billing API."),

        ["google"] = new(
            Category: ProviderCategory.Direct,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: true,
            SupportsTopK: false,
            ChatPath: "v1beta/openai/chat/completions",
            ModelsPath: "v1beta/openai/models",
            DefaultBaseUrl: "https://generativelanguage.googleapis.com",
            EnvPrefix: "GOOGLE",
            DisplayName: "Google Gemini",
            BillingNote: "Usage tracked via quota headers in response."),

        ["cerebras"] = new(
            Category: ProviderCategory.Direct,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: false,
            SupportsTopK: false,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://api.cerebras.ai",
            EnvPrefix: "CEREBRAS",
            DisplayName: "Cerebras",
            BillingNote: "No public billing API."),


        ["zai"] = new(
            Category: ProviderCategory.Direct,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: true,
            SupportsTopK: false,
            ChatPath: "chat/completions",
            ModelsPath: "models",
            DefaultBaseUrl: "https://api.z.ai/api/paas/v4",
            EnvPrefix: "ZAI",
            DisplayName: "Z.AI GLM",
            BillingNote: "No public billing API."),
        // ── Multi-model providers (OpenAI-compatible API) ────────────────────
        ["nvidia"] = new(
            Category: ProviderCategory.MultiModel,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: false,
            SupportsTopK: true,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://integrate.api.nvidia.com",
            EnvPrefix: "NVIDIA",
            DisplayName: "NVIDIA NIM",
            BillingNote: "No public billing API. Usage tracked from response headers."),

        ["openrouter"] = new(
            Category: ProviderCategory.MultiModel,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: false,
            SupportsTopK: true,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://openrouter.ai/api",
            EnvPrefix: "OPENROUTER",
            DisplayName: "OpenRouter",
            Billing: ProviderBillingProbe.OpenRouterAuthKey),

        ["groq"] = new(
            Category: ProviderCategory.MultiModel,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: false,
            SupportsTopK: true,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://api.groq.com/openai",
            EnvPrefix: "GROQ",
            DisplayName: "Groq",
            BillingNote: "No public billing API. Rate-limit headers available on each response."),

        // ── Multi-model providers (ZenMux - aggregator) ─────────────────────
        ["zenmux"] = new(
            Category: ProviderCategory.MultiModel,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: false,
            SupportsTopK: false,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://zenmux.ai/api",
            EnvPrefix: "ZENMUX",
            DisplayName: "ZenMux",
            BillingNote: "Credit-based usage tracked via response headers."),

        // ── Free-tier heavy providers ────────────────────────────────────────
        // Mistral's free "Experiment" tier is by far the largest documented free token pool
        // (~1B/month), but it is rate-limited to a couple of requests per minute — so it earns
        // its place as a fallback for chat, not as a primary for completions.
        ["mistral"] = new(
            Category: ProviderCategory.Direct,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: false,
            SupportsTopK: false,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://api.mistral.ai",
            EnvPrefix: "MISTRAL",
            DisplayName: "Mistral",
            BillingNote: "No public billing API. Free tier limits are visible in the Mistral console."),

        ["siliconflow"] = new(
            Category: ProviderCategory.MultiModel,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: false,
            SupportsTopK: true,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://api.siliconflow.com",
            EnvPrefix: "SILICONFLOW",
            DisplayName: "SiliconFlow",
            BillingNote: "No public billing API. Free models are capped by requests/day, not tokens."),

        // Cloudflare embeds the account id in the base URL
        // (https://api.cloudflare.com/client/v4/accounts/{account_id}/ai/v1), so the paths below
        // are relative — the same shape as Z.AI's /api/paas/v4. There is no usable default, which
        // is why this provider declares RequiresExplicitBaseUrl.
        ["cloudflare"] = new(
            Category: ProviderCategory.MultiModel,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: false,
            SupportsTopK: false,
            ChatPath: "chat/completions",
            ModelsPath: "models",
            DefaultBaseUrl: "https://api.cloudflare.com/client/v4/accounts/YOUR_ACCOUNT_ID/ai/v1",
            EnvPrefix: "CLOUDFLARE",
            DisplayName: "Cloudflare Workers AI",
            BillingNote: "No public billing API. The free plan is 10,000 neurons/day, reset at UTC midnight.",
            RequiresExplicitBaseUrl: true),

        // ── Multi-model providers (Ollama API) ───────────────────────────────
        ["ollama"] = new(
            Category: ProviderCategory.MultiModel,
            ApiFormat: ApiFormat.Ollama,
            SupportsReasoningEffort: false,
            SupportsTopK: false,
            ChatPath: "api/chat",
            ModelsPath: "api/tags",
            DefaultBaseUrl: "https://ollama.com",
            EnvPrefix: "OLLAMACLOUD",
            DisplayName: "Ollama Cloud",
            BillingNote: "No public billing API."),
    };

    /// <summary>Returns the capabilities for a known provider. Throws for unknown names.</summary>
    internal static ProviderCapabilities Get(string providerName) =>
        _capabilities.TryGetValue(providerName, out ProviderCapabilities caps)
            ? caps
            : throw new InvalidOperationException($"Unknown provider: '{providerName}'. Registered providers: {string.Join(", ", _capabilities.Keys)}");

    /// <summary>Attempts to look up capabilities; returns false for unknown providers.</summary>
    internal static bool TryGet(string providerName, out ProviderCapabilities caps) =>
        _capabilities.TryGetValue(providerName, out caps);

    /// <summary>Returns true if the provider name is registered.</summary>
    internal static bool IsKnownProvider(string providerName) =>
        _capabilities.ContainsKey(providerName);

    /// <summary>All registered provider names.</summary>
    internal static IEnumerable<string> KnownProviders => _capabilities.Keys;

    /// <summary>
    /// Human-readable label for a provider ("nvidia" → "NVIDIA NIM"), for the dashboard and
    /// usage reports. Unknown or unlabelled providers fall back to capitalising the raw name.
    /// </summary>
    /// <remarks>
    /// This replaces two hand-written <c>FormatDisplayName</c> switches that had drifted apart —
    /// neither knew about <c>zai</c>. Keep the labels here, next to the rest of the provider's
    /// declaration, so a new provider cannot ship half-labelled.
    /// </remarks>
    internal static string DisplayName(string providerName)
    {
        if (_capabilities.TryGetValue(providerName, out ProviderCapabilities caps)
            && !string.IsNullOrEmpty(caps.DisplayName))
        {
            return caps.DisplayName;
        }
        return string.IsNullOrEmpty(providerName)
            ? providerName
            : char.ToUpperInvariant(providerName[0]) + providerName[1..];
    }
}