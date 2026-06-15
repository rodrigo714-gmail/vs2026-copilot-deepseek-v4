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
            // any models discovered via provider APIs. This ensures models from
            // config/*.json are always listed even when a provider's API is down.
            // Dynamic discovery is done in the background (fire-and-forget) so the
            // response is not delayed by slow provider API calls.
            _ = modelCatalog.RefreshAvailableModelsIfNeeded(ctx.RequestAborted);

            // Collect all enabled models from the static config files as a stable baseline.
            // Each provider's configured match strings are the authoritative list.
            List<(string Provider, string Model)> allModels = [];

            foreach ((string providerName, ModelSelectionEntry[] entries) in modelSelectionStore.ProviderModelSelections)
            {
                foreach (ModelSelectionEntry entry in entries)
                {
                    if (!entry.Enabled)
                        continue;

                    string model = entry.Match;
                    // Some upstream model IDs already include the provider name prefix
                    // (e.g. "nvidia/llama-3.1-nemotron-70b-instruct"). Avoid duplication.
                    string displayId = model.StartsWith(providerName + "/", StringComparison.OrdinalIgnoreCase)
                        ? model
                        : $"{providerName}/{model}";

                    allModels.Add((providerName, displayId));
                }
            }

            // Also include any bare models discovered from provider APIs that aren't
            // in the static config (deduplicate by display id).
            HashSet<string> seen = new(allModels.Select(m => m.Model), StringComparer.OrdinalIgnoreCase);
            foreach (string discovered in modelCatalog.AvailableModels)
            {
                if (discovered.Contains('@'))
                    continue; // skip qualified aliases

                string providerName = providerRegistry.ModelToProvider.TryGetValue(discovered, out ProviderInfo prov)
                    ? prov.Name
                    : "unknown";

                string displayId = discovered.StartsWith(providerName + "/", StringComparison.OrdinalIgnoreCase)
                    ? discovered
                    : $"{providerName}/{discovered}";

                if (seen.Add(displayId))
                {
                    allModels.Add((providerName, displayId));
                }
            }

            // Sort by provider name then model name.
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

        app.MapGet("/v1/models/{modelId}", (string modelId, ProviderRegistry providerRegistry, ModelCatalogService modelCatalog) =>
        {
            string resolvedModel = providerRegistry.ResolveModel(modelId);
            string upstreamModel = providerRegistry.ResolveUpstreamModel(modelId);

            (int contextLength, int maxOutputTokens, bool supportsTools, bool supportsVision, string[] capabilities, string family) =
                modelCatalog.GetModelProfile(resolvedModel);

            ModelExecutionConfig execConfig = modelCatalog.GetExecutionConfigForModel(resolvedModel);

            providerRegistry.ModelToProvider.TryGetValue(resolvedModel, out ProviderInfo prov);
            string ownedBy = prov.Name ?? "deepseek";

            return Results.Json(new
            {
                id = resolvedModel,
                @object = "model",
                created = 1700000000,
                owned_by = ownedBy,
                context_length = contextLength,
                max_output_tokens = maxOutputTokens,
                supports_tools = supportsTools,
                supports_vision = supportsVision,
                capabilities = capabilities,
                family = family,
                upstream_model = upstreamModel,
                max_tokens_preferred = execConfig.MaxTokensPreferred,
                reasoning_effort = execConfig.ReasoningEffort
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
            IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> candidates = providerRegistry.ResolveCandidates(effectiveModel);

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
                        candidateBody = requestTransformer.ApplyExecutionDefaults(candidateBody, effectiveModel, candidateProvider.Name);

                        if (candidateProvider.Name.Equals("ollama", StringComparison.OrdinalIgnoreCase))
                        {
                            bool handled = await TryHandleOllamaCloudChatCompletion(
                                ctx, candidateProvider, candidateBody, effectiveModel, candidateUpstream, requestCt, ct);
                            if (handled)
                                return;
                            continue;
                        }

                        using StringContent content = new(candidateBody, Encoding.UTF8, "application/json");
                        HttpResponseMessage response = await candidateProvider.Client.SendAsync(
                            new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = content },
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
            bodyText = requestTransformer.ApplyExecutionDefaults(bodyText, effectiveModel, provider.Name);

            if (provider.Name.Equals("ollama", StringComparison.OrdinalIgnoreCase))
            {
                await HandleOllamaCloudChatCompletion(ctx, provider, bodyText, effectiveModel, upstreamModel, isStream, requestCt, ct);
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            using StringContent reqContent = new(bodyText, Encoding.UTF8, "application/json");
            using HttpRequestMessage upstreamReq = new(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = reqContent
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
            new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = content },
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
            new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = content },
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

        if (root.TryGetProperty("messages", out JsonElement messages))
        {
            writer.WritePropertyName("messages");
            messages.WriteTo(writer);
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