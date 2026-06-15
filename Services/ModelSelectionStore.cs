using System.Text.Json;

internal sealed class ModelSelectionStore
{
    private readonly Dictionary<string, ModelSelectionEntry[]> _providerModelSelections;

    public ModelSelectionStore()
    {
        _providerModelSelections = LoadProviderModelSelections();
    }

    internal Dictionary<string, ModelSelectionEntry[]> ProviderModelSelections => _providerModelSelections;

    internal ModelExecutionConfig GetExecutionConfigForModel(string model, IReadOnlyDictionary<string, ProviderInfo> modelToProvider)
    {
        // Collect every (provider, entry) that matches this model, then pick the
        // one with the longest match substring (= most specific). This handles
        // cases where two providers both match a given upstream id (e.g. ollama
        // matches "nemotron" and ollamacloud matches "nemotron-3-super"): the
        // longer match wins regardless of which JSON file is loaded first.
        ModelSelectionEntry best = default;
        string? bestProvider = null;
        bool hasBest = false;

        if (modelToProvider.TryGetValue(model, out ProviderInfo provider))
        {
            ModelSelectionEntry? entry = FindModelSelectionEntry(model, provider.Name);
            if (entry.HasValue)
            {
                best = entry.Value;
                bestProvider = provider.Name;
                hasBest = true;
            }
        }

        foreach (KeyValuePair<string, ModelSelectionEntry[]> kv in _providerModelSelections)
        {
            ModelSelectionEntry? entry = FindModelSelectionEntry(model, kv.Key);
            if (!entry.HasValue)
            {
                continue;
            }

            if (!hasBest || entry.Value.Match.Length > best.Match.Length)
            {
                best = entry.Value;
                bestProvider = kv.Key;
                hasBest = true;
            }
        }

        return hasBest ? best.Execution : new ModelExecutionConfig();
    }

    internal int GetPreferredModelPriority(string model, string providerName)
    {
        ModelSelectionEntry? entry = FindModelSelectionEntry(model, providerName);
        return entry?.Priority ?? int.MaxValue;
    }

    internal bool IsPreferredModel(string model, string providerName) =>
        FindModelSelectionEntry(model, providerName) != null;

    internal ModelSelectionEntry[] GetProviderModelSelections(string providerName)
    {
        if (_providerModelSelections.TryGetValue(providerName, out ModelSelectionEntry[]? configured) && configured.Length > 0)
        {
            return configured;
        }

        return GetDefaultPreferredModelSelections();
    }

    internal ModelSelectionEntry? FindModelSelectionEntry(string model, string providerName)
    {
        string m = model.ToLowerInvariant();

        if (m.Contains("guard") || m.Contains("safety") || m.Contains("embed") || m.Contains("retriever") || m.Contains("reranker")
            || m.Contains("reward") || m.Contains("parse") || m.Contains("detector") || m.Contains("clip") || m.Contains("riva-translate"))
        {
            return null;
        }

        ModelSelectionEntry[] entries = GetProviderModelSelections(providerName);
        foreach (ModelSelectionEntry entry in entries.OrderBy(x => x.Priority))
        {
            if (!entry.Enabled)
            {
                continue;
            }

            if (m.Contains(entry.Match, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static Dictionary<string, ModelSelectionEntry[]> LoadProviderModelSelections()
    {
        Dictionary<string, ModelSelectionEntry[]> selections = new(StringComparer.OrdinalIgnoreCase);
        string[] candidateDirs =
        [
            Path.Combine(AppContext.BaseDirectory, "config", "model-selection"),
            Path.Combine(Directory.GetCurrentDirectory(), "config", "model-selection")
        ];

        foreach (string dir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                    JsonElement root = doc.RootElement;

                    if (!root.TryGetProperty("provider", out JsonElement provE) || provE.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string provider = provE.GetString()!.Trim().ToLowerInvariant();

                    if (!root.TryGetProperty("models", out JsonElement modelsE) || modelsE.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    List<ModelSelectionEntry> entries = [];
                    int idx = 0;
                    foreach (JsonElement item in modelsE.EnumerateArray())
                    {
                        idx++;
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            string? match = item.GetString();
                            if (!string.IsNullOrWhiteSpace(match))
                            {
                                entries.Add(new ModelSelectionEntry(match!, idx, true, new ModelExecutionConfig()));
                            }

                            continue;
                        }

                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        string? matchValue = null;
                        if (item.TryGetProperty("match", out JsonElement matchE) && matchE.ValueKind == JsonValueKind.String)
                        {
                            matchValue = matchE.GetString();
                        }
                        else if (item.TryGetProperty("model", out JsonElement modelE) && modelE.ValueKind == JsonValueKind.String)
                        {
                            matchValue = modelE.GetString();
                        }
                        else if (item.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String)
                        {
                            matchValue = idE.GetString();
                        }

                        if (string.IsNullOrWhiteSpace(matchValue))
                        {
                            continue;
                        }

                        int priority = item.TryGetProperty("priority", out JsonElement priE) && priE.ValueKind == JsonValueKind.Number
                            ? priE.GetInt32() : idx;
                        bool enabled = !item.TryGetProperty("enabled", out JsonElement enE) || enE.ValueKind != JsonValueKind.False;

                        ModelExecutionConfig exec = new();
                        if (item.TryGetProperty("execution", out JsonElement execE) && execE.ValueKind == JsonValueKind.Object)
                        {
                            exec = new ModelExecutionConfig(
                                ContextLength: execE.TryGetProperty("context_length", out JsonElement ctxE) && ctxE.ValueKind == JsonValueKind.Number ? ctxE.GetInt32() : null,
                                MaxOutputTokens: execE.TryGetProperty("max_output_tokens", out JsonElement outE) && outE.ValueKind == JsonValueKind.Number ? outE.GetInt32() : null,
                                SupportsTools: execE.TryGetProperty("supports_tools", out JsonElement stE) && stE.ValueKind is JsonValueKind.True or JsonValueKind.False ? stE.GetBoolean() : null,
                                SupportsVision: execE.TryGetProperty("supports_vision", out JsonElement svE) && svE.ValueKind is JsonValueKind.True or JsonValueKind.False ? svE.GetBoolean() : null,
                                Family: execE.TryGetProperty("family", out JsonElement famE) && famE.ValueKind == JsonValueKind.String ? famE.GetString() : null,
                                Temperature: execE.TryGetProperty("temperature", out JsonElement tempE) && tempE.ValueKind == JsonValueKind.Number ? tempE.GetDouble() : null,
                                TopP: execE.TryGetProperty("top_p", out JsonElement topPE) && topPE.ValueKind == JsonValueKind.Number ? topPE.GetDouble() : null,
                                MaxTokensPreferred: execE.TryGetProperty("max_tokens", out JsonElement maxTokE) && maxTokE.ValueKind == JsonValueKind.Number ? maxTokE.GetInt32() : null,
                                ReasoningEffort: execE.TryGetProperty("reasoning_effort", out JsonElement reE) && reE.ValueKind == JsonValueKind.String ? reE.GetString() : null,
                                TimeoutSeconds: execE.TryGetProperty("timeout_seconds", out JsonElement timeoutE) && timeoutE.ValueKind == JsonValueKind.Number ? timeoutE.GetInt32() : null,
                                OverrideClientParams: execE.TryGetProperty("override_client_params", out JsonElement ovE) && ovE.ValueKind is JsonValueKind.True or JsonValueKind.False && ovE.GetBoolean(),
                                SupportsReasoning: execE.TryGetProperty("supports_reasoning", out JsonElement srE) && srE.ValueKind is JsonValueKind.True or JsonValueKind.False ? srE.GetBoolean() : null
                            );
                        }

                        entries.Add(new ModelSelectionEntry(matchValue!, priority, enabled, exec));
                    }

                    if (entries.Count > 0)
                    {
                        // Merge with existing entries for the same provider (multiple files may
                        // declare the same provider, e.g. ollama.json + ollamacloud.json both use
                        // "provider": "ollama"). Previously loaded entries are preserved.
                        if (selections.TryGetValue(provider, out ModelSelectionEntry[] existing))
                        {
                            // Start with existing entries, then add new ones (no duplicate match strings).
                            HashSet<string> existingMatches = new(existing.Select(e => e.Match), StringComparer.OrdinalIgnoreCase);
                            List<ModelSelectionEntry> merged = [..existing];
                            foreach (ModelSelectionEntry entry in entries)
                            {
                                if (existingMatches.Add(entry.Match))
                                {
                                    merged.Add(entry);
                                }
                            }
                            entries = merged;
                        }

                        // Order entries by (priority asc, match length desc). A longer match
                        // substring is more specific than a shorter one, so it wins when two
                        // entries (e.g. "nemotron" vs "nemotron-3-super") both match the
                        // same upstream id. Without this, a generic match would shadow a
                        // specific one whenever the generic file is loaded first.
                        selections[provider] = entries
                            .OrderBy(x => x.Priority)
                            .ThenByDescending(x => x.Match.Length)
                            .ToArray();
                    }
                }
                catch
                {
                    // ignore malformed selection files
                }
            }
        }

        return selections;
    }

    private static ModelSelectionEntry[] GetDefaultPreferredModelSelections() =>
    [
        new("deepseek-v4-pro", 1, true, new()),
        new("qwen3-coder-480b-a35b", 2, true, new()),
        new("qwen3.5-397b-a17b", 3, true, new()),
        new("mistral-large-3-675b-instruct-2512", 4, true, new()),
        new("llama-4-maverick-17b-128e-instruct", 5, true, new()),
        new("llama-3.1-nemotron-ultra-253b-v1", 6, true, new()),
        new("nemotron-4-340b-instruct", 7, true, new()),
        new("gpt-oss-120b", 8, true, new()),
        new("kimi-k2.6", 9, true, new()),
        new("llama-3.3-nemotron-super-49b-v1.5", 10, true, new())
    ];
}
