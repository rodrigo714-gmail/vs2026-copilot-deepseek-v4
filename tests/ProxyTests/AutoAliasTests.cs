using System.Text.Json;
using Xunit;

namespace ProxyTests;

/// <summary>
/// Tests for the unpinned <c>@auto</c> alias.
///
/// Every id <c>/api/tags</c> publishes carries an <c>@provider</c> suffix, which resolves to a
/// single candidate. That is what a user picking "GROQ - gpt-oss-120b" asks for, but it also means
/// a client that only ever picks from that list can never fail over: an upstream 402 or 413 comes
/// back to the IDE as a hard error while ten healthy providers sit idle. The <c>@auto</c> alias is
/// the unpinned counterpart — same model, every provider that serves it, best first.
///
/// The awkward part it exists to absorb is that "the same model" has a different id at every
/// provider: <c>gpt-oss-120b</c> at Cerebras, <c>openai/gpt-oss-120b</c> at Groq and NVIDIA,
/// <c>gpt-oss:120b</c> at Ollama. So the candidate list has to carry a per-provider upstream id
/// rather than one shared name.
/// </summary>
// Constructs a ProviderRegistry from process env vars, which ProxyFixture also mutates.
[Collection("Proxy")]
public sealed class AutoAliasTests
{
    private readonly ProxyFixture _fixture;

    public AutoAliasTests(ProxyFixture fixture) => _fixture = fixture;

    // ── Grouping key ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("gpt-oss-120b", "gpt-oss-120b")]
    [InlineData("openai/gpt-oss-120b", "gpt-oss-120b")]
    [InlineData("gpt-oss:120b", "gpt-oss-120b")]
    [InlineData("moonshotai/kimi-k2.7-code", "kimi-k2.7-code")]
    [InlineData("models/gemini-3.5-flash", "gemini-3.5-flash")]
    [InlineData("zai-glm-4.7", "zai-glm-4.7")]
    public void AutoAliasKey_FoldsTheSpellingsOfOneModelTogether(string upstreamId, string expected) =>
        Assert.Equal(expected, ModelCatalogService.AutoAliasKey(upstreamId));

    [Fact]
    public void AutoAliasKey_KeepsGenuinelyDifferentModelsApart()
    {
        // Under-grouping only costs an AUTO entry nobody gets; over-grouping would route a
        // request to a model the user never picked. OpenRouter's ":free" is a different
        // entitlement of the same weights, not another spelling of the paid one.
        Assert.NotEqual(
            ModelCatalogService.AutoAliasKey("nvidia/nemotron-3-super-120b-a12b"),
            ModelCatalogService.AutoAliasKey("nvidia/nemotron-3-super-120b-a12b:free"));

        Assert.NotEqual(
            ModelCatalogService.AutoAliasKey("gpt-5.4-mini"),
            ModelCatalogService.AutoAliasKey("gpt-5.4-nano"));
    }

    // ── Resolution ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("gpt-oss-120b@auto:latest")]  // what /api/tags publishes as the model id
    [InlineData("gpt-oss-120b@auto")]         // the same id with the tag already stripped
    [InlineData("AUTO - gpt-oss-120b:latest")]// the display form, in case a client echoes the name
    public void ResolveModel_AcceptsEveryFormOfTheAutoAlias(string requested)
    {
        using ProviderEnvScope scope = new();
        ProviderRegistry registry = NewRegistry();
        SeedGptOssAlias(registry);

        Assert.Equal("gpt-oss-120b@auto", registry.ResolveModel(requested));
    }

    [Fact]
    public void ResolveCandidates_AutoAlias_FansOutWithEachProvidersOwnUpstreamId()
    {
        using ProviderEnvScope scope = new();
        ProviderRegistry registry = NewRegistry();
        SeedGptOssAlias(registry);

        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> candidates =
            registry.ResolveCandidates("gpt-oss-120b@auto:latest");

        Assert.Equal(2, candidates.Count);

        // Order is the configured one, and each candidate carries the id its own provider knows.
        Assert.Equal("groq", candidates[0].Provider.Name);
        Assert.Equal("openai/gpt-oss-120b", candidates[0].UpstreamModel);

        Assert.Equal("cerebras", candidates[1].Provider.Name);
        Assert.Equal("gpt-oss-120b", candidates[1].UpstreamModel);
    }

    [Fact]
    public void ResolveCandidates_PinnedAlias_StillYieldsExactlyOneCandidate()
    {
        // The regression that matters most: adding @auto must not loosen an explicit pick.
        using ProviderEnvScope scope = new();
        ProviderRegistry registry = NewRegistry();
        SeedGptOssAlias(registry);

        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> candidates =
            registry.ResolveCandidates("openai/gpt-oss-120b@groq:latest");

        (ProviderInfo Provider, string UpstreamModel) only = Assert.Single(candidates);
        Assert.Equal("groq", only.Provider.Name);
    }

    [Fact]
    public void ResolveModel_AutoHintForAModelWithNoAlias_FallsBackToTheBareName()
    {
        // "auto" is not a provider, so the "hint names a provider that cannot serve this"
        // rule must not fire and send the request to the default model. Asking for any
        // provider is satisfied by the only provider that has it.
        using ProviderEnvScope scope = new();
        ProviderRegistry registry = NewRegistry();

        registry.UpdateModelMappings(
            new Dictionary<string, ProviderInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["zai-glm-4.7"] = registry.Providers.First(p => p.Name == "cerebras"),
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("zai-glm-4.7", registry.ResolveModel("zai-glm-4.7@auto:latest"));
    }

    [Fact]
    public void ResolveRoutePlan_AutoAlias_SendsACoolingProviderToTheBack()
    {
        // The whole point of the alias: when the preferred provider is throttled, the next one
        // gets the request instead of the IDE getting an error.
        using ProviderEnvScope scope = new();
        ProviderHealthService health = new();
        ProviderRegistry registry = NewRegistry(health);
        SeedGptOssAlias(registry);

        UpstreamFailure throttled = UpstreamFailureClassifier.Classify(429, null, null);
        Assert.Equal(UpstreamFailureKind.RateLimit, throttled.Kind);
        health.RecordFailure("groq", "openai/gpt-oss-120b", throttled);

        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> plan =
            registry.ResolveRoutePlan("gpt-oss-120b@auto:latest");

        Assert.Equal(2, plan.Count);
        Assert.Equal("cerebras", plan[0].Provider.Name);
        Assert.Equal("groq", plan[1].Provider.Name);

        // Degraded, never excluded — a bad hour across every provider must still produce a try.
        Assert.Contains(plan, c => c.Provider.Name == "groq");
    }

    // ── /api/tags ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiTags_AutoEntries_AreWellFormedAndAdvertiseTheFloorOfTheirCandidates()
    {
        using JsonDocument doc = JsonDocument.Parse(
            await _fixture.Client.GetStringAsync("/api/tags"));

        JsonElement[] all = [.. doc.RootElement.GetProperty("models").EnumerateArray()];
        JsonElement[] auto = [.. all.Where(m => m.GetProperty("name").GetString()!.StartsWith("AUTO - ", StringComparison.Ordinal))];
        JsonElement[] pinned = [.. all.Where(m => !m.GetProperty("name").GetString()!.StartsWith("AUTO - ", StringComparison.Ordinal))];

        foreach (JsonElement entry in auto)
        {
            string id = entry.GetProperty("model").GetString()!;
            Assert.EndsWith("@auto:latest", id, StringComparison.Ordinal);

            string key = id[..id.IndexOf('@')];
            Assert.Equal($"AUTO - {key}:latest", entry.GetProperty("name").GetString());

            // Every AUTO entry must correspond to at least two pinned providers, otherwise it
            // is the pinned entry wearing a different hat and just lengthens the dropdown.
            JsonElement[] members =
            [
                .. pinned.Where(p =>
                    ModelCatalogService.AutoAliasKey(StripPinnedId(p.GetProperty("model").GetString()!)) == key)
            ];
            Assert.True(members.Length >= 2, $"AUTO entry '{key}' has {members.Length} pinned counterpart(s)");

            // Limits are the floor across candidates. Advertising the best provider's 128k when
            // the next caps at 8k is how a request sized against this list dies on failover.
            Assert.Equal(members.Min(m => m.GetProperty("context_length").GetInt32()),
                         entry.GetProperty("context_length").GetInt32());
            Assert.Equal(members.Min(m => m.GetProperty("max_output_tokens").GetInt32()),
                         entry.GetProperty("max_output_tokens").GetInt32());
            Assert.Equal(members.All(m => m.GetProperty("supports_tools").GetBoolean()),
                         entry.GetProperty("supports_tools").GetBoolean());
        }
    }

    [Fact]
    public async Task ApiTags_PinnedEntries_KeepTheirProviderSuffix()
    {
        // Guards the half of the list that must not change: an explicit pick stays explicit.
        using JsonDocument doc = JsonDocument.Parse(
            await _fixture.Client.GetStringAsync("/api/tags"));

        foreach (JsonElement entry in doc.RootElement.GetProperty("models").EnumerateArray())
        {
            string name = entry.GetProperty("name").GetString()!;
            string id = entry.GetProperty("model").GetString()!;

            if (name.StartsWith("AUTO - ", StringComparison.Ordinal))
                continue;

            Assert.Contains('@', id);
            Assert.DoesNotContain("@auto:", id, StringComparison.Ordinal);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>"openai/gpt-oss-120b@groq:latest" → "openai/gpt-oss-120b".</summary>
    private static string StripPinnedId(string publishedId)
    {
        int at = publishedId.LastIndexOf('@');
        return at > 0 ? publishedId[..at] : publishedId;
    }

    /// <summary>
    /// A registry with exactly two providers, so candidate order is decided by the test rather
    /// than by whichever keys happen to sit in the developer's .env.
    /// </summary>
    private static ProviderRegistry NewRegistry(ProviderHealthService? health = null)
    {
        Environment.SetEnvironmentVariable("PROVIDER_GROQ_API_KEY", "test-groq");
        Environment.SetEnvironmentVariable("PROVIDER_CEREBRAS_API_KEY", "test-cerebras");
        return new ProviderRegistry(new ProviderHttpClientFactory(), health);
    }

    private static void SeedGptOssAlias(ProviderRegistry registry)
    {
        ProviderInfo groq = registry.Providers.First(p => p.Name == "groq");
        ProviderInfo cerebras = registry.Providers.First(p => p.Name == "cerebras");

        registry.UpdateModelMappings(
            new Dictionary<string, ProviderInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai/gpt-oss-120b"] = groq,
                ["openai/gpt-oss-120b@groq"] = groq,
                ["gpt-oss-120b@cerebras"] = cerebras,
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai/gpt-oss-120b"] = "openai/gpt-oss-120b",
                ["openai/gpt-oss-120b@groq"] = "openai/gpt-oss-120b",
                ["gpt-oss-120b@cerebras"] = "gpt-oss-120b",
            });

        registry.UpdateAutoAliases(new Dictionary<string, List<(ProviderInfo Provider, string UpstreamModel)>>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-oss-120b@auto"] = [(groq, "openai/gpt-oss-120b"), (cerebras, "gpt-oss-120b")],
        });
    }
}
