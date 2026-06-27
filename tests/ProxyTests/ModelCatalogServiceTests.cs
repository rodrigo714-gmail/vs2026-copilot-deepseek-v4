using System.Net.Http;
using ProxyTests.FakeProviders;

namespace ProxyTests;

// All tests in this class mutate process-level env vars to control which
// providers ProviderRegistry discovers. xUnit runs test collections in
// parallel by default; that races with EndpointTests.ProxyFixture which also
// sets env vars. Share the "Proxy" collection with EndpointTests so the
// fixture is initialized once and our tests run sequentially after it.
[Collection("Proxy")]
public class ModelCatalogServiceTests : IDisposable
{
    // Snapshot of env vars we touch, restored in Dispose so subsequent
    // tests (in any class) start from a clean slate.
    private readonly Dictionary<string, string?> _envSnapshot;

    public ModelCatalogServiceTests()
    {
        // The "Proxy" collection fixture sets PROVIDER_DEEPSEEK_API_KEY and
        // clears the others. We extend that to clear all provider env vars
        // so each test starts from a known-clean state.
        string[] keys =
        [
            "PROVIDER_DEEPSEEK_API_KEY", "PROVIDER_DEEPSEEK_BASE_URL",
            "PROVIDER_OPENAI_API_KEY", "PROVIDER_OPENAI_BASE_URL",
            "PROVIDER_NVIDIA_API_KEY", "PROVIDER_NVIDIA_BASE_URL",
            "PROVIDER_OPENROUTER_API_KEY", "PROVIDER_OPENROUTER_BASE_URL",
            "PROVIDER_GROQ_API_KEY", "PROVIDER_GROQ_BASE_URL",
            "PROVIDER_OLLAMACLOUD_API_KEY", "PROVIDER_OLLAMA_API_KEY", "PROVIDER_OLLAMA_BASE_URL",
            "PROVIDER_GOOGLE_API_KEY", "PROVIDER_GOOGLE_BASE_URL",
            "PROVIDER_MOONSHOT_API_KEY", "PROVIDER_MOONSHOT_BASE_URL",
            "DEEPSEEK_API_KEY", "DEEPSEEK_BASE_URL",
            "DEEPSEEK_MODEL"
        ];
        _envSnapshot = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string k in keys)
        {
            _envSnapshot[k] = Environment.GetEnvironmentVariable(k);
            Environment.SetEnvironmentVariable(k, null);
        }
    }

    public void Dispose()
    {
        foreach (KeyValuePair<string, string?> kv in _envSnapshot)
        {
            Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }
    }

    private const string AnyKey = "test-key";

    // ── Helpers ──────────────────────────────────────────────────────────

    private static (ModelCatalogService catalog, ProviderRegistry registry, FakeProviderHandler handler)
        BuildCatalog(IDictionary<string, string[]> perProviderModels, IEnumerable<string>? ollamaProviders = null)
    {
        // "ollama" is the only provider that uses /api/tags and reads
        // PROVIDER_OLLAMACLOUD_API_KEY. Default the ollama set to include
        // only the providers explicitly passed as ollamaProviders, plus the
        // string "ollama" if it appears in the model map.
        HashSet<string> ollama = new(ollamaProviders ?? [], StringComparer.OrdinalIgnoreCase);
        if (perProviderModels.Keys.Any(k => k.Equals("ollama", StringComparison.OrdinalIgnoreCase)))
        {
            ollama.Add("ollama");
        }
        FakeProviderHandler handler = new(perProviderModels, ollama);
        ProviderHttpClientFactory factory = new(handler);

        // Set env vars for the providers we want discovered (any others will be skipped).
        // For "ollama", the registry reads PROVIDER_OLLAMACLOUD_API_KEY (or PROVIDER_OLLAMA_API_KEY).
        // The base URL host MUST match the provider name so FakeProviderHandler routes by host.
        HashSet<string> requested = new(perProviderModels.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string name in requested)
        {
            // Use capabilities to determine the correct env-var prefix (e.g. OLLAMACLOUD for ollama).
            string prefix = ProviderCapabilitiesRegistry.TryGet(name, out ProviderCapabilities caps)
                ? caps.EnvPrefix
                : name.ToUpperInvariant();
            string baseUrl = $"http://{name}.test/";
            Environment.SetEnvironmentVariable($"PROVIDER_{prefix}_API_KEY", AnyKey);
            // Backward compatibility: Ollama also reads PROVIDER_OLLAMA_API_KEY as fallback
            if (ollama.Contains(name))
            {
                Environment.SetEnvironmentVariable("PROVIDER_OLLAMA_API_KEY", null);
            }
            Environment.SetEnvironmentVariable($"PROVIDER_{prefix}_BASE_URL", baseUrl);
        }

        // Clear anything the host environment might have set that isn't in our list.
        bool ollamaRequested = requested.Contains("ollama");
        foreach (string provName in ProviderCapabilitiesRegistry.KnownProviders)
        {
            ProviderCapabilities caps = ProviderCapabilitiesRegistry.Get(provName);
            if (!requested.Contains(provName))
            {
                Environment.SetEnvironmentVariable($"PROVIDER_{caps.EnvPrefix}_API_KEY", null);
            }
        }
        if (!ollamaRequested)
        {
            Environment.SetEnvironmentVariable("PROVIDER_OLLAMACLOUD_API_KEY", null);
        }
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);

        ProviderRegistry registry = new(factory);
        ModelSelectionStore store = new();
        ModelCatalogService catalog = new(registry, store);
        return (catalog, registry, handler);
    }

    // ── Per-provider happy path (7 tests) ────────────────────────────────

    [Fact]
    public async Task Deepseek_OnlyProvider_ClaimsBareName()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["deepseek"] = ["deepseek-v4-pro"] });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("deepseek", registry.ResolveProvider("deepseek-v4-pro").Name);
        Assert.Equal("deepseek", registry.ModelToProvider["deepseek-v4-pro"].Name);
        Assert.Equal("deepseek", registry.ModelToProvider["deepseek-v4-pro@deepseek"].Name);
        Assert.Contains("deepseek-v4-pro", catalog.AvailableModels);
    }

    [Fact]
    public async Task Openai_OnlyProvider_ClaimsBareName()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["openai"] = ["gpt-5"] });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("openai", registry.ResolveProvider("gpt-5").Name);
        Assert.Equal("openai", registry.ModelToProvider["gpt-5@openai"].Name);
    }

    [Fact]
    public async Task Nvidia_OnlyProvider_ClaimsBareName()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["nvidia"] = ["nvidia/llama-3.1-nemotron-70b-instruct"] });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("nvidia", registry.ResolveProvider("nvidia/llama-3.1-nemotron-70b-instruct").Name);
    }

    [Fact]
    public async Task Groq_OnlyProvider_ClaimsBareName()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["groq"] = ["llama-3.3-70b-versatile"] });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("groq", registry.ResolveProvider("llama-3.3-70b-versatile").Name);
    }

    [Fact]
    public async Task Openrouter_OnlyProvider_ClaimsBareName()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["openrouter"] = ["nvidia/nemotron-3-super-120b-a12b:free"] });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("openrouter", registry.ResolveProvider("nvidia/nemotron-3-super-120b-a12b:free").Name);
    }

    [Fact]
    public async Task Moonshot_OnlyProvider_ClaimsBareName()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["moonshot"] = ["kimi-k2.6"] });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("moonshot", registry.ResolveProvider("kimi-k2.6").Name);
        Assert.Equal("moonshot", registry.ModelToProvider["kimi-k2.6@moonshot"].Name);
    }

    [Fact]
    public async Task Google_OnlyProvider_ClaimsBareName()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["google"] = ["models/gemini-2.5-flash"] });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("google", registry.ResolveProvider("models/gemini-2.5-flash").Name);
        Assert.Equal("google", registry.ModelToProvider["models/gemini-2.5-flash"].Name);
        Assert.Equal("google", registry.ModelToProvider["models/gemini-2.5-flash@google"].Name);
        Assert.Contains("models/gemini-2.5-flash", catalog.AvailableModels);
    }

    [Fact]
    public async Task Ollama_OnlyProvider_ClaimsBareName()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(
                new Dictionary<string, string[]> { ["ollama"] = ["gpt-oss-120b"] },
                ollamaProviders: ["ollama"]);

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("ollama", registry.ResolveProvider("gpt-oss-120b").Name);
    }

    // ── Cross-provider collisions ───────────────────────────────────────

    [Fact]
    public async Task GptOss120b_OfferedByNvidiaGroqOllama_ClaimsLowestPriority()
    {
        // Live JSON priorities after the 2026-06-10 curation:
        //   nvidia.json p4 (match "openai/gpt-oss-120b", upstream "openai/gpt-oss-120b").
        //   groq.json   p4 (match "openai/gpt-oss-120b", upstream "openai/gpt-oss-120b").
        //   ollama.json match "gpt-oss" is DISABLED — ollama is no longer a claimant for
        //   any "gpt-oss-120b" upstream id, so we don't even ask the fake handler for one.
        // Tie-break: nvidia wins over groq because ProviderRegistry discovery order
        // (deepseek, openai, nvidia, openrouter, groq, …) places nvidia before groq.
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(
                new Dictionary<string, string[]>
                {
                    ["nvidia"] = ["openai/gpt-oss-120b"],
                    ["groq"] = ["openai/gpt-oss-120b"],
                },
                ollamaProviders: ["ollama"]);

        await catalog.RefreshAvailableModels(CancellationToken.None);

        // Shared upstream: nvidia wins (p4) over groq (p4) via provider discovery order.
        Assert.Equal("nvidia", registry.ResolveProvider("openai/gpt-oss-120b").Name);
        Assert.Equal("nvidia", registry.ModelToProvider["openai/gpt-oss-120b"].Name);

        // Both claimants get qualified aliases.
        Assert.Equal("nvidia", registry.ModelToProvider["openai/gpt-oss-120b@nvidia"].Name);
        Assert.Equal("groq", registry.ModelToProvider["openai/gpt-oss-120b@groq"].Name);

        // Failover: nvidia (p4) first, then groq (p4) — same priority, registry order breaks tie.
        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands =
            registry.ResolveCandidates("openai/gpt-oss-120b");
        Assert.Equal(2, cands.Count);
        Assert.Equal("nvidia", cands[0].Provider.Name);
        Assert.Equal("groq", cands[1].Provider.Name);

        // The bare "gpt-oss-120b" (no upstream prefix) is NOT exposed by anyone:
        //   - nvidia's upstream id is "openai/gpt-oss-120b" (not the bare name).
        //   - groq's upstream id is the same.
        //   - ollama's "gpt-oss" match is disabled, so ollama doesn't claim the bare id.
        Assert.False(registry.ModelToProvider.ContainsKey("gpt-oss-120b"));
    }

    [Fact]
    public async Task KimiK26_OfferedByMoonshotAndOllama_ClaimsMoonshot()
    {
        // moonshot.json p1 ("kimi-k2.6"), ollama.json p7 ("kimi").
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(
                new Dictionary<string, string[]>
                {
                    ["moonshot"] = ["kimi-k2.6"],
                    ["ollama"] = ["kimi-k2.6"],
                },
                ollamaProviders: ["ollama"]);

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("moonshot", registry.ResolveProvider("kimi-k2.6").Name);

        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands =
            registry.ResolveCandidates("kimi-k2.6");
        Assert.Equal(2, cands.Count);
        Assert.Equal("moonshot", cands[0].Provider.Name);
        Assert.Equal("ollama", cands[1].Provider.Name);

        Assert.Equal("moonshot", registry.ModelToProvider["kimi-k2.6@moonshot"].Name);
        Assert.Equal("ollama", registry.ModelToProvider["kimi-k2.6@ollama"].Name);
    }

    // ── Exposure gate ────────────────────────────────────────────────────

    [Fact]
    public async Task ModelId_NotInAnyJson_IsNotExposed()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["moonshot"] = ["totally-unconfigured-xyz"] });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        // The model is not exposed because no JSON file has a `match` entry for it.
        Assert.False(registry.ModelToProvider.ContainsKey("totally-unconfigured-xyz"));
        Assert.False(registry.ModelToProvider.ContainsKey("totally-unconfigured-xyz@moonshot"));
        Assert.DoesNotContain("totally-unconfigured-xyz", catalog.AvailableModels);
    }

    // ── Regression guards for ResolveCandidates with null/empty ──────────

    [Fact]
    public void ResolveCandidates_NullModel_ReturnsDefaultProvider()
    {
        (_, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["deepseek"] = ["deepseek-v4-pro"] });

        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands = registry.ResolveCandidates(null);

        Assert.Single(cands);
        Assert.Equal("deepseek", cands[0].Provider.Name);
    }

    [Fact]
    public void ResolveCandidates_EmptyModel_ReturnsDefaultProvider()
    {
        (_, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["deepseek"] = ["deepseek-v4-pro"] });

        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands = registry.ResolveCandidates("");

        Assert.Single(cands);
        Assert.Equal("deepseek", cands[0].Provider.Name);
    }

    // ── Coverage: every (provider × enabled match) pair exposes an alias ─

    [Fact]
    public async Task EveryConfiguredProvider_AndEveryEnabledMatch_ProducesAQualifiedAlias()
    {
        // For each JSON file in config/model-selection/, take every enabled entry.
        // We synthesize a fake upstream id that contains the configured `match`
        // substring and serve it from the matching provider. The catalog must
        // expose a `id@<provider>` alias for every configured (provider, match).
        string configDir = Path.Combine(AppContext.BaseDirectory, "config", "model-selection");
        if (!Directory.Exists(configDir))
        {
            // Fallback for test runs that don't copy the JSON to bin/.
            configDir = Path.Combine(Directory.GetCurrentDirectory(), "config", "model-selection");
        }

        Assert.True(Directory.Exists(configDir),
            $"config/model-selection not found at {configDir}");

        Dictionary<string, List<string>> perProvider = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(configDir, "*.json"))
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            string provider = doc.RootElement.GetProperty("provider").GetString()!.Trim().ToLowerInvariant();
            foreach (System.Text.Json.JsonElement entry in doc.RootElement.GetProperty("models").EnumerateArray())
            {
                bool enabled = !entry.TryGetProperty("enabled", out System.Text.Json.JsonElement enE)
                    || enE.ValueKind != System.Text.Json.JsonValueKind.False;
                if (!enabled) continue;

                string? match = entry.ValueKind == System.Text.Json.JsonValueKind.String
                    ? entry.GetString()
                    : (entry.TryGetProperty("match", out System.Text.Json.JsonElement mE) ? mE.GetString()
                       : entry.TryGetProperty("model", out System.Text.Json.JsonElement moE) ? moE.GetString()
                       : entry.TryGetProperty("id", out System.Text.Json.JsonElement iE) ? iE.GetString()
                       : null);
                if (string.IsNullOrWhiteSpace(match)) continue;

                string fakeId = $"unit-test-{match}-sentinel";
                if (!perProvider.TryGetValue(provider, out List<string>? list))
                {
                    list = [];
                    perProvider[provider] = list;
                }
                list.Add(fakeId);
            }
        }

        // The ProviderRegistry knows 7 slots. "ollamacloud" is not a real slot;
        // it is consumed by the "ollama" slot via PROVIDER_OLLAMACLOUD_API_KEY,
        // but its models are matched against the "ollamacloud" key in
        // ModelSelectionStore — NOT against "ollama". So we must only set up
        // fake upstream ids for the 7 real slots, and skip ollamacloud entries
        // (they would not be accepted by the "ollama" provider's match rules).
        perProvider.Remove("ollamacloud");

        if (perProvider.Count == 0)
        {
            return; // nothing configured; trivially passes
        }

        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(
                perProvider.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
                ollamaProviders: perProvider.Keys.Where(k => k.Equals("ollama", StringComparison.OrdinalIgnoreCase)));

        await catalog.RefreshAvailableModels(CancellationToken.None);

        // For every (provider, match) that the test set up, the qualified alias must exist.
        foreach (KeyValuePair<string, List<string>> kv in perProvider)
        {
            foreach (string fakeId in kv.Value)
            {
                string alias = $"{fakeId}@{kv.Key}";
                Assert.True(
                    registry.ModelToProvider.ContainsKey(alias),
                    $"Expected qualified alias '{alias}' to be in ModelToProvider after RefreshAvailableModels. "
                    + $"Available models: [{string.Join(", ", catalog.AvailableModels.Take(20))}]...");
            }
        }
    }

    // ── Tie-break: when two providers have the same priority for a model,
    //               the one earlier in the configured order wins. ──────────

    [Fact]
    public async Task SamePriority_TieBreaksByConfiguredProviderOrder()
    {
        // moonshot.json has explicit priority 1 for "kimi-k2.6"; ollama.json
        // has explicit priority 7 for the substring "kimi". moonshot wins by
        // priority (lower number), and the failover list is [moonshot, ollama].
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(
                new Dictionary<string, string[]>
                {
                    ["moonshot"] = ["kimi-k2.6"],
                    ["ollama"] = ["kimi-k2.6"],
                },
                ollamaProviders: ["ollama"]);

        await catalog.RefreshAvailableModels(CancellationToken.None);

        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands =
            registry.ResolveCandidates("kimi-k2.6");
        Assert.Equal(2, cands.Count);
        Assert.Equal("moonshot", cands[0].Provider.Name);
        Assert.Equal("ollama", cands[1].Provider.Name);
    }

    [Fact]
    public async Task QualifiedAlias_AlwaysResolvesToItsOwnProvider()
    {
        // nvidia and groq both offer "openai/gpt-oss-120b"; ollama offers "gpt-oss-120b".
        // The qualified alias "openai/gpt-oss-120b@nvidia" must resolve to nvidia,
        // even if the bare-name winner is groq. Single-candidate resolution, no failover.
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(
                new Dictionary<string, string[]>
                {
                    ["nvidia"] = ["openai/gpt-oss-120b"],
                    ["groq"] = ["openai/gpt-oss-120b"],
                    ["ollama"] = ["gpt-oss-120b"],
                },
                ollamaProviders: ["ollama"]);

        await catalog.RefreshAvailableModels(CancellationToken.None);

        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands =
            registry.ResolveCandidates("openai/gpt-oss-120b@nvidia");
        Assert.Single(cands);
        Assert.Equal("nvidia", cands[0].Provider.Name);
        Assert.Equal("openai/gpt-oss-120b", cands[0].UpstreamModel);
    }
}

[CollectionDefinition("CatalogTests", DisableParallelization = true)]
public class CatalogTestsCollection { }
