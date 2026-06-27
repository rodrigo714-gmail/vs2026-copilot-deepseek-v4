using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

internal static class OllamaEndpoints
{
    internal static IEndpointRouteBuilder MapOllamaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/version", () => Results.Json(new { version = "0.5.7" }, JsonDefaults.SnakeCase));

        app.MapGet("/api/tags", async (HttpContext ctx, ModelCatalogService modelCatalog, ProviderRegistry providerRegistry, ModelSelectionStore modelSelectionStore) =>
        {
            await modelCatalog.RefreshAvailableModelsIfNeeded(ctx.RequestAborted);

            // Keep the configured default model visible even if provider discovery fails.
            modelCatalog.EnsureDefaultModelPresent(ctx.RequestAborted);

            // Build /api/tags strictly from enabled model-selection entries so the
            // Copilot BYOM list stays coherent with config/model-selection/*.json.
            // Only include providers that are currently active (API key configured).
            HashSet<string> activeProviders = new(
                providerRegistry.Providers.Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase);

            List<(string Provider, string Model, int Priority)> configuredEnabled = [];
            foreach ((string providerName, ModelSelectionEntry[] entries) in modelSelectionStore.ProviderModelSelections)
            {
                if (!activeProviders.Contains(providerName))
                    continue;

                foreach (ModelSelectionEntry entry in entries)
                {
                    if (!entry.Enabled)
                        continue;

                    configuredEnabled.Add((providerName, entry.Match, entry.Priority));
                }
            }

            static string NormalizeModelForDisplay(string model)
            {
                string clean = model.Trim();
                int slash = clean.IndexOf('/');
                if (slash > 0 && slash < clean.Length - 1)
                    clean = clean[(slash + 1)..];

                return clean;
            }

            // Group by provider + normalized display model to reduce duplicate aliases such as
            // "kimi-k2.6" and "moonshotai/kimi-k2.6" in the same provider slot.
            // For each group, keep the best entry by priority and then by readability (prefer non-prefixed ids).
            var curated = configuredEnabled
                .Select(x => new
                {
                    x.Provider,
                    x.Model,
                    x.Priority,
                    DisplayModel = NormalizeModelForDisplay(x.Model)
                })
                .GroupBy(x => $"{x.Provider.ToLowerInvariant()}::{x.DisplayModel.ToLowerInvariant()}")
                .Select(g => g
                    .OrderBy(x => x.Priority)
                    .ThenBy(x => x.Model.Contains('/') ? 1 : 0)
                    .ThenBy(x => x.Model.Length)
                    .First())
                .OrderBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Priority)
                .ThenBy(x => x.DisplayModel, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Json(new
            {
                models = curated.Select(x =>
                {
                    string providerPrefix = x.Provider.ToUpperInvariant();
                    string displayName = $"{providerPrefix} - {x.DisplayModel}";
                    string routedModel = x.Model;
                    // Use a provider-qualified alias so that when the client sends this back
                    // as the model name, the proxy routes it uniquely to the correct provider
                    // instead of falling back to the default (e.g. DeepSeek).
                    string qualifiedModel = $"{routedModel}@{x.Provider}:latest";

                    (int ContextLength, int MaxOutputTokens, bool SupportsTools, bool SupportsVision, string[] Capabilities, string Family) p = modelCatalog.GetModelProfile(routedModel);
                    return new
                    {
                        name = displayName + ":latest",
                        model = qualifiedModel,
                        modified_at = DateTime.UtcNow.ToString("o"),
                        size = 3_826_793_677L,
                        digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
                        details = new
                        {
                            parent_model = "",
                            format = "api",
                            family = p.Family,
                            families = new[] { p.Family },
                            parameter_size = "api",
                            quantization_level = "none"
                        },
                        capabilities = p.Capabilities,
                        context_length = p.ContextLength,
                        max_output_tokens = p.MaxOutputTokens,
                        input_token_limit = p.ContextLength,
                        output_token_limit = p.MaxOutputTokens,
                        supports_tools = p.SupportsTools,
                        supports_tool_calls = p.SupportsTools,
                        supports_vision = p.SupportsVision,
                        supports_images = p.SupportsVision
                    };
                }).ToArray()
            }, JsonDefaults.SnakeCase);
        });

        app.MapGet("/api/show", async (HttpContext ctx, string? model, ModelCatalogService modelCatalog, ProviderRegistry providerRegistry, OllamaResponseBuilder ollamaResponseBuilder) =>
        {
            await modelCatalog.RefreshAvailableModelsIfNeeded(ctx.RequestAborted);
            string resolved = providerRegistry.ResolveModel(model);
            return Results.Json(ollamaResponseBuilder.BuildOllamaShowResponse(resolved), JsonDefaults.SnakeCase);
        });

        app.MapPost("/api/show", async (HttpContext ctx, ModelCatalogService modelCatalog, ProviderRegistry providerRegistry, OllamaResponseBuilder ollamaResponseBuilder) =>
        {
            await modelCatalog.RefreshAvailableModelsIfNeeded(ctx.RequestAborted);
            using StreamReader reader = new(ctx.Request.Body);
            string body = await reader.ReadToEndAsync(ctx.RequestAborted);
            string? model = null;
            try
            {
                using JsonDocument d = JsonDocument.Parse(body);
                if (d.RootElement.TryGetProperty("model", out JsonElement m) && m.ValueKind == JsonValueKind.String)
                    model = m.GetString();
            }
            catch { }

            string resolved = providerRegistry.ResolveModel(model);
            return Results.Json(ollamaResponseBuilder.BuildOllamaShowResponse(resolved), JsonDefaults.SnakeCase);
        });

        app.MapPost("api/chat", async (
            HttpContext ctx,
            ProviderRegistry providerRegistry,
            ModelCatalogService modelCatalog,
            ChatStreamingService chatStreaming,
            ReasoningCacheService reasoningCache,
            RequestTransformer requestTransformer) =>
        {
            CancellationToken ct = ctx.RequestAborted;
            await modelCatalog.RefreshAvailableModelsIfNeeded(ct);
            using StreamReader reader = new(ctx.Request.Body);
            string body = await reader.ReadToEndAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            bool isStream = root.TryGetProperty("stream", out JsonElement sp) && sp.GetBoolean();

            string ollamaRequestedModel = root.TryGetProperty("model", out JsonElement om) && om.ValueKind == JsonValueKind.String
                ? om.GetString()! : providerRegistry.DefaultModel;
            string ollamaEffectiveModel = providerRegistry.ResolveModel(ollamaRequestedModel);
            string ollamaUpstreamModel = providerRegistry.ResolveUpstreamModel(ollamaEffectiveModel);
            ProviderInfo ollamaProvider = providerRegistry.ResolveProvider(ollamaEffectiveModel);
            ModelExecutionConfig ollamaExec = modelCatalog.GetExecutionConfigForModel(ollamaEffectiveModel);

            // ── Diagnostic headers ───────────────────────────────────────
            ctx.Response.Headers["X-Proxy-Requested-Model"] = ollamaRequestedModel;
            ctx.Response.Headers["X-Proxy-Resolved-Model"] = ollamaEffectiveModel;
            ctx.Response.Headers["X-Proxy-Upstream-Model"] = ollamaUpstreamModel;
            ctx.Response.Headers["X-Proxy-Provider"] = ollamaProvider.Name;

            using CancellationTokenSource? ollamaTimeoutCts = modelCatalog.CreateModelTimeoutCts(ollamaEffectiveModel, ct);
            CancellationToken ollamaCt = ollamaTimeoutCts?.Token ?? ct;

            // ── Ollama Cloud / Native Ollama passthrough ──────────────────
            if (ollamaProvider.Capabilities.ApiFormat == ApiFormat.Ollama)
            {
                string upstreamBody = ReplaceModelInOllamaRequestBody(body, ollamaUpstreamModel);

                if (!isStream)
                {
                    using StringContent ollamaContent = new(upstreamBody, Encoding.UTF8, "application/json");
                    using HttpResponseMessage ollamaResp = await ollamaProvider.Client.SendAsync(
                        new HttpRequestMessage(HttpMethod.Post, ollamaProvider.Capabilities.ChatPath) { Content = ollamaContent }, ollamaCt);
                    string ollamaRespBody = await ollamaResp.Content.ReadAsStringAsync(ct);
                    // Fallback: copy `thinking` into `content` for reasoning models that leave content empty.
                    ollamaRespBody = EnsureOllamaContentFromThinking(ollamaRespBody);
                    ctx.Response.StatusCode = (int)ollamaResp.StatusCode;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(ollamaRespBody, ct);
                    return;
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/x-ndjson";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";

                using StringContent ollamaStreamContent = new(upstreamBody, Encoding.UTF8, "application/json");
                using HttpRequestMessage ollamaStreamReq = new(HttpMethod.Post, "api/chat") { Content = ollamaStreamContent };
                using HttpResponseMessage ollamaStreamResp = await ollamaProvider.Client.SendAsync(
                    ollamaStreamReq, HttpCompletionOption.ResponseHeadersRead, ollamaCt);

                if (!ollamaStreamResp.IsSuccessStatusCode)
                {
                    string errBody = await ollamaStreamResp.Content.ReadAsStringAsync(ct);
                    ctx.Response.StatusCode = (int)ollamaStreamResp.StatusCode;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(errBody, ct);
                    return;
                }

                await chatStreaming.StreamNdjsonPassthrough(ollamaStreamResp, ctx.Response, ct);
                return;
            }

            // ── Convert Ollama → OpenAI request ──────────────────────────
            string openAiBody = ConvertOllamaToOpenAi(body, ollamaUpstreamModel, isStream);
            try
            {
                using JsonDocument openAiDoc = JsonDocument.Parse(openAiBody);
                string? modifiedRequest = requestTransformer.ModifyRequest(openAiDoc);
                if (modifiedRequest is not null)
                    openAiBody = modifiedRequest;
            }
            catch
            {
                // Keep original request body if pre-sanitization parsing fails.
            }

            // Apply execution defaults with provider-aware parameter filtering
            openAiBody = requestTransformer.ApplyExecutionDefaults(openAiBody, ollamaEffectiveModel, ollamaProvider.Capabilities);

            using StringContent reqContent = new(openAiBody, Encoding.UTF8, "application/json");

            if (!isStream)
            {
                using HttpResponseMessage resp = await ollamaProvider.Client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, ollamaProvider.Capabilities.ChatPath) { Content = reqContent }, ollamaCt);
                string respBody = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    ctx.Response.StatusCode = (int)resp.StatusCode;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(respBody, ct);
                    return;
                }

                reasoningCache.CacheReasoningFromResponse(respBody);

                using JsonDocument odoc = JsonDocument.Parse(respBody);
                JsonElement msg = odoc.RootElement.GetProperty("choices")[0].GetProperty("message");
                Dictionary<string, object?> ollamaResp = new()
                {
                    ["model"] = ollamaEffectiveModel,
                    ["created_at"] = DateTime.UtcNow.ToString("o"),
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = msg.GetProperty("content").GetString() ?? ""
                    },
                    ["done"] = true,
                    ["done_reason"] = "stop"
                };
                if (msg.TryGetProperty("tool_calls", out JsonElement tcs))
                    ((Dictionary<string, object?>)ollamaResp["message"]!)["tool_calls"] = tcs;

                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(ollamaResp, JsonDefaults.SnakeCase), ct);
            }
            else
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/x-ndjson";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";

                using HttpRequestMessage upstreamReq = new(HttpMethod.Post, "v1/chat/completions")
                {
                    Content = reqContent
                };
                upstreamReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                using HttpResponseMessage upstreamResp = await ollamaProvider.Client.SendAsync(
                    upstreamReq, HttpCompletionOption.ResponseHeadersRead, ollamaCt);

                if (!upstreamResp.IsSuccessStatusCode)
                {
                    string errBody = await upstreamResp.Content.ReadAsStringAsync(ct);
                    ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(errBody, ct);
                    return;
                }

                await chatStreaming.StreamOllamaAndCache(upstreamResp, ctx.Response, ollamaEffectiveModel, ct);
            }
        });

        return app;
    }

    /// <summary>
    /// Converts an Ollama API request body into an OpenAI-compatible request body.
    /// Preserves client-supplied parameters from the Ollama "options" block.
    /// Handles message content with embedded images (converts Ollama format to OpenAI multi-part format).
    /// </summary>
    private static string ConvertOllamaToOpenAi(string ollamaBody, string upstreamModel, bool isStream)
    {
        using JsonDocument doc = JsonDocument.Parse(ollamaBody);
        JsonElement root = doc.RootElement;

        using MemoryStream ms = new();
        using Utf8JsonWriter writer = new(ms);

        writer.WriteStartObject();
        writer.WriteString("model", upstreamModel);
        writer.WriteBoolean("stream", isStream);

        // ── Messages (handle Ollama images → OpenAI multi-part content) ──
        if (root.TryGetProperty("messages", out JsonElement omsgs) && omsgs.ValueKind == JsonValueKind.Array)
        {
            writer.WritePropertyName("messages");
            writer.WriteStartArray();

            foreach (JsonElement msg in omsgs.EnumerateArray())
            {
                writer.WriteStartObject();
                bool hasImages = msg.TryGetProperty("images", out JsonElement imgs) && imgs.GetArrayLength() > 0;

                foreach (JsonProperty mp in msg.EnumerateObject())
                {
                    if (mp.NameEquals("content") && hasImages)
                    {
                        string text = mp.Value.GetString() ?? "";
                        writer.WritePropertyName("content");
                        writer.WriteStartArray();
                        writer.WriteStartObject();
                        writer.WriteString("type", "text");
                        writer.WriteString("text", text);
                        writer.WriteEndObject();
                        foreach (JsonElement img in imgs.EnumerateArray())
                        {
                            string url = img.GetString()!;
                            if (!url.StartsWith("data:") && !url.StartsWith("http"))
                                url = $"data:image/png;base64,{url}";
                            writer.WriteStartObject();
                            writer.WriteString("type", "image_url");
                            writer.WritePropertyName("image_url");
                            writer.WriteStartObject();
                            writer.WriteString("url", url);
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    else if (mp.NameEquals("images"))
                    {
                        // Already handled inside "content" above
                        continue;
                    }
                    else
                    {
                        mp.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        // ── Tools ──
        if (root.TryGetProperty("tools", out JsonElement tools))
        {
            writer.WritePropertyName("tools");
            tools.WriteTo(writer);
        }

        // ── Preserve client-supplied parameters from Ollama's "options" block ──
        // Ollama format: "options": { "temperature": 0.7, "top_p": 0.9, "num_predict": 4096 }
        bool hasOptionsBlock = root.TryGetProperty("options", out JsonElement options) && options.ValueKind == JsonValueKind.Object;

        if (hasOptionsBlock)
        {
            foreach (JsonProperty opt in options.EnumerateObject())
            {
                if (opt.NameEquals("num_predict"))
                {
                    writer.WriteNumber("max_tokens", opt.Value.GetInt32());
                }
                else if (opt.NameEquals("num_ctx") || opt.NameEquals("repeat_penalty") ||
                         opt.NameEquals("repeat_last_n") || opt.NameEquals("mirostat") ||
                         opt.NameEquals("mirostat_tau") || opt.NameEquals("mirostat_eta") ||
                         opt.NameEquals("penalize_newline") || opt.NameEquals("stop") ||
                         opt.NameEquals("tfs_z") || opt.NameEquals("typical_p") ||
                         opt.NameEquals("use_mmap") || opt.NameEquals("use_mlock") ||
                         opt.NameEquals("num_thread") || opt.NameEquals("num_gpu") ||
                         opt.NameEquals("seed") || opt.NameEquals("num_batch") ||
                         opt.NameEquals("num_keep") || opt.NameEquals("f16_kv"))
                {
                    // Skip Ollama-specific options that have no OpenAI equivalent
                    continue;
                }
                else
                {
                    opt.WriteTo(writer);
                }
            }
        }
        else
        {
            // No options block — check for top-level Ollama params (model, stream, messages, etc. already handled)
            foreach (JsonProperty prop in root.EnumerateObject())
            {
                string name = prop.Name;
                if (name == "model" || name == "stream" || name == "messages" ||
                    name == "tools" || name == "options" || name == "keep_alive" ||
                    name == "format" || name == "raw")
                {
                    continue;
                }
                prop.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string ReplaceModelInOllamaRequestBody(string rawBody, string upstreamModel)
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

    /// <summary>
    /// Reasoning models on Ollama Cloud may return `thinking` with empty `content`.
    /// This copies the `thinking` field into `content` when content is empty or missing.
    /// </summary>
    private static string EnsureOllamaContentFromThinking(string responseBody)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("message", out JsonElement msg) || msg.ValueKind != JsonValueKind.Object)
                return responseBody;

            JsonElement contentElem = msg.TryGetProperty("content", out JsonElement ce) ? ce : default;
            bool contentMissing = contentElem.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
            bool contentEmpty = !contentMissing && contentElem.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(contentElem.GetString());
            if (!contentMissing && !contentEmpty)
                return responseBody;

            if (!msg.TryGetProperty("thinking", out JsonElement thinkingElem) || thinkingElem.ValueKind != JsonValueKind.String)
                return responseBody;

            string thinking = thinkingElem.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(thinking))
                return responseBody;

            using MemoryStream ms = new();
            using Utf8JsonWriter writer = new(ms);
            writer.WriteStartObject();
            bool wroteContent = false;

            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (prop.NameEquals("message") && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("message");
                    writer.WriteStartObject();
                    foreach (JsonProperty mp in prop.Value.EnumerateObject())
                    {
                        if (mp.NameEquals("content"))
                        {
                            writer.WriteString("content", thinking);
                            wroteContent = true;
                        }
                        else
                        {
                            mp.WriteTo(writer);
                        }
                    }

                    if (!wroteContent)
                    {
                        writer.WriteString("content", thinking);
                    }

                    writer.WriteEndObject();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return responseBody;
        }
    }
}
