internal sealed class ProviderRegistry
{
    /// <summary>
    /// The provider-hint token meaning "whichever provider serves this model, in priority order"
    /// — the unpinned counterpart to <c>model@groq</c>. Reserved: it is not, and must never be,
    /// the name of a real provider in <see cref="ProviderCapabilitiesRegistry"/>, or an
    /// <c>@auto</c> id would resolve to that provider and silently pin what must stay open.
    /// </summary>
    internal const string AutoProviderToken = "auto";

    private readonly List<ProviderInfo> _providers = [];
    private Dictionary<string, ProviderInfo> _modelToProvider = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _modelToUpstream = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<ProviderInfo>> _upstreamToProviders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>"gpt-oss-120b@auto"</c> → every provider serving that model, best first. Keyed separately
    /// from <see cref="_upstreamToProviders"/> because the same model carries a different upstream
    /// id per provider (<c>gpt-oss-120b</c>, <c>openai/gpt-oss-120b</c>, <c>gpt-oss:120b</c>), so
    /// there is no single upstream key these candidates could share.
    /// </summary>
    private Dictionary<string, List<(ProviderInfo Provider, string UpstreamModel)>> _autoAliases = new(StringComparer.OrdinalIgnoreCase);

    private readonly ProviderHealthService? _health;

    /// <param name="health">
    /// Optional so the many tests that construct a bare registry keep compiling. The DI container
    /// picks the widest constructor it can satisfy, so the registered singleton is injected in
    /// production and only <see cref="ResolveRoutePlan"/> — never <see cref="ResolveCandidates"/> —
    /// consults it.
    /// </param>
    public ProviderRegistry(ProviderHttpClientFactory httpClientFactory, ProviderHealthService? health = null)
    {
        _health = health;
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

    internal IReadOnlyDictionary<string, List<(ProviderInfo Provider, string UpstreamModel)>> AutoAliases => _autoAliases;

    /// <summary>
    /// Replaces the unpinned alias table. Candidates arrive already ordered by the caller
    /// (<see cref="ModelCatalogService"/>), which applies the same priority-then-provider-order
    /// rule the pinned catalog uses for cross-provider collisions.
    /// </summary>
    internal void UpdateAutoAliases(Dictionary<string, List<(ProviderInfo Provider, string UpstreamModel)>> aliases) =>
        _autoAliases = aliases;

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
            string? displayProviderHint;
            (cleanModel, displayProviderHint) = StripProviderDisplayPrefix(cleanModel);
            // Strip @provider suffix (e.g. "qwen3.6-plus@zenmux" → "qwen3.6-plus")
            string? atProviderHint = StripAtProviderSuffixGetProvider(cleanModel, out cleanModel);
            // @provider suffix takes priority over display prefix hint
            string? effectiveProviderHint = atProviderHint ?? displayProviderHint;

            // If we have a provider hint (from @suffix or display prefix), check the
            // qualified alias FIRST before falling back to the bare key (which may be
            // mapped to a different provider with higher priority).
            // "@auto" is not a provider — it asks for the fan-out list. When the model has one
            // registered, hand back the alias; otherwise drop the hint and resolve the bare name
            // normally, since "any provider" is satisfied by the only provider that offers it.
            if (string.Equals(effectiveProviderHint, AutoProviderToken, StringComparison.OrdinalIgnoreCase))
            {
                string autoAlias = $"{cleanModel}@{AutoProviderToken}";
                if (_autoAliases.ContainsKey(autoAlias))
                    return autoAlias;
                effectiveProviderHint = null;
            }

            if (effectiveProviderHint is not null)
            {
                string qualifiedAlias = $"{cleanModel}@{effectiveProviderHint}";
                if (_modelToProvider.ContainsKey(qualifiedAlias))
                    return qualifiedAlias;

                // Try rebuild with provider prefix (e.g. "kimi-k2.6" → "moonshotai/kimi-k2.6@zenmux")
                string? rebuilt = RebuildQualifiedAlias(cleanModel, effectiveProviderHint);
                if (rebuilt is not null)
                    return rebuilt;

                // The hint names a provider that does not offer this model. Both lookups
                // above already searched within that provider, so anything matched from
                // here on would belong to a *different* provider — and answering a
                // "OLLAMA - x" pick from, say, NVIDIA is worse than falling back to the
                // configured default. Stop here.
                Console.WriteLine($"[ProviderRegistry] Provider hint '{effectiveProviderHint}' does not offer '{cleanModel}' — falling back to default '{DefaultModel}'.");
                return DefaultModel;
            }

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
        (clean, _) = StripProviderDisplayPrefix(clean);
        // Also strip @provider suffix (e.g. "qwen3.6-plus@zenmux" → "qwen3.6-plus")
        StripAtProviderSuffixGetProvider(clean, out clean);
        if (_modelToProvider.ContainsKey(clean))
            return true;
        // Fallback: search by suffix (e.g. "gemini-2.5-pro" matches "models/gemini-2.5-pro")
        string? fallback = FindModelBySuffix(clean);
        if (fallback is not null)
            return true;
        // Also try rebuilding qualified alias from @provider hint
        string stripped = StripTagSuffix(StripProviderDisplayPrefix(model).CleanModel);
        string? atHint = StripAtProviderSuffixGetProvider(stripped, out string bare);
        if (atHint is not null && RebuildQualifiedAlias(bare, atHint) is not null)
            return true;
        return false;
    }

    /// <summary>
    /// Removes the Copilot BYOM display prefix from a model name (e.g. "ZENMUX - claude-opus-4.8" → "claude-opus-4.8")
    /// and returns the provider hint that was stripped.
    /// </summary>
    private (string CleanModel, string? ProviderHint) StripProviderDisplayPrefix(string model)
    {
        int dashIdx = model.IndexOf(" - ");
        if (dashIdx > 0)
        {
            string maybeProvider = model[..dashIdx];

            // "AUTO - gpt-oss-120b" is the display form of the unpinned alias. No provider is
            // named "auto", so without this it would fall through as an unknown prefix.
            if (string.Equals(maybeProvider, AutoProviderToken, StringComparison.OrdinalIgnoreCase))
                return (model[(dashIdx + 3)..].Trim(), AutoProviderToken);

            ProviderInfo? prov = _providers.FirstOrDefault(p => string.Equals(p.Name, maybeProvider, StringComparison.OrdinalIgnoreCase));
            if (prov is { Name: var providerName })
            {
                return (model[(dashIdx + 3)..].Trim(), providerName);
            }
        }
        return (model, null);
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

    /// <summary>
    /// Removes the client-facing tag appended by <c>/api/tags</c> (e.g. "model@ollama:latest" → "model@ollama").
    /// Only the trailing tag is removed: Ollama upstream ids legitimately embed a colon
    /// ("qwen3-coder:480b", "devstral-2:123b"), so splitting on the *first* colon would
    /// truncate the model id and route the request to the wrong provider.
    /// </summary>
    private static string StripTagSuffix(string model)
    {
        int lastColon = model.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == model.Length - 1)
            return model;

        // Never strip a colon that belongs to the model id itself — the tag added by
        // /api/tags always comes after the "@provider" marker when one is present.
        int at = model.LastIndexOf('@');
        if (at >= 0)
            return lastColon > at ? model[..lastColon] : model;

        return model.AsSpan(lastColon + 1).Equals("latest", StringComparison.OrdinalIgnoreCase)
            ? model[..lastColon]
            : model;
    }

    /// <summary>
    /// Strips the @provider suffix from a model name (e.g. "qwen3.6-plus@zenmux" → "qwen3.6-plus").
    /// Returns the provider name if found, or null.
    /// </summary>
    private string? StripAtProviderSuffixGetProvider(string model, out string cleanModel)
    {
        int atIdx = model.IndexOf('@');
        if (atIdx > 0 && atIdx < model.Length - 1)
        {
            cleanModel = model[..atIdx];
            return model[(atIdx + 1)..];
        }
        cleanModel = model;
        return null;
    }

    /// <summary>
    /// Given a bare model name (e.g. "qwen3.6-plus") and a provider hint (e.g. "zenmux"),
    /// rebuilds a qualified alias by searching for a key in _modelToProvider that belongs
    /// to the specified provider and ends with the bare model name.
    /// Example: "qwen3.6-plus" + "zenmux" → "qwen/qwen3.6-plus@zenmux"
    /// </summary>
    private string? RebuildQualifiedAlias(string bareModel, string providerHint)
    {
        foreach (KeyValuePair<string, ProviderInfo> kv in _modelToProvider)
        {
            if (!string.Equals(kv.Value.Name, providerHint, StringComparison.OrdinalIgnoreCase))
                continue;

            // Extract the suffix after the last /, and strip @provider if present
            int lastSlash = kv.Key.LastIndexOf('/');
            string suffix = lastSlash >= 0 ? kv.Key[(lastSlash + 1)..] : kv.Key;
            int atIdx = suffix.IndexOf('@');
            if (atIdx > 0)
                suffix = suffix[..atIdx];

            if (string.Equals(suffix, bareModel, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }
        return null;
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

        // "model@auto" fans out to every provider serving the model, each with its own upstream
        // id. Checked before the qualified branch below, which sees only the '@' and would pin
        // the request to a single provider — the opposite of what this alias asks for.
        if (_autoAliases.TryGetValue(resolved, out List<(ProviderInfo Provider, string UpstreamModel)>? autoCandidates)
            && autoCandidates.Count > 0)
        {
            return autoCandidates;
        }

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

    /// <summary>
    /// The candidate list an endpoint should actually dispatch against: <see cref="ResolveCandidates"/>
    /// reordered so providers currently in cooldown are tried last.
    /// </summary>
    /// <remarks>
    /// Health lives here rather than inside <see cref="ResolveCandidates"/> on purpose.
    /// <c>ResolveCandidates</c> is pure, deterministic and heavily tested, and
    /// <c>ProviderBenchmarkService</c> depends on that purity — it specifically wants to probe
    /// providers that are cooling down, which is how they get re-discovered as healthy.
    ///
    /// An explicit <c>model@provider</c> pin resolves to a single candidate; reordering one
    /// element is a no-op, so a pinned request still goes exactly where the user asked, cooldown
    /// or not. Answering an explicit pick from a different provider would be worse than an
    /// honest error.
    /// </remarks>
    internal IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> ResolveRoutePlan(string? requestedModel)
    {
        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> candidates = ResolveCandidates(requestedModel);
        if (_health is null || candidates.Count <= 1)
            return candidates;

        return _health.Order(candidates, ResolveModel(requestedModel));
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

            // Some providers have no usable default URL because it embeds an account id
            // (Cloudflare). Registering one with the placeholder would send every request to a
            // host that cannot answer, so skip it and say why.
            if (caps.RequiresExplicitBaseUrl && string.IsNullOrWhiteSpace(configuredBaseUrl))
            {
                Console.WriteLine($"[CONFIG] Provider '{providerName}' has an API key but no PROVIDER_{prefix}_BASE_URL. " +
                                  $"Its base URL contains an account id and has no default, so it was not registered.");
                continue;
            }

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
