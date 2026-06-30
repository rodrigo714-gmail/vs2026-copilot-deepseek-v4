using System.Text.Json;

internal sealed class ModelCatalogService
{
    private readonly ProviderRegistry _providerRegistry;
    private readonly ModelSelectionStore _modelSelectionStore;
    private readonly TimeSpan _modelsRefreshInterval = TimeSpan.FromMinutes(5);

    public ModelCatalogService(ProviderRegistry providerRegistry, ModelSelectionStore modelSelectionStore)
    {
        _providerRegistry = providerRegistry;
        _modelSelectionStore = modelSelectionStore;
        AvailableModels = [_providerRegistry.DefaultModel];
        ModelsLastRefreshUtc = DateTime.MinValue;
        // Load static aliases from config immediately — these come from JSON config files,
        // not from provider API discovery, so they're available before any API call.
        LoadStaticAliasesFromConfig();
    }

    internal string[] AvailableModels { get; private set; }

    internal DateTime ModelsLastRefreshUtc { get; private set; }

    internal async Task RefreshAvailableModelsIfNeeded(CancellationToken ct)
    {
        if (DateTime.UtcNow - ModelsLastRefreshUtc < _modelsRefreshInterval)
            return;
        await RefreshAvailableModels(ct);
    }

    internal async Task RefreshAvailableModels(CancellationToken ct)
    {
        try
        {
            var claimsByUpstream = new Dictionary<string, List<Claimant>>(StringComparer.OrdinalIgnoreCase);

            foreach (ProviderInfo prov in _providerRegistry.Providers)
            {
                string[] discovered = await TryGetModelsFromProvider(prov, ct);
                foreach (string m in discovered)
                {
                    if (string.IsNullOrWhiteSpace(m)) continue;
                    if (!_modelSelectionStore.IsPreferredModel(m, prov.Name)) continue;
                    (int ctx, _, _, _, _, _) = GetModelProfile(m);
                    if (ctx == 0) continue;
                    int prio = _modelSelectionStore.GetPreferredModelPriority(m, prov.Name);
                    if (!claimsByUpstream.TryGetValue(m, out var list))
                    {
                        list = [];
                        claimsByUpstream[m] = list;
                    }
                    list.Add(new Claimant(prov, m, prio));
                }
            }

            var providerOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _providerRegistry.Providers.Count; i++)
                providerOrder[_providerRegistry.Providers[i].Name] = i;

            var newMap = new Dictionary<string, ProviderInfo>(StringComparer.OrdinalIgnoreCase);
            var newUpstream = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var allModels = new List<string>();

            foreach (var kv in claimsByUpstream)
            {
                var ordered = kv.Value
                    .OrderBy(c => c.Priority)
                    .ThenBy(c => providerOrder.TryGetValue(c.Provider.Name, out int o) ? o : int.MaxValue)
                    .ToList();

                foreach (var c in ordered)
                {
                    string qualified = $"{c.UpstreamId}@{c.Provider.Name}";
                    if (!newMap.ContainsKey(qualified))
                    {
                        newMap[qualified] = c.Provider;
                        newUpstream[qualified] = c.UpstreamId;
                        allModels.Add(qualified);
                    }
                }

                var winner = ordered[0];
                string bare = winner.UpstreamId;
                if (!newMap.ContainsKey(bare))
                {
                    newMap[bare] = winner.Provider;
                    newUpstream[bare] = bare;
                    allModels.Add(bare);
                }
            }

            // Always merge static aliases from config
            MergeAliases(newMap, newUpstream, allModels);

            if (allModels.Count > 0)
            {
                AvailableModels = [.. allModels.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];

                var upstreamToProviders = new Dictionary<string, List<ProviderInfo>>(StringComparer.OrdinalIgnoreCase);
                foreach (string upstreamId in claimsByUpstream.Keys)
                {
                    var ordered = claimsByUpstream[upstreamId]
                        .OrderBy(c => c.Priority)
                        .ThenBy(c => providerOrder.TryGetValue(c.Provider.Name, out int o) ? o : int.MaxValue)
                        .ToList();
                    upstreamToProviders[upstreamId] = [.. ordered.Select(c => c.Provider).Distinct(ProviderInfoNameComparer.Instance)];
                }
                _providerRegistry.UpdateModelMappings(newMap, newUpstream, upstreamToProviders);
                ModelsLastRefreshUtc = DateTime.UtcNow;
            }
        }
        catch { }
    }

    /// <summary>
    /// Loads model aliases from config files (the "upstream" field in model-selection/*.json).
    /// These are available immediately at startup, before any provider API calls.
    /// </summary>
    private void LoadStaticAliasesFromConfig()
    {
        var newMap = new Dictionary<string, ProviderInfo>(_providerRegistry.ModelToProvider, StringComparer.OrdinalIgnoreCase);
        var newUpstream = new Dictionary<string, string>(_providerRegistry.ModelToUpstream, StringComparer.OrdinalIgnoreCase);
        var models = new List<string>(newMap.Keys);

        foreach (var provider in _providerRegistry.Providers)
        {
            var entries = _modelSelectionStore.GetProviderModelSelections(provider.Name);
            foreach (var entry in entries)
            {
                if (!entry.Enabled) continue;

                // Upstream defaults to match when not specified
                string up = string.IsNullOrWhiteSpace(entry.Upstream) ? entry.Match : entry.Upstream;

                // Always register a qualified alias: match@provider
                string qualified = $"{entry.Match}@{provider.Name}";
                if (!newMap.ContainsKey(qualified))
                {
                    newMap[qualified] = provider;
                    newUpstream[qualified] = up;
                    if (!models.Contains(qualified)) models.Add(qualified);
                }

                // Register upstream if different from match
                if (!string.Equals(entry.Match, up, StringComparison.OrdinalIgnoreCase))
                {
                    if (!newMap.ContainsKey(up))
                    {
                        newMap[up] = provider;
                        newUpstream[up] = up;
                        if (!models.Contains(up)) models.Add(up);
                    }
                }

                // Register bare match
                if (!newMap.ContainsKey(entry.Match))
                {
                    newMap[entry.Match] = provider;
                    newUpstream[entry.Match] = up;
                    if (!models.Contains(entry.Match)) models.Add(entry.Match);
                }
            }
        }

        if (models.Count > 0)
        {
            _providerRegistry.UpdateModelMappings(newMap, newUpstream);
            string defaultModel = _providerRegistry.DefaultModel;
            AvailableModels = [defaultModel, .. models.Where(m => !string.Equals(m, defaultModel, StringComparison.OrdinalIgnoreCase)).OrderBy(m => m)];
        }
    }

    /// <summary>
    /// Merges alias entries from config into discovered model data.
    /// </summary>
    private void MergeAliases(Dictionary<string, ProviderInfo> map, Dictionary<string, string> upstream, List<string> models)
    {
        foreach (var provider in _providerRegistry.Providers)
        {
            var entries = _modelSelectionStore.GetProviderModelSelections(provider.Name);
            foreach (var entry in entries)
            {
                if (!entry.Enabled) continue;

                // Upstream defaults to match when not specified
                string up = string.IsNullOrWhiteSpace(entry.Upstream) ? entry.Match : entry.Upstream;

                // Always register a qualified alias: match@provider
                string qualified = $"{entry.Match}@{provider.Name}";
                if (!map.ContainsKey(qualified))
                {
                    map[qualified] = provider;
                    upstream[qualified] = up;
                    if (!models.Contains(qualified)) models.Add(qualified);
                }

                // Register upstream if different from match
                if (!string.Equals(entry.Match, up, StringComparison.OrdinalIgnoreCase))
                {
                    if (!map.ContainsKey(up))
                    {
                        map[up] = provider;
                        upstream[up] = up;
                        if (!models.Contains(up)) models.Add(up);
                    }
                }

                // Register bare match
                if (!map.ContainsKey(entry.Match))
                {
                    map[entry.Match] = provider;
                    upstream[entry.Match] = up;
                    if (!models.Contains(entry.Match)) models.Add(entry.Match);
                }
            }
        }
    }

    private readonly record struct Claimant(ProviderInfo Provider, string UpstreamId, int Priority);

    private sealed class ProviderInfoNameComparer : IEqualityComparer<ProviderInfo>
    {
        public static readonly ProviderInfoNameComparer Instance = new();
        public bool Equals(ProviderInfo x, ProviderInfo y) =>
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(ProviderInfo obj) =>
            obj.Name is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name);
    }

    internal (int ContextLength, int MaxOutputTokens, bool SupportsTools, bool SupportsVision, string[] Capabilities, string Family) GetModelProfile(string model)
    {
        ModelExecutionConfig configured = _modelSelectionStore.GetExecutionConfigForModel(model, _providerRegistry.ModelToProvider);
        string m = model.ToLowerInvariant();
        bool tools = configured.SupportsTools ?? true;
        bool vision = configured.SupportsVision ?? (m.Contains("vision") || m.Contains("-vl") || m.Contains("neva") || m.Contains("vila") || m.Contains("fuyu") || m.Contains("kosmos"));
        int ctx, maxOut;

        if (m.Contains("guard") || m.Contains("safety") || m.Contains("embed") || m.Contains("retriever") || m.Contains("reranker") || m.Contains("reward") || m.Contains("parse") || m.Contains("detector") || m.Contains("clip") || m.Contains("nv-embed") || m.Contains("embedqa") || m.Contains("cached-model") || m.Contains("rerank") || m.Contains("classification") || m.Contains("riva-translate") || m.Contains("synthetic-video"))
        { ctx = 0; maxOut = 0; tools = false; }
        else if (m.Contains("deepseek")) { ctx = 1_000_000; maxOut = 384_000; }
        else if (m.Contains("nemotron-3-super")) { ctx = 1_000_000; maxOut = 16384; }
        else if (m.Contains("nemotron") && m.Contains("ultra")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("nemotron") || m.Contains("nvidia-nemotron")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("llama-4") || m.Contains("llama-3.3") || m.Contains("llama-3.2") || m.Contains("llama-3.1")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("llama-2") || m.Contains("codellama")) { ctx = 4096; maxOut = 4096; }
        else if (m.Contains("mistral-large-3") || m.Contains("mistral-large-2") || m.Contains("mistral-large")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("mistral") && (m.Contains("medium") || m.Contains("small"))) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("mixtral-8x22b")) { ctx = 65536; maxOut = 4096; }
        else if (m.Contains("mixtral") || m.Contains("mistral") || m.Contains("codestral") || m.Contains("ministral") || m.Contains("mistral-nemo")) { ctx = 32768; maxOut = 4096; }
        else if (m.Contains("qwen3-coder")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("qwen")) { ctx = 128_000; maxOut = 8192; }
        else if (m.Contains("gemma-4")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("gemma-3")) { ctx = 32768; maxOut = 8192; }
        else if (m.Contains("gemma-2") || m.Contains("gemma-2b") || m.Contains("codegemma")) { ctx = 8192; maxOut = 4096; }
        else if (m.Contains("phi-4")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("phi-3")) { ctx = 128_000; maxOut = 4096; }
        else if (m.Contains("granite-34b-code")) { ctx = 128_000; maxOut = 4096; }
        else if (m.Contains("granite")) { ctx = 128_000; maxOut = 4096; }
        else if (m.Contains("starcoder2")) { ctx = 16384; maxOut = 4096; }
        else if (m.Contains("gpt-oss")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("dbrx") || m.Contains("jamba")) { ctx = 32768; maxOut = 4096; }
        else if (m.Contains("yi-large") || m.Contains("seed-oss")) { ctx = 32768; maxOut = 4096; }
        else if (m.Contains("kimi")) { ctx = 128_000; maxOut = 8192; }
        else if (m.Contains("step-3")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("zai-glm")) { ctx = 128_000; maxOut = 32768; }
        else if (m.Contains("glm")) { ctx = 128_000; maxOut = 32768; }
        else if (m.Contains("minimax")) { ctx = 128_000; maxOut = 32768; }
        else if (m.Contains("cogito")) { ctx = 128_000; maxOut = 32768; }
        else if (m.Contains("solar") || m.Contains("zamba")) { ctx = 4096; maxOut = 4096; }
        else if (m.Contains("palmyra")) { ctx = 32768; maxOut = 4096; }
        else { ctx = 128_000; maxOut = 8192; }

        ctx = configured.ContextLength ?? ctx;
        maxOut = configured.MaxOutputTokens ?? maxOut;
        string[] capabilities = vision ? ["completion", "tools", "vision"] : ["completion", "tools"];
        string family = configured.Family
            ?? (m.Contains("deepseek") ? "deepseek"
            : m.Contains("nemotron") || m.Contains("llama-3.1-nemotron") || m.Contains("llama-3.3-nemotron") || m.Contains("nvidia-nemotron") || m.Contains("cosmos-reason") ? "nvidia"
            : m.Contains("llama") || m.Contains("codellama") ? "meta"
            : m.Contains("mistral") || m.Contains("mixtral") || m.Contains("codestral") || m.Contains("ministral") ? "mistralai"
            : m.Contains("qwen") ? "qwen"
            : m.Contains("gemma") || m.Contains("codegemma") ? "google"
            : m.Contains("phi-") ? "microsoft"
            : m.Contains("granite") ? "ibm"
            : m.Contains("gpt-oss") ? "openai"
            : m.Contains("nemotron") ? "nvidia"
            : _providerRegistry.ModelToProvider.TryGetValue(model, out ProviderInfo prov) ? prov.Name
            : "api");
        return (ctx, maxOut, tools, vision, capabilities, family);
    }

    internal CancellationTokenSource? CreateModelTimeoutCts(string model, CancellationToken outer)
    {
        var exec = _modelSelectionStore.GetExecutionConfigForModel(model, _providerRegistry.ModelToProvider);
        if (!exec.TimeoutSeconds.HasValue || exec.TimeoutSeconds.Value <= 0)
            return null;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
        linked.CancelAfter(TimeSpan.FromSeconds(exec.TimeoutSeconds.Value));
        return linked;
    }

    internal ModelExecutionConfig GetExecutionConfigForModel(string model) =>
        _modelSelectionStore.GetExecutionConfigForModel(model, _providerRegistry.ModelToProvider);

    internal void EnsureDefaultModelPresent(CancellationToken ct)
    {
        string defaultModel = _providerRegistry.DefaultModel;
        if (AvailableModels.Any(m => string.Equals(m, defaultModel, StringComparison.OrdinalIgnoreCase)))
            return;

        foreach (ProviderInfo prov in _providerRegistry.Providers)
        {
            if (!_modelSelectionStore.IsPreferredModel(defaultModel, prov.Name))
                continue;

            var newMap = new Dictionary<string, ProviderInfo>(_providerRegistry.ModelToProvider, StringComparer.OrdinalIgnoreCase);
            var newUpstream = new Dictionary<string, string>(_providerRegistry.ModelToUpstream, StringComparer.OrdinalIgnoreCase);

            if (!newMap.ContainsKey(defaultModel))
            {
                newMap[defaultModel] = prov;
                newUpstream[defaultModel] = defaultModel;
            }
            string qualified = $"{defaultModel}@{prov.Name}";
            if (!newMap.ContainsKey(qualified))
            {
                newMap[qualified] = prov;
                newUpstream[qualified] = defaultModel;
            }
            _providerRegistry.UpdateModelMappings(newMap, newUpstream);
            AvailableModels = [defaultModel, .. AvailableModels.Where(m => !string.Equals(m, defaultModel, StringComparison.OrdinalIgnoreCase))];
            return;
        }

        if (_providerRegistry.Providers.Count > 0)
        {
            var first = _providerRegistry.Providers[0];
            var newMap = new Dictionary<string, ProviderInfo>(_providerRegistry.ModelToProvider, StringComparer.OrdinalIgnoreCase);
            var newUpstream = new Dictionary<string, string>(_providerRegistry.ModelToUpstream, StringComparer.OrdinalIgnoreCase);
            newMap[defaultModel] = first;
            newUpstream[defaultModel] = defaultModel;
            _providerRegistry.UpdateModelMappings(newMap, newUpstream);
            AvailableModels = [defaultModel, .. AvailableModels.Where(m => !string.Equals(m, defaultModel, StringComparison.OrdinalIgnoreCase))];
        }
    }

    private static async Task<string[]> TryGetModelsFromProvider(ProviderInfo provider, CancellationToken ct)
    {
        try
        {
            string modelsPath = provider.Capabilities.ModelsPath;
            using var resp = await provider.Client.GetAsync(modelsPath, ct);
            if (!resp.IsSuccessStatusCode) return [];
            string body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return ExtractModels(doc.RootElement);
        }
        catch { return []; }
    }

    private static string[] ExtractModels(JsonElement root)
    {
        IEnumerable<JsonElement> items = [];
        if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
            items = data.EnumerateArray();
        else if (root.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array)
            items = models.EnumerateArray();

        return items
            .Select(item =>
            {
                if (item.ValueKind == JsonValueKind.String) return item.GetString();
                if (item.ValueKind != JsonValueKind.Object) return null;
                if (item.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String) return id.GetString();
                if (item.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String) return name.GetString();
                if (item.TryGetProperty("model", out JsonElement model) && model.ValueKind == JsonValueKind.String) return model.GetString();
                return null;
            })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}