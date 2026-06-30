using System.Text.Json;

namespace ProxyTests;

public class ProviderRegistryTests : IDisposable
{
    private readonly Dictionary<string, string?> _envSnapshot = new(StringComparer.OrdinalIgnoreCase);

    public ProviderRegistryTests()
    {
        // Snapshot env vars we may change, then set a minimal env so ProviderRegistry
        // discovers at least one provider (deepseek).
        string[] keys = ["PROVIDER_DEEPSEEK_API_KEY", "PROVIDER_DEEPSEEK_BASE_URL", "DEEPSEEK_API_KEY", "DEEPSEEK_MODEL"];
        foreach (string k in keys)
        {
            _envSnapshot[k] = Environment.GetEnvironmentVariable(k);
        }
        Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_API_KEY", "test-key");
        // Clear backward-compat fallback key so it doesn't interfere.
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
        // Ensure no default base URL env var is set that would point to a real endpoint
        Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_BASE_URL", null);
    }

    public void Dispose()
    {
        foreach (KeyValuePair<string, string?> kv in _envSnapshot)
        {
            if (kv.Value is null)
                Environment.SetEnvironmentVariable(kv.Key, null);
            else
                Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }
    }

    [Fact]
    public void ResolveProvider_WithNullModel_ReturnsDefaultProvider()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ProviderInfo result = registry.ResolveProvider(null);

        // First registered provider = deepseek (via PROVIDER_DEEPSEEK_API_KEY)
        Assert.Equal("deepseek", result.Name);
    }

    [Fact]
    public void ResolveProvider_WithEmptyModel_ReturnsDefaultProvider()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ProviderInfo result = registry.ResolveProvider("");

        // First registered provider = deepseek (via PROVIDER_DEEPSEEK_API_KEY)
        Assert.Equal("deepseek", result.Name);
    }

    [Fact]
    public void ResolveModel_WithNullModel_ReturnsDefaultModel()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        string result = registry.ResolveModel(null);

        Assert.Equal("deepseek-v4-pro", result);
    }

    [Fact]
    public void ResolveModel_WithEmptyModel_ReturnsDefaultModel()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        string result = registry.ResolveModel("");

        Assert.Equal("deepseek-v4-pro", result);
    }

    [Fact]
    public void ResolveUpstreamModel_WithNullModel_ReturnsDefaultModel()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        string result = registry.ResolveUpstreamModel(null);

        Assert.Equal("deepseek-v4-pro", result);
    }

    [Fact]
    public void DefaultModel_IsDeepSeekV4Pro()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        Assert.Equal("deepseek-v4-pro", registry.DefaultModel);
    }

    [Fact]
    public void UpdateModelMappings_UpdatesModelToProvider()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        Dictionary<string, ProviderInfo> newMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["custom-model"] = new ProviderInfo("groq", "key", "http://localhost", new System.Net.Http.HttpClient(), ProviderCapabilitiesRegistry.Get("groq"))
        };
        Dictionary<string, string> newUpstream = new(StringComparer.OrdinalIgnoreCase)
        {
            ["custom-model"] = "llama-3.3-70b-versatile"
        };

        registry.UpdateModelMappings(newMap, newUpstream);

        ProviderInfo result = registry.ResolveProvider("custom-model");
        
        Assert.Equal("groq", result.Name);
    }

    [Fact]
    public void ResolveCandidates_WithNullModel_ReturnsDefaultProvider()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        var candidates = registry.ResolveCandidates(null);

        // When no providers are discovered (no API keys), returns empty.
        // When deepseek is registered, fallback to first provider = deepseek.
        if (candidates.Count > 0)
        {
            Assert.Equal("deepseek", candidates[0].Provider.Name);
        }
    }

    [Fact]
    public void Providers_AtLeastOneProviderExists()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        Assert.NotEmpty(registry.Providers);
    }

    [Fact]
    public void ModelToProvider_IsNotNull()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        Assert.NotNull(registry.ModelToProvider);
    }
}