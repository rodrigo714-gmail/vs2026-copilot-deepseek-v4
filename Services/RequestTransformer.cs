using System.Text;
using System.Text.Json;

internal sealed class RequestTransformer
{
    private readonly ModelCatalogService _modelCatalogService;
    private readonly ReasoningCacheService _reasoningCacheService;

    public RequestTransformer(ModelCatalogService modelCatalogService, ReasoningCacheService reasoningCacheService)
    {
        _modelCatalogService = modelCatalogService;
        _reasoningCacheService = reasoningCacheService;
    }

    internal string ApplyExecutionDefaults(string rawBody, string model, ProviderCapabilities capabilities = default)
    {
        ModelExecutionConfig exec = _modelCatalogService.GetExecutionConfigForModel(model);
        bool hasAnyDefault = exec.Temperature.HasValue
            || exec.TopP.HasValue
            || exec.MaxTokensPreferred.HasValue
            || !string.IsNullOrWhiteSpace(exec.ReasoningEffort);
        if (!hasAnyDefault)
        {
            return rawBody;
        }

        // Parameter support is driven by the provider's declared capabilities.
        // For unknown providers (default capabilities), all feature flags are false
        // (no reasoning_effort, no top_k) — a safe, conservative default.
        bool supportsReasoningEffort = capabilities.SupportsReasoningEffort;

        // DeepSeek & OpenAI o-series: sending both temperature and top_p simultaneously
        // causes undefined behaviour per official docs. When reasoning_effort is active
        // (i.e. the model is a native reasoner) only emit temperature, not top_p.
        bool isNativeReasoner = capabilities.SupportsReasoningEffort && !string.IsNullOrWhiteSpace(exec.ReasoningEffort);

        // Providers that DO support top_k: NVIDIA, Groq, OpenRouter (passthrough).
        // Everyone else (including unknown) gets top_k stripped.
        bool supportsTopK = capabilities.SupportsTopK;

        // OverrideClientParams=true means the configured value is non-negotiable for this
        // model (e.g. Kimi K2.x requires temperature=1.0). In that mode we overwrite the
        // client-supplied field instead of only injecting defaults.
        bool force = exec.OverrideClientParams;

        try
        {
            using JsonDocument original = JsonDocument.Parse(rawBody);
            JsonElement root = original.RootElement;
            using MemoryStream ms = new();
            using Utf8JsonWriter writer = new(ms);

            writer.WriteStartObject();

            bool hasTemperature = false;
            bool hasTopP = false;
            bool hasMaxTokens = false;
            bool hasReasoningEffort = false;

            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (prop.NameEquals("temperature"))
                {
                    if (force && exec.Temperature.HasValue)
                    {
                        writer.WriteNumber("temperature", exec.Temperature.Value);
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                    hasTemperature = true;
                }
                else if (prop.NameEquals("top_p"))
                {
                    if (force && exec.TopP.HasValue)
                    {
                        writer.WriteNumber("top_p", exec.TopP.Value);
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                    hasTopP = true;
                }
                else if (prop.NameEquals("max_tokens"))
                {
                    if (force && exec.MaxTokensPreferred.HasValue)
                    {
                        writer.WriteNumber("max_tokens", exec.MaxTokensPreferred.Value);
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                    hasMaxTokens = true;
                }
                else if (prop.NameEquals("reasoning_effort"))
                {
                    if (force && !string.IsNullOrWhiteSpace(exec.ReasoningEffort))
                    {
                        writer.WriteString("reasoning_effort", exec.ReasoningEffort);
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                    hasReasoningEffort = true;
                }
                else if (prop.NameEquals("top_k") && !supportsTopK)
                {
                    // Skip top_k for providers that don't support it
                    continue;
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }

            if (!hasTemperature && exec.Temperature.HasValue)
                writer.WriteNumber("temperature", exec.Temperature.Value);
            // Skip top_p injection for native reasoners to avoid temperature+top_p conflict.
            if (!hasTopP && exec.TopP.HasValue && !isNativeReasoner)
                writer.WriteNumber("top_p", exec.TopP.Value);
            if (!hasMaxTokens && exec.MaxTokensPreferred.HasValue)
                writer.WriteNumber("max_tokens", exec.MaxTokensPreferred.Value);
            // Only inject reasoning_effort for providers/models that natively support it.
            if (!hasReasoningEffort && !string.IsNullOrWhiteSpace(exec.ReasoningEffort) && supportsReasoningEffort)
                writer.WriteString("reasoning_effort", exec.ReasoningEffort);

            writer.WriteEndObject();
            writer.Flush();

            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return rawBody;
        }
    }

    internal string ReplaceModelInRequestBody(string rawBody, string upstreamModel)
    {
        try
        {
            using JsonDocument original = JsonDocument.Parse(rawBody);
            JsonElement root = original.RootElement;
            using MemoryStream ms = new();
            using Utf8JsonWriter writer = new(ms);

            writer.WriteStartObject();
            bool hasModel = false;

            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (prop.NameEquals("model"))
                {
                    writer.WriteString("model", upstreamModel);
                    hasModel = true;
                    continue;
                }

                prop.WriteTo(writer);
            }

            if (!hasModel)
            {
                writer.WriteString("model", upstreamModel);
            }

            writer.WriteEndObject();
            writer.Flush();

            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return rawBody;
        }
    }

    internal string? ModifyRequest(JsonDocument doc)
    {
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("messages", out JsonElement msgs))
        {
            return null;
        }

        int idx = 0;
        bool modified = false;
        using MemoryStream ms = new();
        using Utf8JsonWriter w = new(ms);

        w.WriteStartObject();
        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (!prop.NameEquals("messages"))
            {
                prop.WriteTo(w);
                continue;
            }

            w.WritePropertyName("messages");
            w.WriteStartArray();
            foreach (JsonElement msg in msgs.EnumerateArray())
            {
                string? role = msg.TryGetProperty("role", out JsonElement r) ? r.GetString() : null;
                if (role == "assistant")
                {
                    bool hasTc = msg.TryGetProperty("tool_calls", out JsonElement tcArr) && tcArr.GetArrayLength() > 0;
                    bool hasFunctionCall = msg.TryGetProperty("function_call", out JsonElement fc)
                        && fc.ValueKind == JsonValueKind.Object;
                    string? key = null;

                    if (hasTc)
                    {
                        List<string> ids = [];
                        foreach (JsonElement tc in tcArr.EnumerateArray())
                            if (tc.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String)
                                ids.Add(idE.GetString()!);
                        if (ids.Count > 0) key = $"toolcall:{string.Join("|", ids)}";
                    }
                    else
                    {
                        key = $"assistant:{idx++}";
                    }

                    // Some providers (e.g. Moonshot/Kimi) reject assistant messages whose
                    // content is empty when there are no tool/function calls associated.
                    // Drop those invalid placeholders before forwarding upstream.
                    if (!hasTc && !hasFunctionCall && IsAssistantContentEmpty(msg))
                    {
                        modified = true;
                        continue;
                    }

                    if (key != null && _reasoningCacheService.TryGet(key, out string? rc))
                    {
                        bool needsInject = !msg.TryGetProperty("reasoning_content", out JsonElement exRc)
                            || exRc.ValueKind != JsonValueKind.String
                            || string.IsNullOrEmpty(exRc.GetString());

                        if (needsInject)
                        {
                            w.WriteStartObject();
                            foreach (JsonProperty mp in msg.EnumerateObject())
                                mp.WriteTo(w);
                            w.WriteString("reasoning_content", rc);
                            w.WriteEndObject();
                            modified = true;
                            continue;
                        }
                    }
                }

                msg.WriteTo(w);
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
        w.Flush();

        return modified ? Encoding.UTF8.GetString(ms.ToArray()) : null;
    }

    private static bool IsAssistantContentEmpty(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out JsonElement content))
            return true;

        return content.ValueKind switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.Undefined => true,
            JsonValueKind.String => string.IsNullOrWhiteSpace(content.GetString()),
            JsonValueKind.Array => content.GetArrayLength() == 0,
            _ => false
        };
    }
}
