using System.Text.Json;

namespace ProxyTests;

// Share the "Proxy" collection so ProxyFixture's environment-variable setup
// runs before any of these tests, and the registry constructor can find a
// configured PROVIDER_DEEPSEEK_API_KEY.
[Collection("Proxy")]
public class ProviderRegistryTests
{
    [Fact]
    public void ResolveProvider_WithNullModel_ReturnsDefaultProvider()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ProviderInfo result = registry.ResolveProvider(null)!.Value;

        Assert.Equal("deepseek", result.Name);
    }

    [Fact]
    public void ResolveProvider_WithEmptyModel_ReturnsDefaultProvider()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ProviderInfo result = registry.ResolveProvider("")!.Value;

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
            ["custom-model"] = new ProviderInfo("groq", "key", "http://localhost", new System.Net.Http.HttpClient())
        };
        Dictionary<string, string> newUpstream = new(StringComparer.OrdinalIgnoreCase)
        {
            ["custom-model"] = "llama-3.3-70b-versatile"
        };

        registry.UpdateModelMappings(newMap, newUpstream);

        ProviderInfo result = registry.ResolveProvider("custom-model")!.Value;
        Assert.Equal("groq", result.Name);
    }

    [Fact]
    public void ResolveCandidates_WithNullModel_ReturnsDefaultProvider()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        var candidates = registry.ResolveCandidates(null);

        Assert.Single(candidates);
        Assert.Equal("deepseek", candidates[0].Provider.Name);
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