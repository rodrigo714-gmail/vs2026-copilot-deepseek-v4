namespace ProxyTests;

/// <summary>
/// Tests for the three-level "provider/model" hint resolver in <see cref="ProviderRegistry.ResolveModel"/>.
///
/// Behaviour:
///   1. The full id is taken verbatim if it appears in the registry (e.g. "openai/gpt-oss-120b").
///   2. Otherwise the prefix is stripped and the bare name is looked up
///      (e.g. "nvidia/qwen3-coder-480b-a35b-instruct" → "qwen3-coder-480b-a35b-instruct"
///      when the bare name itself is registered).
///   3. As a last resort, the bare name is matched against any upstream id owned by the
///      hinted provider whose suffix equals the bare name
///      (e.g. "nvidia/qwen3.5-397b-a17b" → "qwen/qwen3.5-397b-a17b", the actual upstream id).
/// </summary>
[Collection("Proxy")]
public class ProviderModelHintTests
{
    // ── Level 1: verbatim match ───────────────────────────────────────────

    [Fact]
    public void ResolveModel_ProviderModelHint_VerbatimMatch_ReturnsVerbatim()
    {
        // nvidia.json's "openai/gpt-oss-120b" is registered under that exact key.
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);
        ModelSelectionStore store = new();
        ModelCatalogService catalog = new(registry, store);

        registry.UpdateModelMappings(
            new Dictionary<string, ProviderInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai/gpt-oss-120b"] = MakeProvider("nvidia"),
                ["openai/gpt-oss-120b@nvidia"] = MakeProvider("nvidia"),
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        string resolved = registry.ResolveModel("nvidia/openai/gpt-oss-120b");

        // Verbatim key wins, so the slash is preserved.
        Assert.Equal("openai/gpt-oss-120b", resolved);
    }

    // ── Level 2: strip prefix, look up bare ───────────────────────────────

    [Fact]
    public void ResolveModel_ProviderModelHint_StripsPrefixToBare()
    {
        // "groq/qwen3-32b" — the bare "qwen3-32b" is registered under groq.
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);
        ModelSelectionStore store = new();
        ModelCatalogService catalog = new(registry, store);

        registry.UpdateModelMappings(
            new Dictionary<string, ProviderInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["qwen3-32b"] = MakeProvider("groq"),
                ["qwen3-32b@groq"] = MakeProvider("groq"),
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        string resolved = registry.ResolveModel("groq/qwen3-32b");

        Assert.Equal("qwen3-32b", resolved);
    }

    [Fact]
    public void ResolveModel_ProviderModelHint_StripsPrefix_BareIsRegistered()
    {
        // Same as above but with a different bare name to confirm bare stripping works
        // when the provider prefix in the request isn't equal to the provider key prefix
        // (still works because ResolveModel only consults the provider hint on a "no match").
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);
        ModelSelectionStore store = new();
        ModelCatalogService catalog = new(registry, store);

        registry.UpdateModelMappings(
            new Dictionary<string, ProviderInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["llama-3.3-70b-versatile"] = MakeProvider("groq"),
                ["llama-3.3-70b-versatile@groq"] = MakeProvider("groq"),
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        string resolved = registry.ResolveModel("groq/llama-3.3-70b-versatile");

        Assert.Equal("llama-3.3-70b-versatile", resolved);
    }

    // ── Level 3: suffix match within the hinted provider ──────────────────

    [Fact]
    public void ResolveModel_ProviderModelHint_SuffixMatchWithinProvider()
    {
        // The actual upstream id is "qwen/qwen3.5-397b-a17b" (NVIDIA exposes it with the
        // "qwen/" family prefix). A user request like "nvidia/qwen3.5-397b-a17b" must
        // resolve to the full "qwen/qwen3.5-397b-a17b" upstream id.
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);
        ModelSelectionStore store = new();
        ModelCatalogService catalog = new(registry, store);

        registry.UpdateModelMappings(
            new Dictionary<string, ProviderInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["qwen/qwen3.5-397b-a17b"] = MakeProvider("nvidia"),
                ["qwen/qwen3.5-397b-a17b@nvidia"] = MakeProvider("nvidia"),
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["qwen/qwen3.5-397b-a17b"] = "qwen/qwen3.5-397b-a17b",
                ["qwen/qwen3.5-397b-a17b@nvidia"] = "qwen/qwen3.5-397b-a17b",
            });

        string resolved = registry.ResolveModel("nvidia/qwen3.5-397b-a17b");

        // Suffix match wins, returning the actual upstream id.
        Assert.Equal("qwen/qwen3.5-397b-a17b", resolved);
    }

    [Fact]
    public void ResolveModel_ProviderModelHint_SuffixMatch_DoesNotCrossProviders()
    {
        // If the suffix is owned by a different provider, the suffix-match path must NOT
        // resolve to it (it would route to the wrong upstream).
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);
        ModelSelectionStore store = new();
        ModelCatalogService catalog = new(registry, store);

        registry.UpdateModelMappings(
            new Dictionary<string, ProviderInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["qwen/qwen3.5-397b-a17b"] = MakeProvider("nvidia"),
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        // "groq/qwen3.5-397b-a17b" — the suffix match must NOT pick nvidia's id because
        // the hint says groq, but no groq claimant has that suffix. The resolver returns
        // the default model.
        string resolved = registry.ResolveModel("groq/qwen3.5-397b-a17b");

        Assert.Equal(registry.DefaultModel, resolved);
    }

    // ── ResolveCandidates with a qualified hint ──────────────────────────

    [Fact]
    public void ResolveCandidates_WithProviderHint_ReturnsSingleNoFailover()
    {
        // When the user requests "openai/gpt-oss-120b@ollama" (qualified), the resolver
        // must return exactly one candidate — no failover to nvidia/groq — even if those
        // providers also offer the upstream id.
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);
        ModelSelectionStore store = new();
        ModelCatalogService catalog = new(registry, store);

        registry.UpdateModelMappings(
            new Dictionary<string, ProviderInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai/gpt-oss-120b"] = MakeProvider("nvidia"),
                ["openai/gpt-oss-120b@nvidia"] = MakeProvider("nvidia"),
                ["openai/gpt-oss-120b@groq"] = MakeProvider("groq"),
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai/gpt-oss-120b"] = "openai/gpt-oss-120b",
                ["openai/gpt-oss-120b@nvidia"] = "openai/gpt-oss-120b",
                ["openai/gpt-oss-120b@groq"] = "openai/gpt-oss-120b",
            },
            new Dictionary<string, List<ProviderInfo>>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai/gpt-oss-120b"] = [MakeProvider("nvidia"), MakeProvider("groq")],
            });

        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands =
            registry.ResolveCandidates("openai/gpt-oss-120b@ollama");

        // The hint is for ollama, but ollama doesn't claim this id, so fallback to default.
        Assert.Single(cands);
    }

    [Fact]
    public void ResolveCandidates_WithProviderHint_MatchingProvider_ReturnsExactCandidate()
    {
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);
        ModelSelectionStore store = new();
        ModelCatalogService catalog = new(registry, store);

        registry.UpdateModelMappings(
            new Dictionary<string, ProviderInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["kimi-k2.6"] = MakeProvider("moonshot"),
                ["kimi-k2.6@moonshot"] = MakeProvider("moonshot"),
                ["kimi-k2.6@ollama"] = MakeProvider("ollama"),
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["kimi-k2.6"] = "kimi-k2.6",
                ["kimi-k2.6@moonshot"] = "kimi-k2.6",
                ["kimi-k2.6@ollama"] = "kimi-k2.6",
            },
            new Dictionary<string, List<ProviderInfo>>(StringComparer.OrdinalIgnoreCase)
            {
                ["kimi-k2.6"] = [MakeProvider("moonshot"), MakeProvider("ollama")],
            });

        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands =
            registry.ResolveCandidates("kimi-k2.6@moonshot");

        Assert.Single(cands);
        Assert.Equal("moonshot", cands[0].Provider.Name);
        Assert.Equal("kimi-k2.6", cands[0].UpstreamModel);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static ProviderInfo MakeProvider(string name) =>
        new(name, "key", $"http://{name}.test/", new HttpClient(),
            ProviderCapabilitiesRegistry.TryGet(name, out ProviderCapabilities caps) ? caps : default);
}
