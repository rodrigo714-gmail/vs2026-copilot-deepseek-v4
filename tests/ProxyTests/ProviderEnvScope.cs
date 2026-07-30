namespace ProxyTests;

/// <summary>
/// Clears every environment variable <see cref="ProviderRegistry"/> reads, so a test controls
/// exactly which providers get discovered, then restores the previous values on dispose.
///
/// The list is derived from <see cref="ProviderCapabilitiesRegistry.KnownProviders"/> on purpose:
/// hand-written lists silently rot when a provider is added, and any provider left uncleared
/// picks up a real API key from the developer's .env (loaded by Program.cs through
/// <c>ProxyFixture</c>) and quietly changes collision resolution.
/// </summary>
internal sealed class ProviderEnvScope : IDisposable
{
    private readonly Dictionary<string, string?> _snapshot = new(StringComparer.OrdinalIgnoreCase);

    internal ProviderEnvScope()
    {
        foreach (string name in VariableNames())
        {
            _snapshot[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    internal static IEnumerable<string> VariableNames()
    {
        foreach (string provider in ProviderCapabilitiesRegistry.KnownProviders)
        {
            string prefix = ProviderCapabilitiesRegistry.Get(provider).EnvPrefix;
            yield return $"PROVIDER_{prefix}_API_KEY";
            yield return $"PROVIDER_{prefix}_BASE_URL";
        }

        // Ollama Cloud's backwards-compatible aliases and the legacy DeepSeek fallback.
        yield return "PROVIDER_OLLAMA_API_KEY";
        yield return "PROVIDER_OLLAMA_BASE_URL";
        yield return "DEEPSEEK_API_KEY";
        yield return "DEEPSEEK_BASE_URL";
        yield return "DEEPSEEK_MODEL";
    }

    public void Dispose()
    {
        foreach (KeyValuePair<string, string?> kv in _snapshot)
            Environment.SetEnvironmentVariable(kv.Key, kv.Value);
    }
}
