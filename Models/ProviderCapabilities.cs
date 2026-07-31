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
/// How to read a provider's remaining balance / quota. Each value maps to one probe
/// implementation in <c>ProviderBillingService</c>; <see cref="None"/> means the provider
/// publishes no billing API and only a descriptive note is available.
/// </summary>
public enum ProviderBillingProbe
{
    /// <summary>No public billing API — report <c>BillingNote</c> only.</summary>
    None,

    /// <summary>DeepSeek <c>GET /user/balance</c>.</summary>
    DeepSeekBalance,

    /// <summary>OpenAI <c>/v1/dashboard/billing/{subscription,credit_grants}</c>.</summary>
    OpenAiDashboard,

    /// <summary>OpenRouter <c>GET /api/v1/auth/key</c> (usage, limit, is_free_tier).</summary>
    OpenRouterAuthKey
}

/// <summary>
/// Declares the static capabilities of a provider: what API format it uses, which parameters
/// it supports, and how to discover it via environment variables. This is the single source
/// of truth — all scattered <c>provider.Name.Equals("ollama")</c> checks are replaced by
/// capability lookups against this struct.
/// </summary>
/// <remarks>
/// <see cref="DisplayName"/>, <see cref="Billing"/> and <see cref="BillingNote"/> are trailing
/// and defaulted on purpose: every registry entry uses named arguments, so adding them here
/// does not touch a single existing construction site. They exist so that adding a provider
/// really is the two-file change the registry's doc comment promises — before this, three
/// separate per-provider <c>switch</c> statements had to be patched too, and two of them had
/// already drifted apart.
/// </remarks>
public readonly record struct ProviderCapabilities(
    ProviderCategory Category,
    ApiFormat ApiFormat,
    bool SupportsReasoningEffort,
    bool SupportsTopK,
    string ChatPath,
    string ModelsPath,
    string DefaultBaseUrl,
    string EnvPrefix,
    string DisplayName = "",
    ProviderBillingProbe Billing = ProviderBillingProbe.None,
    string BillingNote = "",
    bool RequiresExplicitBaseUrl = false
);
