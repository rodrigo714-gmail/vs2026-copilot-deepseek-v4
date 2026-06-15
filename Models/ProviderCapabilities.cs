/// <summary>
/// Provider category for routing and display purposes.
/// </summary>
public enum ProviderCategory
{
    /// <summary>Owns its models directly (DeepSeek, OpenAI, Moonshot).</summary>
    Direct,

    /// <summary>Proxies many models from different sources (Ollama, NVIDIA, OpenRouter, Groq).</summary>
    MultiModel
}

/// <summary>
/// The API format a provider speaks.
/// </summary>
public enum ApiFormat
{
    /// <summary>Standard OpenAI-compatible /v1/chat/completions API.</summary>
    OpenAi,

    /// <summary>Ollama-native /api/chat API with NDJSON streaming.</summary>
    Ollama
}

/// <summary>
/// Declares the static capabilities of a provider: what API format it uses, which parameters
/// it supports, and how to discover it via environment variables. This is the single source
/// of truth — all scattered <c>provider.Name.Equals("ollama")</c> checks are replaced by
/// capability lookups against this struct.
/// </summary>
public readonly record struct ProviderCapabilities(
    ProviderCategory Category,
    ApiFormat ApiFormat,
    bool SupportsReasoningEffort,
    bool SupportsTopK,
    string ChatPath,
    string ModelsPath,
    string DefaultBaseUrl,
    string EnvPrefix
);
