internal sealed class ProviderRegistry
{
    private readonly List<ProviderInfo> _providers = [];
    private Dictionary<string, ProviderInfo> _modelToProvider = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _modelToUpstream = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<ProviderInfo>> _upstreamToProviders = new(StringComparer.OrdinalIgnoreCase);

    public ProviderRegistry(ProviderHttpClientFactory httpClientFactory)
    {
        DefaultModel = Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? "deepseek-v4-pro";
        DiscoverProviders(httpClientFactory);

        if (_providers.Count == 0)
        {
            Console.WriteLine("╔══════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  ⚠️  No se encontraron API keys configuradas.                    ║");
            Console.WriteLine("║                                                                  ║");
            Console.WriteLine("║  Edita el archivo .env en la raíz del proyecto y descomenta     ║");
            Console.WriteLine("║  al menos un proveedor, por ejemplo:                            ║");
            Console.WriteLine("║                                                                  ║");
            Console.WriteLine("║    PROVIDER_DEEPSEEK_API_KEY=sk-tu-key-aqui                      ║");
            Console.WriteLine("║                                                                  ║");
            Console.WriteLine("║  Luego reinicia la aplicación.                                   ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════════╝");
        }
    }

    internal string DefaultModel { get; }

    internal IReadOnlyList<ProviderInfo> Providers => _providers;

    internal IReadOnlyDictionary<string, ProviderInfo> ModelToProvider => _modelToProvider;

    internal IReadOnlyDictionary<string, string> ModelToUpstream => _modelToUpstream;

    internal void UpdateModelMappings(
        Dictionary<string, ProviderInfo> modelToProvider,
        Dictionary<string, string> modelToUpstream,
        Dictionary<string, List<ProviderInfo>>? upstreamToProviders = null)
    {
        _modelToProvider = modelToProvider;
        _modelToUpstream = modelToUpstream;

        if (upstreamToProviders is not null)
        {
            // Caller (ModelCatalogService) already ordered providers by configured priority.
            _upstreamToProviders = upstreamToProviders;
            return;
        }

        // Fallback: build from modelToProvider, preserving discovery order as a tie-break.
        Dictionary<string, List<ProviderInfo>> upstream = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string modelId, ProviderInfo prov) in modelToProvider)
        {
            string up = modelToUpstream.TryGetValue(modelId, out string? u) ? u : modelId;
            if (!upstream.TryGetValue(up, out List<ProviderInfo>? list))
            {
                list = [];
                upstream[up] = list;
            }

            if (!list.Any(p => p.Name.Equals(prov.Name, StringComparison.OrdinalIgnoreCase)))
            {
                int order = _providers.FindIndex(p => p.Name.Equals(prov.Name, StringComparison.OrdinalIgnoreCase));
                int insertAt = list.FindIndex(p => _providers.FindIndex(q => q.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) > order);
                if (insertAt < 0)
                {
                    list.Add(prov);
                }
                else
                {
                    list.Insert(insertAt, prov);
                }
            }
        }

        _upstreamToProviders = upstream;
    }

    internal ProviderInfo ResolveProvider(string? requestedModel)
    {
        if (_providers.Count == 0)
            throw new InvalidOperationException("ProviderRegistry: no providers are registered (check PROVIDER_*_API_KEY env vars).");
        if (!string.IsNullOrWhiteSpace(requestedModel) && _modelToProvider.TryGetValue(requestedModel, out ProviderInfo provider))
            return provider;
        // Fall back to the first registered provider when the model isn't known.
        return _providers[0];
    }

    internal string ResolveModel(string? requestedModel)
    {
        if (_providers.Count == 0)
            return DefaultModel;

        if (!string.IsNullOrWhiteSpace(requestedModel))
        {
            string cleanModel = StripTagSuffix(requestedModel);
            // Handle VS 2026 Copilot display format from /api/tags: "PROVIDER - model_name:latest"
            cleanModel = StripProviderDisplayPrefix(cleanModel);
            if (_modelToProvider.ContainsKey(cleanModel))
                return cleanModel;

            // Fallback: search for keys whose suffix (after the last '/') matches
            // the cleaned model name. This handles cases where an upstream model
            // is stored with a provider prefix like "models/gemini-2.5-pro" but the
            // Copilot display name strips the prefix.
            string? fallback = FindModelBySuffix(cleanModel);
            if (fallback is not null)
                return fallback;

            int slash = cleanModel.IndexOf('/');
            if (slash > 0 && slash < cleanModel.Length - 1)
            {
                if (_modelToProvider.ContainsKey(cleanModel))
                    return cleanModel;

                string bare = cleanModel[(slash + 1)..];
                if (_modelToProvider.ContainsKey(bare))
                    return bare;

                string? providerHint = cleanModel[..slash];
                foreach (KeyValuePair<string, ProviderInfo> kv in _modelToProvider)
                {
                    if (!string.Equals(kv.Value.Name, providerHint, StringComparison.OrdinalIgnoreCase))
                        continue;
                    int lastSlash = kv.Key.LastIndexOf('/');
                    string suffix = lastSlash > 0 ? kv.Key[(lastSlash + 1)..] : kv.Key;
                    if (string.Equals(suffix, bare, StringComparison.OrdinalIgnoreCase))
                        return kv.Key;
                }
            }
        }

        Console.WriteLine($"[ProviderRegistry] Model '{requestedModel}' not found — falling back to default '{DefaultModel}'.");
        return DefaultModel;
    }

    /// <summary>
    /// Returns true if the model name is known in the provider mapping.
    /// </summary>
    internal bool IsModelKnown(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;
        string clean = StripTagSuffix(model);
        clean = StripProviderDisplayPrefix(clean);
        if (_modelToProvider.ContainsKey(clean))
            return true;
        // Fallback: search by suffix (e.g. "gemini-2.5-pro" matches "models/gemini-2.5-pro")
        return FindModelBySuffix(clean) is not null;
    }

    /// <summary>
    /// Removes the Copilot BYOM display prefix from a model name (e.g. "ZENMUX - claude-opus-4.8" → "claude-opus-4.8").
    /// This is the format VS 2026 Copilot sends to /api/show from the /api/tags name field.
    /// </summary>
    private string StripProviderDisplayPrefix(string model)
    {
        int dashIdx = model.IndexOf(" - ");
        if (dashIdx > 0)
        {
            string maybeProvider = model[..dashIdx];
            if (_providers.Any(p => string.Equals(p.Name, maybeProvider, StringComparison.OrdinalIgnoreCase)))
            {
                return model[(dashIdx + 3)..].Trim();
            }
        }
        return model;
    }

    /// <summary>
    /// Searches the model registry for a key whose last path segment matches the given name.
    /// For example, "gemini-2.5-pro" matches "models/gemini-2.5-pro" or "models/gemini-2.5-pro@google".
    /// Returns the first matching key, or null if none found.
    /// </summary>
    private string? FindModelBySuffix(string name)
    {
        foreach (string key in _modelToProvider.Keys)
        {
            // Check bare suffix (e.g. "models/gemini-2.5-pro")
            int lastSlash = key.LastIndexOf('/');
            string suffix = lastSlash >= 0 ? key[(lastSlash + 1)..] : key;

            // Strip @provider tag from the suffix before comparing
            int atIdx = suffix.IndexOf('@');
            if (atIdx > 0)
                suffix = suffix[..atIdx];

            if (string.Equals(suffix, name, StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return null;
    }

    /// <summary>Removes the tag portion of an Ollama model name (e.g. "model:latest" → "model").</summary>
    private static string StripTagSuffix(string model)
    {
        int colonIdx = model.IndexOf(':');
        return colonIdx > 0 ? model[..colonIdx] : model;
    }

    internal string ResolveUpstreamModel(string? requestedModel)
    {
        string resolved = ResolveModel(requestedModel);
        return _modelToUpstream.TryGetValue(resolved, out string? upstream) ? upstream : resolved;
    }

    /// <summary>
    /// Returns the ordered list of (provider, upstreamModel) candidates for a requested model.
    /// A provider-qualified id ("model@provider") resolves to a single candidate (no failover).
    /// A bare model id returns every provider that offers it, ordered by configured priority.
    /// </summary>
    internal IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> ResolveCandidates(string? requestedModel)
    {
        string resolved = ResolveModel(requestedModel);
        string upstream = _modelToUpstream.TryGetValue(resolved, out string? up) ? up : resolved;

        // Explicit "model@provider" alias -> single, exact candidate.
        bool isQualified = resolved.Contains('@');
        if (isQualified && _modelToProvider.TryGetValue(resolved, out ProviderInfo exact))
        {
            return [(exact, upstream)];
        }

        if (_upstreamToProviders.TryGetValue(upstream, out List<ProviderInfo>? providers) && providers.Count > 0)
        {
            // The resolved model name is explicitly mapped to a primary provider via
            // the model-selection config (e.g. "qwen3.7-plus" → zenmux). Even if other
            // providers (e.g. openrouter) claim the same upstream model with a lower
            // (better) priority, the primary provider MUST be the first candidate.
            // Otherwise a user who selected "ZENMUX - qwen3.7-plus" from the /api/tags
            // list could silently receive responses from a different provider.
            var candidates = providers.Select(p => (p, upstream)).ToList();
            if (_modelToProvider.TryGetValue(resolved, out ProviderInfo primary))
            {
                int primaryIdx = candidates.FindIndex(c =>
                    string.Equals(c.p.Name, primary.Name, StringComparison.OrdinalIgnoreCase));
                if (primaryIdx > 0)
                {
                    var primaryCandidate = candidates[primaryIdx];
                    candidates.RemoveAt(primaryIdx);
                    candidates.Insert(0, primaryCandidate);
                }
            }
            return candidates;
        }

        // When no upstream candidates exist, return the fallback — the first
        // registered provider acting as default.
        if (_providers.Count == 0)
            return [];
        return [(ResolveProvider(resolved), upstream)];
    }

    private void DiscoverProviders(ProviderHttpClientFactory httpClientFactory)
    {
        foreach (string providerName in ProviderCapabilitiesRegistry.KnownProviders)
        {
            ProviderCapabilities caps = ProviderCapabilitiesRegistry.Get(providerName);
            string prefix = caps.EnvPrefix;

            string? apiKey = Environment.GetEnvironmentVariable($"PROVIDER_{prefix}_API_KEY");

            // Backward compatibility: Ollama Cloud also accepts PROVIDER_OLLAMA_API_KEY.
            if (string.IsNullOrWhiteSpace(apiKey) && providerName == "ollama")
            {
                apiKey = Environment.GetEnvironmentVariable("PROVIDER_OLLAMA_API_KEY");
            }

            // Skip providers that require an API key when none is configured.
            // Ollama can still be discovered later as a local instance (no API key needed).
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                continue;
            }

            string? configuredBaseUrl = Environment.GetEnvironmentVariable($"PROVIDER_{prefix}_BASE_URL");
            string baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
                ? caps.DefaultBaseUrl
                : configuredBaseUrl;

            HttpClient provClient = httpClientFactory.CreateProviderClient(providerName, baseUrl, apiKey);
            _providers.Add(new ProviderInfo(providerName, apiKey, baseUrl, provClient, caps));
        }

        // ── Local Ollama (no API key required) ──────────────────────────
        // When PROVIDER_OLLAMA_BASE_URL points to localhost / 127.0.0.1 / ::1,
        // register a second "ollama" entry that works without an API key.
        string? ollamaBaseUrl = Environment.GetEnvironmentVariable("PROVIDER_OLLAMA_BASE_URL");
        if (!string.IsNullOrWhiteSpace(ollamaBaseUrl)
            && (ollamaBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                || ollamaBaseUrl.Contains("127.0.0.1")
                || ollamaBaseUrl.Contains("::1"))
            && !_providers.Any(p => p.BaseUrl.Equals(ollamaBaseUrl, StringComparison.OrdinalIgnoreCase)))
        {
            ProviderCapabilities ollamaCaps = ProviderCapabilitiesRegistry.Get("ollama");
            HttpClient localClient = httpClientFactory.CreateProviderClient("ollama", ollamaBaseUrl, apiKey: "");
            _providers.Add(new ProviderInfo("ollama", "", ollamaBaseUrl, localClient, ollamaCaps));
        }

        // ── Legacy DEEPSEEK_API_KEY fallback ────────────────────────────
        string? legacyKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(legacyKey) && !_providers.Any(p => p.Name == "deepseek"))
        {
            ProviderCapabilities deepseekCaps = ProviderCapabilitiesRegistry.Get("deepseek");
            string legacyUrl = Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL") ?? deepseekCaps.DefaultBaseUrl;
            _providers.Add(new ProviderInfo("deepseek", legacyKey, legacyUrl,
                httpClientFactory.CreateProviderClient("deepseek", legacyUrl, legacyKey), deepseekCaps));
        }
    }
}
