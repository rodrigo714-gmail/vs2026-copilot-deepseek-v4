using System.Net.Http;
using ProxyTests.FakeProviders;

namespace ProxyTests;

/// <summary>
/// Pruebas de diagnóstico para verificar FEHACIENTEMENTE que los modelos de
/// Ollama Cloud se enrutan al proveedor correcto según la selección configurada
/// en config/model-selection/*.json, y NO siempre caen en deepseek.
///
/// Estos tests ejercitan el flujo completo:
///   1. Fake providers devuelven modelos vía /v1/models o /api/tags
///   2. ModelCatalogService.RefreshAvailableModels() descubre y asigna
///   3. ProviderRegistry.ResolveProvider() devuelve el proveedor ganador
///   4. ProviderRegistry.ResolveCandidates() devuelve la lista de failover
///
/// Ninguno de estos tests toca la red; todo se ejecuta en memoria.
/// </summary>
[Collection("Proxy")]
public class RoutingDiagnosticTests : IDisposable
{
    private readonly Dictionary<string, string?> _envSnapshot;

    public RoutingDiagnosticTests()
    {
        // Snapshot and clear all provider env vars
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
            "PROVIDER_CEREBRAS_API_KEY", "PROVIDER_CEREBRAS_BASE_URL",
            "PROVIDER_ZENMUX_API_KEY", "PROVIDER_ZENMUX_BASE_URL",
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
            Environment.SetEnvironmentVariable(kv.Key, kv.Value);
    }

    private const string AnyKey = "test-key";

    /// <summary>
    /// Builds a ProviderRegistry + ModelCatalogService with fake in-memory providers.
    /// </summary>
    private static (ModelCatalogService catalog, ProviderRegistry registry, FakeProviderHandler handler)
        BuildCatalog(IDictionary<string, string[]> perProviderModels, IEnumerable<string>? ollamaProviders = null)
    {
        HashSet<string> ollama = new(ollamaProviders ?? [], StringComparer.OrdinalIgnoreCase);
        if (perProviderModels.Keys.Any(k => k.Equals("ollama", StringComparison.OrdinalIgnoreCase)))
            ollama.Add("ollama");

        FakeProviderHandler handler = new(perProviderModels, ollama);
        ProviderHttpClientFactory factory = new(handler);

        HashSet<string> requested = new(perProviderModels.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string name in requested)
        {
            string prefix = ProviderCapabilitiesRegistry.TryGet(name, out ProviderCapabilities caps)
                ? caps.EnvPrefix
                : name.ToUpperInvariant();
            string baseUrl = $"http://{name}.test/";
            Environment.SetEnvironmentVariable($"PROVIDER_{prefix}_API_KEY", AnyKey);
            if (ollama.Contains(name))
                Environment.SetEnvironmentVariable("PROVIDER_OLLAMA_API_KEY", null);
            Environment.SetEnvironmentVariable($"PROVIDER_{prefix}_BASE_URL", baseUrl);
        }

        bool ollamaRequested = requested.Contains("ollama");
        foreach (string provName in ProviderCapabilitiesRegistry.KnownProviders)
        {
            ProviderCapabilities caps = ProviderCapabilitiesRegistry.Get(provName);
            if (!requested.Contains(provName))
                Environment.SetEnvironmentVariable($"PROVIDER_{caps.EnvPrefix}_API_KEY", null);
        }
        if (!ollamaRequested)
            Environment.SetEnvironmentVariable("PROVIDER_OLLAMACLOUD_API_KEY", null);
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);

        ProviderRegistry registry = new(factory);
        ModelSelectionStore store = new();
        ModelCatalogService catalog = new(registry, store);
        return (catalog, registry, handler);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  DIAGNÓSTICO: Modelos exclusivos de Ollama Cloud
    //  Se sirven SOLO vía Ollama, nunca caen en deepseek.
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// kimi2.7-code solo está configurado en ollamacloud.json.
    /// deepseek.json NO tiene un match para "kimi". Debe enrutar a ollama.
    /// </summary>
    [Fact]
    public async Task Kimi27Code_OnlyInOllamaCloud_RoutesToOllama()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]>
            {
                ["deepseek"] = ["deepseek-v4-pro"],
                ["ollama"] = ["kimi2.7-code"],
            });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        // kimi2.7-code solo existe en ollamacloud.json (provider "ollama")
        ProviderInfo provider = registry.ResolveProvider("kimi2.7-code");
        Assert.Equal("ollama", provider.Name);
    }

    /// <summary>
    /// glm-5.2 solo está configurado en ollamacloud.json. Debe enrutar a ollama.
    /// </summary>
    [Fact]
    public async Task Glm52_OnlyInOllamaCloud_RoutesToOllama()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]>
            {
                ["deepseek"] = ["deepseek-v4-pro"],
                ["ollama"] = ["glm-5.2"],
            });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        ProviderInfo provider = registry.ResolveProvider("glm-5.2");
        Assert.Equal("ollama", provider.Name);
    }

    /// <summary>
    /// qwen3-coder:480b está SOLO en ollamacloud.json (no en deepseek, nvidia, etc.).
    /// Debe enrutar a ollama.
    /// </summary>
    [Fact]
    public async Task Qwen3Coder480B_OnlyInOllamaCloud_RoutesToOllama()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]>
            {
                ["deepseek"] = ["deepseek-v4-pro", "other-model"],
                ["ollama"] = ["qwen3-coder:480b"],
            });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        ProviderInfo provider = registry.ResolveProvider("qwen3-coder:480b");
        Assert.Equal("ollama", provider.Name);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  DIAGNÓSTICO: Modelo compartido entre Ollama Cloud y proveedor dedicado
    //  El que tiene menor priority (más preferido) gana.
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// kimi-k2.6 está en moonshot.json (prio 1) y en ollamacloud.json (prio 7).
    /// Debe enrutar a moonshot (prio 1 < 7), NO a ollama.
    /// </summary>
    [Fact]
    public async Task KimiK26_SharedBetweenMoonshotAndOllama_RoutesToMoonshot()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]>
            {
                ["moonshot"] = ["kimi-k2.6"],
                ["ollama"] = ["kimi-k2.6"],
            },
            ollamaProviders: ["ollama"]);

        await catalog.RefreshAvailableModels(CancellationToken.None);

        // moonshot.json tiene priority 1 para "kimi-k2.6"; ollamacloud.json tiene priority 7.
        // El menor número gana → moonshot.
        ProviderInfo provider = registry.ResolveProvider("kimi-k2.6");
        Assert.Equal("moonshot", provider.Name);

        // Failover: primero moonshot, luego ollama
        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands =
            registry.ResolveCandidates("kimi-k2.6");
        Assert.Equal(2, cands.Count);
        Assert.Equal("moonshot", cands[0].Provider.Name);
        Assert.Equal("ollama", cands[1].Provider.Name);
    }

    /// <summary>
    /// deepseek-v4-pro está en deepseek.json (prio 1) y en ollamacloud.json (prio 8).
    /// Debe enrutar a deepseek (prio 1 < 8), NO a ollama.
    /// </summary>
    [Fact]
    public async Task DeepSeekV4Pro_SharedBetweenDeepSeekAndOllama_RoutesToDeepSeek()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]>
            {
                ["deepseek"] = ["deepseek-v4-pro"],
                ["ollama"] = ["deepseek-v4-pro"],
            },
            ollamaProviders: ["ollama"]);

        await catalog.RefreshAvailableModels(CancellationToken.None);

        // deepseek.json tiene priority 1; ollamacloud.json tiene priority 8.
        // El menor número gana → deepseek.
        ProviderInfo provider = registry.ResolveProvider("deepseek-v4-pro");
        Assert.Equal("deepseek", provider.Name);
    }

    /// <summary>
    /// mistral-medium-3.5 solo está en ollamacloud.json. Debe enrutar a ollama.
    /// </summary>
    [Fact]
    public async Task MistralMedium35_OnlyInOllamaCloud_RoutesToOllama()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]>
            {
                ["deepseek"] = ["deepseek-v4-pro"],
                ["ollama"] = ["mistral-medium-3.5"],
            });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        ProviderInfo provider = registry.ResolveProvider("mistral-medium-3.5");
        Assert.Equal("ollama", provider.Name);
    }

    /// <summary>
    /// deepseek-v4-flash solo está en deepseek.json. Debe enrutar a deepseek.
    /// </summary>
    [Fact]
    public async Task DeepSeekV4Flash_OnlyInDeepSeek_RoutesToDeepSeek()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]>
            {
                ["deepseek"] = ["deepseek-v4-flash"],
                ["ollama"] = ["some-ollama-model"],
            });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        ProviderInfo provider = registry.ResolveProvider("deepseek-v4-flash");
        Assert.Equal("deepseek", provider.Name);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  DIAGNÓSTICO: El alias calificado "model@provider" siempre enruta
    //  al proveedor exacto, sin importar priorities.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task QualifiedAlias_DeepSeekV4ProAtOllama_RoutesToOllama()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]>
            {
                ["deepseek"] = ["deepseek-v4-pro"],
                ["ollama"] = ["deepseek-v4-pro"],
            },
            ollamaProviders: ["ollama"]);

        await catalog.RefreshAvailableModels(CancellationToken.None);

        // "deepseek-v4-pro@ollama" debe enrutar exactamente a ollama (1 solo candidato)
        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands =
            registry.ResolveCandidates("deepseek-v4-pro@ollama");
        Assert.Single(cands);
        Assert.Equal("ollama", cands[0].Provider.Name);
    }

    [Fact]
    public async Task QualifiedAlias_DeepSeekV4ProAtDeepSeek_RoutesToDeepSeek()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]>
            {
                ["deepseek"] = ["deepseek-v4-pro"],
                ["ollama"] = ["deepseek-v4-pro"],
            },
            ollamaProviders: ["ollama"]);

        await catalog.RefreshAvailableModels(CancellationToken.None);

        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands =
            registry.ResolveCandidates("deepseek-v4-pro@deepseek");
        Assert.Single(cands);
        Assert.Equal("deepseek", cands[0].Provider.Name);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  DIAGNÓSTICO: Cuando SOLO Ollama Cloud está configurado (sin deepseek),
    //  los modelos de ollamacloud se enrutan a ollama correctamente.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnlyOllamaCloud_NoOtherProvider_DeepSeekV4Pro_RoutesToOllama()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]>
            {
                ["ollama"] = ["deepseek-v4-pro", "kimi-k2.6", "qwen3-coder:480b", "devstral-2:123b"],
            },
            ollamaProviders: ["ollama"]);

        await catalog.RefreshAvailableModels(CancellationToken.None);

        // Sin deepseek configurado, ollamacloud toma el modelo
        Assert.Equal("ollama", registry.ResolveProvider("deepseek-v4-pro").Name);
        Assert.Equal("ollama", registry.ResolveProvider("kimi-k2.6").Name);
        Assert.Equal("ollama", registry.ResolveProvider("qwen3-coder:480b").Name);
        Assert.Equal("ollama", registry.ResolveProvider("devstral-2:123b").Name);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  DIAGNÓSTICO: /api/tags expone modelos con prefijo de proveedor
    //  para que Copilot BYOM pueda seleccionar el proveedor exacto.
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifica que cuando deepseek y ollama exponen deepseek-v4-pro,
    /// el endpoint /api/tags genera aliases con "@deepseek" y "@ollama"
    /// para que el usuario pueda elegir explícitamente.
    /// </summary>
    [Fact]
    public async Task ApiTags_ExposesQualifiedAliasesForDisambiguation()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]>
            {
                ["deepseek"] = ["deepseek-v4-pro"],
                ["ollama"] = ["deepseek-v4-pro"],
            },
            ollamaProviders: ["ollama"]);

        await catalog.RefreshAvailableModels(CancellationToken.None);

        // Ambos aliases calificados deben existir
        Assert.True(registry.ModelToProvider.ContainsKey("deepseek-v4-pro@deepseek"),
            "Expected qualified alias deepseek-v4-pro@deepseek");
        Assert.True(registry.ModelToProvider.ContainsKey("deepseek-v4-pro@ollama"),
            "Expected qualified alias deepseek-v4-pro@ollama");
    }
}