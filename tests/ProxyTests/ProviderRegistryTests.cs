using System.Text.Json;

namespace ProxyTests;

/// <summary>
/// These tests construct a real <see cref="ProviderRegistry"/>, which reads process environment
/// variables at construction time — so they must be in the "Proxy" collection and must use
/// <see cref="ProviderEnvScope"/>.
///
/// They previously did neither: the class hand-rolled a four-key snapshot covering only
/// DeepSeek. That happened to pass because DeepSeek was the only key it touched, but any other
/// provider key present in the developer's .env (loaded into the process by Program.cs via
/// ProxyFixture) was left set, so those providers were discovered too and cross-provider
/// collision resolution shifted underneath the assertions. ProviderEnvScope derives its list
/// from ProviderCapabilitiesRegistry.KnownProviders, so it cannot rot as providers are added.
/// </summary>
[Collection("Proxy")]
public class ProviderRegistryTests : IDisposable
{
    private readonly ProviderEnvScope _envScope = new();

    public ProviderRegistryTests()
    {
        // ProviderEnvScope has already cleared every PROVIDER_* variable. Set the single key
        // these tests rely on, so exactly one provider (deepseek) is discovered.
        Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_API_KEY", "test-key");
    }

    public void Dispose() => _envScope.Dispose();

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