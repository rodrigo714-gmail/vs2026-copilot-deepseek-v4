using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

internal static class OpenAiEndpoints
{
    internal static IEndpointRouteBuilder MapOpenAiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/models", (HttpContext ctx, ModelCatalogService modelCatalog, ProviderRegistry providerRegistry, ModelSelectionStore modelSelectionStore) =>
        {
            // Build a complete list from static config files (always available) plus
            // any models discovered from provider catalogs. The id format MUST match
            // what ProviderRegistry.ResolveModel / ResolveCandidates can actually
            // route: bare "model" and qualified "model@provider" (the internal
            // mapping built by ModelCatalogService). Listing "provider/model" would
            // not be routable on POST /v1/chat/completions.
            _ = modelCatalog.RefreshAvailableModelsIfNeeded(ctx.RequestAborted);

            List<(string Provider, string Model)> allModels = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            // 1) Enumerate everything already in the live catalog (covers both bare
            //    and qualified aliases that the routing layer actually accepts).
            foreach (string modelId in modelCatalog.AvailableModels)
            {
                if (string.IsNullOrWhiteSpace(modelId))
                    continue;

                string providerName;
                string displayModel;

                int at = modelId.IndexOf('@');
                if (at > 0 && at < modelId.Length - 1)
                {
                    string upstreamPart = modelId[..at];
                    string provPart = modelId[(at + 1)..];
                    displayModel = upstreamPart;
                    providerName = provPart;
                }
                else
                {
                    displayModel = modelId;
                    providerName = providerRegistry.ModelToProvider.TryGetValue(modelId, out ProviderInfo prov)
                        ? prov.Name
                        : "unknown";
                }

                if (seen.Add(modelId))
                {
                    allModels.Add((providerName, modelId));
                }
                // Also surface the bare form when the catalog only registered the
                // qualified one (helps clients that prefer short ids).
                if (modelId.Contains('@'))
                {
                    string bare = modelId[..modelId.IndexOf('@')];
                    if (seen.Add(bare))
                    {
                        allModels.Add((providerName, bare));
                    }
                }
                _ = displayModel; // currently unused beyond the assignments above
            }

            // 2) Add any model known by its upstream id but not present yet
            //    (defensive: catalogs populated outside the discoverer).
            foreach (KeyValuePair<string, ProviderInfo> kv in providerRegistry.ModelToProvider)
            {
                if (seen.Add(kv.Key))
                {
                    allModels.Add((kv.Value.Name, kv.Key));
                }
            }

            // Sort by provider name then model name for stable output.
            allModels = allModels.OrderBy(m => m.Provider, StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            return Results.Json(new
            {
                @object = "list",
                data = allModels.Select(m => new
                {
                    id = m.Model,
                    @object = "model",
                    created = 1700000000,
                    owned_by = m.Provider
                }).ToArray()
            }, JsonDefaults.SnakeCase);
        });

        app.MapPost("/v1/chat/completions", async (
            HttpContext ctx,
            ProviderRegistry providerRegistry,
            RequestTransformer requestTransformer,
            ModelCatalogService modelCatalog,
            ChatStreamingService chatStreaming,
            ReasoningCacheService reasoningCache) =>
        {
            CancellationToken ct = ctx.RequestAborted;

            using StreamReader bodyReader = new(ctx.Request.Body, Encoding.UTF8, false, 1024);
            string rawBody = await bodyReader.ReadToEndAsync(ct);

            using JsonDocument doc = JsonDocument.Parse(rawBody);
            JsonElement root = doc.RootElement;
            bool isStream = root.TryGetProperty("stream", out JsonElement sp) && sp.GetBoolean();

            string reqModel = root.TryGetProperty("model", out JsonElement rm) && rm.ValueKind == JsonValueKind.String
                ? rm.GetString()! : providerRegistry.DefaultModel;
            string effectiveModel = providerRegistry.ResolveModel(reqModel);
            // Honour an explicit OpenAI-style "provider/model" hint so the request goes
            // to the requested provider even when the bare model id is owned by a
            // different one in the catalog.
            ProviderInfo? requestedProvider = ExtractProviderHint(reqModel, providerRegistry);
            IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> candidates;
            if (requestedProvider is { } pinnedHint)
            {
                string upstream = providerRegistry.ResolveUpstreamModel(effectiveModel);
                candidates = [(pinnedHint, upstream)];
            }
            else
            {
                candidates = providerRegistry.ResolveCandidates(effectiveModel);
            }

            // ── Diagnostic headers ───────────────────────────────────────
            ctx.Response.Headers["X-Proxy-Requested-Model"] = reqModel;
            ctx.Response.Headers["X-Proxy-Resolved-Model"] = effectiveModel;
            ctx.Response.Headers["X-Proxy-Candidate-Count"] = candidates.Count.ToString();
            if (candidates.Count > 0)
            {
                ctx.Response.Headers["X-Proxy-Primary-Provider"] = candidates[0].Provider.Name;
                ctx.Response.Headers["X-Proxy-Primary-Upstream"] = candidates[0].UpstreamModel;
            }

            string? modifiedRequest = requestTransformer.ModifyRequest(doc);

            using CancellationTokenSource? timeoutCts = modelCatalog.CreateModelTimeoutCts(effectiveModel, ct);
            CancellationToken requestCt = timeoutCts?.Token ?? ct;

            if (!isStream)
            {
                HttpResponseMessage? lastResponse = null;
                string? lastBody = null;
                try
                {
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        (ProviderInfo candidateProvider, string candidateUpstream) = candidates[i];

                        string candidateBody = modifiedRequest ?? rawBody;
                        // Always replace the model in the body with the upstream model.
                        // The raw body may carry a BYOM tag suffix (e.g. ":latest") that
                        // upstream providers don't understand.
                        candidateBody = requestTransformer.ReplaceModelInRequestBody(candidateBody, candidateUpstream);
                        candidateBody = requestTransformer.ApplyExecutionDefaults(candidateBody, effectiveModel, candidateProvider.Capabilities);

                        if (candidateProvider.Capabilities.ApiFormat == ApiFormat.Ollama)
                        {
                            bool handled = await TryHandleOllamaCloudChatCompletion(
                                ctx, candidateProvider, candidateBody, effectiveModel, candidateUpstream, requestCt, ct);
                            if (handled)
                                return;
                            continue;
                        }

                        using StringContent content = new(candidateBody, Encoding.UTF8, "application/json");
                        HttpResponseMessage response = await candidateProvider.Client.SendAsync(
                            new HttpRequestMessage(HttpMethod.Post, candidateProvider.Capabilities.ChatPath) { Content = content },
                            requestCt);

                        string respBody = await response.Content.ReadAsStringAsync(ct);

                        if (response.IsSuccessStatusCode)
                        {
                            reasoningCache.CacheReasoningFromResponse(respBody);
                            ctx.Response.StatusCode = (int)response.StatusCode;
                            ctx.Response.ContentType = "application/json";
                            await ctx.Response.WriteAsync(respBody, ct);
                            response.Dispose();
                            return;
                        }

                        lastResponse?.Dispose();
                        lastResponse = response;
                        lastBody = respBody;
                        // Try next provider candidate (failover by configured priority).
                    }

                    // All candidates failed: surface the last upstream error.
                    ctx.Response.StatusCode = lastResponse is not null ? (int)lastResponse.StatusCode : StatusCodes.Status502BadGateway;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(lastBody ?? "{\"error\":\"no provider candidate available\"}", ct);
                }
                finally
                {
                    lastResponse?.Dispose();
                }
                return;
            }

            // Streaming: use the first candidate only (cannot fail over once bytes are emitted).
            (ProviderInfo provider, string upstreamModel) = candidates[0];

            string bodyText = modifiedRequest ?? rawBody;
            // Always replace the model in the body with the upstream model.
            // The raw body may carry a BYOM tag suffix (e.g. ":latest") that
            // upstream providers don't understand.
            bodyText = requestTransformer.ReplaceModelInRequestBody(bodyText, upstreamModel);
            bodyText = requestTransformer.ApplyExecutionDefaults(bodyText, effectiveModel, provider.Capabilities);

            if (provider.Capabilities.ApiFormat == ApiFormat.Ollama)
            {
                await HandleOllamaCloudChatCompletion(ctx, provider, bodyText, effectiveModel, upstreamModel, isStream, requestCt, ct);
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            using StringContent reqContent = new(bodyText, Encoding.UTF8, "application/json");
            using HttpRequestMessage upstreamReq = new(HttpMethod.Post, provider.Capabilities.ChatPath)
            {
                Content = reqContent,
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            upstreamReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using HttpResponseMessage upstreamResp = await provider.Client.SendAsync(
                upstreamReq, HttpCompletionOption.ResponseHeadersRead, requestCt);

            if (!upstreamResp.IsSuccessStatusCode)
            {
                string errBody = await upstreamResp.Content.ReadAsStringAsync(ct);
                ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(errBody, ct);
                return;
            }

            await chatStreaming.StreamAndCache(upstreamResp, ctx.Response, ct);
        });

        return app;
    }

    /// <summary>
    /// Resolves an explicit "provider/model" hint from the request id to a
    /// <see cref="ProviderInfo"/>. Returns null when the hint is absent, ambiguous,
    /// or points at a provider the registry does not know about.
    /// </summary>
    private static ProviderInfo? ExtractProviderHint(string? requestedModel, ProviderRegistry providerRegistry)
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
            return null;

        int slash = requestedModel.IndexOf('/');
        if (slash <= 0 || slash >= requestedModel.Length - 1)
            return null;

        string providerHint = requestedModel[..slash];
        foreach (ProviderInfo prov in providerRegistry.Providers)
        {
            if (string.Equals(prov.Name, providerHint, StringComparison.OrdinalIgnoreCase))
                return prov;
        }

        return null;
    }

    /// <summary>
    /// Attempts an Ollama Cloud chat completion as part of failover.
    /// Returns true if the response was written to the client; false if the candidate failed and the caller should try the next one.
    /// </summary>
    private static async Task<bool> TryHandleOllamaCloudChatCompletion(
        HttpContext ctx,
        ProviderInfo provider,
        string openAiRequestBody,
        string effectiveModel,
        string upstreamModel,
        CancellationToken requestCt,
        CancellationToken clientCt)
    {
        string ollamaRequestBody = BuildOllamaChatRequest(openAiRequestBody, upstreamModel, isStream: false);

        using StringContent content = new(ollamaRequestBody, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await provider.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, provider.Capabilities.ChatPath) { Content = content },
            requestCt);

        string respBody = await response.Content.ReadAsStringAsync(clientCt);
        if (!response.IsSuccessStatusCode)
            return false;

        string openAiResponseBody = ConvertOllamaChatToOpenAiCompletion(respBody, effectiveModel);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(openAiResponseBody, clientCt);
        return true;
    }

    private static async Task HandleOllamaCloudChatCompletion(
        HttpContext ctx,
        ProviderInfo provider,
        string openAiRequestBody,
        string effectiveModel,
        string upstreamModel,
        bool isStream,
        CancellationToken requestCt,
        CancellationToken clientCt)
    {
        string ollamaRequestBody = BuildOllamaChatRequest(openAiRequestBody, upstreamModel, isStream: false);

        using StringContent content = new(ollamaRequestBody, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await provider.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, provider.Capabilities.ChatPath) { Content = content },
            requestCt);

        string respBody = await response.Content.ReadAsStringAsync(clientCt);
        if (!response.IsSuccessStatusCode)
        {
            ctx.Response.StatusCode = (int)response.StatusCode;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(respBody, clientCt);
            return;
        }

        string openAiResponseBody = ConvertOllamaChatToOpenAiCompletion(respBody, effectiveModel);

        using JsonDocument completionDoc = JsonDocument.Parse(openAiResponseBody);
        JsonElement msg = completionDoc.RootElement.GetProperty("choices")[0].GetProperty("message");
        string contentText = msg.TryGetProperty("content", out JsonElement ce) && ce.ValueKind == JsonValueKind.String
            ? ce.GetString() ?? string.Empty
            : string.Empty;

        if (!isStream)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(openAiResponseBody, clientCt);
            return;
        }

        // Streaming: Ollama Cloud non-streaming -> SSE chunks
        object firstChunk = new
        {
            id = $"chatcmpl-{Guid.NewGuid():N}",
            @object = "chat.completion.chunk",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = effectiveModel,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { role = "assistant", content = contentText },
                    finish_reason = (string?)null
                }
            }
        };

        object finishChunk = new
        {
            id = $"chatcmpl-{Guid.NewGuid():N}",
            @object = "chat.completion.chunk",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = effectiveModel,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = "stop"
                }
            }
        };

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(firstChunk, JsonDefaults.SnakeCase)}\n\n", clientCt);
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(finishChunk, JsonDefaults.SnakeCase)}\n\n", clientCt);
        await ctx.Response.WriteAsync("data: [DONE]\n\n", clientCt);
    }

    private static string BuildOllamaChatRequest(string openAiRequestBody, string model, bool isStream)
    {
        using JsonDocument openAiDoc = JsonDocument.Parse(openAiRequestBody);
        JsonElement root = openAiDoc.RootElement;

        using MemoryStream ms = new();
        using Utf8JsonWriter writer = new(ms);

        writer.WriteStartObject();
        writer.WriteString("model", model);
        writer.WriteBoolean("stream", isStream);

        // ── Messages ──
        // Convert OpenAI multi-part content (text + image_url parts) into Ollama format:
        //   - text parts → "content" string
        //   - image_url parts → "images" array of base64 data URLs
        if (root.TryGetProperty("messages", out JsonElement messages))
        {
            writer.WritePropertyName("messages");
            writer.WriteStartArray();

            foreach (JsonElement msg in messages.EnumerateArray())
            {
                writer.WriteStartObject();

                bool hasMultiPartContent = false;
                List<string> imageUrls = [];

                // Determine if content is multi-part array with images
                if (msg.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
                {
                    hasMultiPartContent = true;
                    StringBuilder textContent = new();

                    foreach (JsonElement part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("type", out JsonElement type) && type.GetString() == "text")
                        {
                            if (part.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                            {
                                if (textContent.Length > 0)
                                    textContent.Append('\n');
                                textContent.Append(text.GetString());
                            }
                        }
                        else if (type.GetString() == "image_url")
                        {
                            if (part.TryGetProperty("image_url", out JsonElement imgUrl) && imgUrl.ValueKind == JsonValueKind.Object)
                            {
                                if (imgUrl.TryGetProperty("url", out JsonElement url) && url.ValueKind == JsonValueKind.String)
                                {
                                    imageUrls.Add(url.GetString()!);
                                }
                            }
                        }
                    }

                    writer.WriteString("content", textContent.ToString());
                }

                // Copy remaining properties (role, tool_calls, etc.) but skip content if already written
                foreach (JsonProperty mp in msg.EnumerateObject())
                {
                    if (mp.NameEquals("content") && hasMultiPartContent)
                        continue; // already written
                    mp.WriteTo(writer);
                }

                // Write images array if any image_url parts were found
                if (imageUrls.Count > 0)
                {
                    writer.WritePropertyName("images");
                    writer.WriteStartArray();
                    foreach (string imgUrl in imageUrls)
                    {
                        writer.WriteStringValue(imgUrl);
                    }
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (root.TryGetProperty("tools", out JsonElement tools))
        {
            writer.WritePropertyName("tools");
            tools.WriteTo(writer);
        }

        bool hasTemperature = root.TryGetProperty("temperature", out JsonElement temp);
        bool hasTopP = root.TryGetProperty("top_p", out JsonElement topP);
        bool hasMaxTokens = root.TryGetProperty("max_tokens", out JsonElement maxTokens);

        if (hasTemperature || hasTopP || hasMaxTokens)
        {
            writer.WritePropertyName("options");
            writer.WriteStartObject();
            if (hasTemperature && temp.ValueKind == JsonValueKind.Number)
            {
                writer.WriteNumber("temperature", temp.GetDouble());
            }

            if (hasTopP && topP.ValueKind == JsonValueKind.Number)
            {
                writer.WriteNumber("top_p", topP.GetDouble());
            }

            if (hasMaxTokens && maxTokens.ValueKind == JsonValueKind.Number)
            {
                writer.WriteNumber("num_predict", maxTokens.GetInt32());
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string ConvertOllamaChatToOpenAiCompletion(string ollamaResponseBody, string effectiveModel)
    {
        using JsonDocument ollamaDoc = JsonDocument.Parse(ollamaResponseBody);
        JsonElement root = ollamaDoc.RootElement;
        JsonElement message = root.TryGetProperty("message", out JsonElement msg) ? msg : default;

        string content = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out JsonElement contentElement)
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;

        // Fallback to `thinking` when content is empty (reasoning models put text in `thinking`).
        if (string.IsNullOrWhiteSpace(content) && message.ValueKind == JsonValueKind.Object && message.TryGetProperty("thinking", out JsonElement thinkingElement))
        {
            content = thinkingElement.GetString() ?? string.Empty;
        }

        object completion = new
        {
            id = $"chatcmpl-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = effectiveModel,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content,
                        tool_calls = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("tool_calls", out JsonElement tcs)
                            ? tcs
                            : (JsonElement?)null
                    },
                    finish_reason = root.TryGetProperty("done_reason", out JsonElement dr) && dr.ValueKind == JsonValueKind.String
                        ? dr.GetString()
                        : "stop"
                }
            }
        };

        return JsonSerializer.Serialize(completion, JsonDefaults.SnakeCase);
    }
}