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
            EnvPrefix: "DEEPSEEK"),

        ["openai"] = new(
            Category: ProviderCategory.Direct,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: true,
            SupportsTopK: false,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://api.openai.com",
            EnvPrefix: "OPENAI"),

        ["moonshot"] = new(
            Category: ProviderCategory.Direct,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: false,
            SupportsTopK: false,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://api.moonshot.ai",
            EnvPrefix: "MOONSHOT"),

        ["google"] = new(
            Category: ProviderCategory.Direct,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: true,
            SupportsTopK: false,
            ChatPath: "v1beta/openai/chat/completions",
            ModelsPath: "v1beta/openai/models",
            DefaultBaseUrl: "https://generativelanguage.googleapis.com",
            EnvPrefix: "GOOGLE"),

        ["cerebras"] = new(
            Category: ProviderCategory.Direct,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: false,
            SupportsTopK: false,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://api.cerebras.ai",
            EnvPrefix: "CEREBRAS"),

        // ── Multi-model providers (OpenAI-compatible API) ────────────────────
        ["nvidia"] = new(
            Category: ProviderCategory.MultiModel,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: false,
            SupportsTopK: true,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://integrate.api.nvidia.com",
            EnvPrefix: "NVIDIA"),

        ["openrouter"] = new(
            Category: ProviderCategory.MultiModel,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: false,
            SupportsTopK: true,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://openrouter.ai/api",
            EnvPrefix: "OPENROUTER"),

        ["groq"] = new(
            Category: ProviderCategory.MultiModel,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: false,
            SupportsTopK: true,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://api.groq.com/openai",
            EnvPrefix: "GROQ"),

        // ── Multi-model providers (ZenMux - aggregator) ─────────────────────
        ["zenmux"] = new(
            Category: ProviderCategory.MultiModel,
            ApiFormat: ApiFormat.OpenAi,
            SupportsReasoningEffort: false,
            SupportsTopK: false,
            ChatPath: "v1/chat/completions",
            ModelsPath: "v1/models",
            DefaultBaseUrl: "https://zenmux.ai/api",
            EnvPrefix: "ZENMUX"),

        // ── Multi-model providers (Ollama API) ───────────────────────────────
        ["ollama"] = new(
            Category: ProviderCategory.MultiModel,
            ApiFormat: ApiFormat.Ollama,
            SupportsReasoningEffort: false,
            SupportsTopK: false,
            ChatPath: "api/chat",
            ModelsPath: "api/tags",
            DefaultBaseUrl: "https://ollama.com",
            EnvPrefix: "OLLAMACLOUD"),
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
}
