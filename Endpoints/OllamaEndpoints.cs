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
            // Fire-and-forget refresh to avoid blocking first request (VS 2026 BYOM times out).
            _ = modelCatalog.RefreshAvailableModelsIfNeeded(CancellationToken.None);

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
            RequestTransformer requestTransformer,
            UsageTrackerService usageTracker,
            ProxyLogger proxyLogger,
            ProviderHealthService providerHealth) =>
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

            // ── Detailed routing log ──────────────────────────────────────
            Console.WriteLine($"[ROUTE] Received model='{ollamaRequestedModel}' stream={isStream}");

            string ollamaEffectiveModel = providerRegistry.ResolveModel(ollamaRequestedModel);
            Console.WriteLine($"[ROUTE] Resolved model='{ollamaEffectiveModel}'");

            // ── Candidate list, not a single provider ─────────────────────
            // This endpoint is the Visual Studio 2026 BYOM path. It used to call
            // ResolveProvider() and commit to one provider, so an exhausted quota or a dead
            // key surfaced to the IDE as a hard failure with ten other providers sitting idle.
            IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> ollamaCandidates =
                providerRegistry.ResolveRoutePlan(ollamaEffectiveModel);
            Console.WriteLine($"[ROUTE] Candidates={ollamaCandidates.Count} [{string.Join(", ", ollamaCandidates.Select(c => $"{c.Provider.Name}:{c.UpstreamModel}"))}]");

            if (ollamaCandidates.Count == 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("""{"error":"no provider candidate available","code":"NO_CANDIDATE"}""", ct);
                return;
            }

            // ── Diagnostic headers ───────────────────────────────────────
            // Provider and upstream model are set per candidate, immediately before the
            // response body is written, so they always name the provider that actually served.
            ctx.Response.Headers["X-Proxy-Requested-Model"] = ollamaRequestedModel;
            ctx.Response.Headers["X-Proxy-Resolved-Model"] = ollamaEffectiveModel;
            ctx.Response.Headers["X-Proxy-Candidate-Count"] = ollamaCandidates.Count.ToString();

            using CancellationTokenSource? ollamaTimeoutCts = modelCatalog.CreateModelTimeoutCts(ollamaEffectiveModel, ct);
            CancellationToken ollamaCt = ollamaTimeoutCts?.Token ?? ct;

            OllamaDispatchResult lastResult = default;
            int attempts = 0;
            for (int i = 0; i < ollamaCandidates.Count; i++)
            {
                (ProviderInfo candidate, string candidateUpstream) = ollamaCandidates[i];
                attempts++;
                proxyLogger.LogRequest(candidate.Name, ollamaEffectiveModel, i + 1, ollamaCandidates.Count);

                OllamaDispatchResult result;
                try
                {
                    result = await TryDispatchOllamaChat(
                        ctx, candidate, candidateUpstream, ollamaEffectiveModel, body, isStream, i,
                        chatStreaming, reasoningCache, requestTransformer, usageTracker, ollamaCt, ct);
                }
                catch (Exception ex) when (ProxyDiagnostics.IsRetryableTransportFailure(ex, ctx, ct))
                {
                    // A provider that refuses the connection or never answers must not take the
                    // whole request down with it — that is the same "this one is unavailable, use
                    // another" case as an exhausted quota, and it used to surface to the IDE as a
                    // bare 502/504 with every other provider untried.
                    (int transportStatus, string transportBody) =
                        ProxyDiagnostics.DescribeTransportFailure(ex, candidate, ollamaEffectiveModel, out UpstreamFailure transportFailure);
                    result = new OllamaDispatchResult(DispatchOutcome.FailedRetryable, transportStatus, transportBody, transportFailure, 0);

                    Console.WriteLine($"[ERROR] Provider='{candidate.Name}' Upstream='{candidateUpstream}' {ex.GetType().Name}: {ex.Message}");
                    usageTracker.RecordError(candidate.Name, ex.GetType().Name, transportStatus.ToString(), 0, transportFailure.Kind);
                }

                if (result.Outcome == DispatchOutcome.Succeeded)
                {
                    providerHealth.RecordSuccess(candidate.Name, ollamaEffectiveModel);
                    return;
                }

                lastResult = result;
                providerHealth.RecordFailure(candidate.Name, ollamaEffectiveModel, result.Failure);

                // A malformed request fails identically everywhere — burning the remaining
                // candidates just delays the same error by several seconds.
                if (result.Outcome == DispatchOutcome.FailedTerminal)
                    break;

                if (i < ollamaCandidates.Count - 1)
                {
                    proxyLogger.LogFailover(candidate.Name, ollamaEffectiveModel, result.StatusCode, result.LatencyMs);
                    providerHealth.RecordFailover(candidate.Name, ollamaCandidates[i + 1].Provider.Name,
                        ollamaEffectiveModel, result.StatusCode, result.Failure.Kind, result.LatencyMs);
                }
            }

            // Report the last real upstream status and body. Collapsing this into a synthetic
            // 502 would hide what the provider actually said, which is the only clue the user
            // has about why their model stopped answering.
            ctx.Response.StatusCode = lastResult.StatusCode > 0 ? lastResult.StatusCode : StatusCodes.Status502BadGateway;
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers["X-Proxy-Attempts"] = attempts.ToString();
            await ctx.Response.WriteAsync(
                string.IsNullOrEmpty(lastResult.ErrorBody)
                    ? """{"error":"all provider candidates failed","code":"ALL_CANDIDATES_FAILED"}"""
                    : lastResult.ErrorBody,
                ct);
        });

        return app;
    }

    // ── Failover dispatch ────────────────────────────────────────────────────

    private enum DispatchOutcome
    {
        /// <summary>The response was written to the client. No further candidate may be tried.</summary>
        Succeeded,

        /// <summary>This provider failed but nothing was written — the next candidate may be tried.</summary>
        FailedRetryable,

        /// <summary>The request itself is bad; every other candidate would fail the same way.</summary>
        FailedTerminal
    }

    private readonly record struct OllamaDispatchResult(
        DispatchOutcome Outcome, int StatusCode, string? ErrorBody, UpstreamFailure Failure, long LatencyMs);

    /// <summary>
    /// Sends one candidate's request and, only if the upstream answered successfully, writes the
    /// response to the client.
    ///
    /// The invariant that makes failover safe: nothing is written to <paramref name="ctx"/> until
    /// the upstream status is known to be successful. Assigning <c>Response.StatusCode</c> or
    /// headers does not commit the response in ASP.NET Core — only a write or an explicit flush
    /// does — so a failed candidate leaves the response untouched and the caller can try the next.
    /// Once streaming begins, bytes are on the wire and no failover is possible.
    /// </summary>
    private static async Task<OllamaDispatchResult> TryDispatchOllamaChat(
        HttpContext ctx,
        ProviderInfo provider,
        string upstreamModel,
        string effectiveModel,
        string originalBody,
        bool isStream,
        int candidateIndex,
        ChatStreamingService chatStreaming,
        ReasoningCacheService reasoningCache,
        RequestTransformer requestTransformer,
        UsageTrackerService usageTracker,
        CancellationToken providerCt,
        CancellationToken ct)
    {
        bool nativeOllama = provider.Capabilities.ApiFormat == ApiFormat.Ollama;

        // The body is rebuilt per candidate: each provider has its own upstream model id and its
        // own parameter filtering (top_k, reasoning_effort, max_completion_tokens, Moonshot's
        // forced temperature). Reusing the first candidate's body would break the second.
        string requestBody;
        if (nativeOllama)
        {
            requestBody = ReplaceModelInOllamaRequestBody(originalBody, upstreamModel);
        }
        else
        {
            requestBody = ConvertOllamaToOpenAi(originalBody, upstreamModel, isStream);
            try
            {
                using JsonDocument openAiDoc = JsonDocument.Parse(requestBody);
                string? modifiedRequest = requestTransformer.ModifyRequest(openAiDoc);
                if (modifiedRequest is not null)
                    requestBody = modifiedRequest;
            }
            catch
            {
                // Keep original request body if pre-sanitization parsing fails.
            }
            requestBody = requestTransformer.ApplyExecutionDefaults(requestBody, effectiveModel, provider.Capabilities);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        using StringContent content = new(requestBody, Encoding.UTF8, "application/json");
        using HttpRequestMessage upstreamReq = new(HttpMethod.Post, provider.Capabilities.ChatPath) { Content = content };
        if (isStream && !nativeOllama)
            upstreamReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using HttpResponseMessage upstream = await provider.Client.SendAsync(
            upstreamReq,
            isStream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
            providerCt);
        sw.Stop();

        if (!upstream.IsSuccessStatusCode)
        {
            string errBody = await upstream.Content.ReadAsStringAsync(ct);
            UpstreamFailure failure = UpstreamFailureClassifier.Classify(
                (int)upstream.StatusCode, CollectResponseHeaders(upstream), errBody);

            Console.WriteLine($"[ERROR] Provider='{provider.Name}' Upstream='{upstreamModel}' HTTP={(int)upstream.StatusCode} Kind={failure.Kind} Latency={sw.ElapsedMilliseconds}ms Body='{Truncate(errBody, 500)}'");
            usageTracker.RecordError(provider.Name, $"HTTP {(int)upstream.StatusCode}", ((int)upstream.StatusCode).ToString(), sw.ElapsedMilliseconds);

            return new OllamaDispatchResult(
                UpstreamFailureClassifier.ShouldFailover(failure) ? DispatchOutcome.FailedRetryable : DispatchOutcome.FailedTerminal,
                (int)upstream.StatusCode, errBody, failure, sw.ElapsedMilliseconds);
        }

        return nativeOllama
            ? await WriteNativeOllamaResponse(ctx, provider, upstream, upstreamModel, effectiveModel, isStream, candidateIndex, chatStreaming, usageTracker, sw.ElapsedMilliseconds, ct)
            : await WriteConvertedOpenAiResponse(ctx, provider, upstream, upstreamModel, effectiveModel, isStream, candidateIndex, chatStreaming, reasoningCache, usageTracker, sw.ElapsedMilliseconds, ct);
    }

    /// <summary>Forwards an Ollama-native upstream response (NDJSON stream or single JSON object).</summary>
    private static async Task<OllamaDispatchResult> WriteNativeOllamaResponse(
        HttpContext ctx, ProviderInfo provider, HttpResponseMessage upstream,
        string upstreamModel, string effectiveModel, bool isStream, int candidateIndex,
        ChatStreamingService chatStreaming, UsageTrackerService usageTracker, long latencyMs, CancellationToken ct)
    {
        if (!isStream)
        {
            string respBody = await upstream.Content.ReadAsStringAsync(ct);
            // Fallback: copy `thinking` into `content` for reasoning models that leave content empty.
            respBody = EnsureOllamaContentFromThinking(respBody);

            StampWinningProvider(ctx, provider, upstreamModel, candidateIndex);
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(respBody, ct);

            RecordOllamaUsage(usageTracker, provider.Name, respBody, upstream.Headers, upstream.TrailingHeaders, latencyMs, effectiveModel);
            return Served(latencyMs);
        }

        StampWinningProvider(ctx, provider, upstreamModel, candidateIndex);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/x-ndjson";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        double cost = PricingCatalog.EstimateCostUsd(provider.Name, effectiveModel, 0, 0);
        usageTracker.RecordRequest(provider.Name, 0, 0, 0, null, latencyMs, cost);
        RecordOllamaRateLimitHeaders(usageTracker, provider.Name, upstream.Headers, upstream.TrailingHeaders);

        await chatStreaming.StreamNdjsonPassthrough(upstream, ctx.Response, ct);
        return Served(latencyMs);
    }

    /// <summary>Converts an OpenAI-format upstream response into the Ollama shape the client expects.</summary>
    private static async Task<OllamaDispatchResult> WriteConvertedOpenAiResponse(
        HttpContext ctx, ProviderInfo provider, HttpResponseMessage upstream,
        string upstreamModel, string effectiveModel, bool isStream, int candidateIndex,
        ChatStreamingService chatStreaming, ReasoningCacheService reasoningCache,
        UsageTrackerService usageTracker, long latencyMs, CancellationToken ct)
    {
        if (isStream)
        {
            StampWinningProvider(ctx, provider, upstreamModel, candidateIndex);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/x-ndjson";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            double streamCost = PricingCatalog.EstimateCostUsd(provider.Name, effectiveModel, 0, 0);
            usageTracker.RecordRequest(provider.Name, 0, 0, 0, null, latencyMs, streamCost);
            RecordOllamaRateLimitHeaders(usageTracker, provider.Name, upstream.Headers, upstream.TrailingHeaders);

            await chatStreaming.StreamOllamaAndCache(upstream, ctx.Response, effectiveModel, ct);
            return Served(latencyMs);
        }

        string respBody = await upstream.Content.ReadAsStringAsync(ct);
        reasoningCache.CacheReasoningFromResponse(respBody);
        RecordOllamaOpenAiUsage(usageTracker, provider.Name, respBody, upstream.Headers, upstream.TrailingHeaders, latencyMs, effectiveModel);

        // A 200 whose body is not an OpenAI completion is this provider's problem, not the
        // request's — another candidate may well answer properly, so this is retryable rather
        // than an immediate 502.
        if (!TryExtractAssistantMessage(respBody, out JsonElement msg, out string assistantContent))
        {
            Console.WriteLine($"[ERROR] Provider='{provider.Name}' returned an unparseable completion: '{Truncate(respBody, 500)}'");
            usageTracker.RecordError(provider.Name, "Unparseable completion", "502", latencyMs);

            string errorJson = JsonSerializer.Serialize(new
            {
                error = $"Provider '{provider.Name}' returned a response the proxy could not parse as an OpenAI completion.",
                upstream_body = Truncate(respBody, 2000)
            }, JsonDefaults.SnakeCase);

            return new OllamaDispatchResult(
                DispatchOutcome.FailedRetryable, StatusCodes.Status502BadGateway, errorJson,
                new UpstreamFailure(UpstreamFailureKind.Transient, StatusCodes.Status502BadGateway, null, QuotaPeriod.None, "unparseable-completion"),
                latencyMs);
        }

        Dictionary<string, object?> ollamaResp = new()
        {
            ["model"] = effectiveModel,
            ["created_at"] = DateTime.UtcNow.ToString("o"),
            ["message"] = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = assistantContent
            },
            ["done"] = true,
            ["done_reason"] = "stop"
        };
        if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("tool_calls", out JsonElement tcs))
            ((Dictionary<string, object?>)ollamaResp["message"]!)["tool_calls"] = tcs;

        StampWinningProvider(ctx, provider, upstreamModel, candidateIndex);
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(ollamaResp, JsonDefaults.SnakeCase), ct);
        return Served(latencyMs);
    }

    private static OllamaDispatchResult Served(long latencyMs) =>
        new(DispatchOutcome.Succeeded, StatusCodes.Status200OK, null, UpstreamFailure.Success, latencyMs);

    private static void StampWinningProvider(HttpContext ctx, ProviderInfo provider, string upstreamModel, int candidateIndex) =>
        ProxyDiagnostics.StampWinningProvider(ctx, provider, upstreamModel, candidateIndex);

    private static Dictionary<string, string?> CollectResponseHeaders(HttpResponseMessage response) =>
        ProxyDiagnostics.CollectResponseHeaders(response);

    private static string Truncate(string value, int max) => ProxyDiagnostics.Truncate(value, max);

    /// <summary>
    /// Extracts <c>choices[0].message</c> from an OpenAI-format completion.
    /// Reasoning models (DeepSeek, Nemotron, GLM) may leave <c>content</c> empty and put the
    /// answer in <c>reasoning_content</c>; that text is used as the fallback so BYOM clients
    /// never see a blank reply. Returns false when the body is not an OpenAI completion at all.
    /// </summary>
    private static bool TryExtractAssistantMessage(string responseBody, out JsonElement message, out string content)
    {
        message = default;
        content = string.Empty;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return false;
            }

            if (!choices[0].TryGetProperty("message", out JsonElement msg) || msg.ValueKind != JsonValueKind.Object)
                return false;

            // Clone so the element stays valid after the JsonDocument is disposed.
            message = msg.Clone();

            if (message.TryGetProperty("content", out JsonElement contentEl) && contentEl.ValueKind == JsonValueKind.String)
                content = contentEl.GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(content)
                && message.TryGetProperty("reasoning_content", out JsonElement reasoningEl)
                && reasoningEl.ValueKind == JsonValueKind.String)
            {
                content = reasoningEl.GetString() ?? string.Empty;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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
    /// Records usage from an Ollama-format response body (non-streaming).
    /// Parses <c>prompt_eval_count</c> and <c>eval_count</c>.
    /// </summary>
    private static void RecordOllamaUsage(
        UsageTrackerService usageTracker,
        string providerName,
        string responseBody,
        HttpHeaders responseHeaders,
        HttpHeaders? trailingHeaders,
        long latencyMs = 0,
        string model = "")
    {
        var headers = CollectHeadersDict(responseHeaders, trailingHeaders);

        long promptTokens = 0, completionTokens = 0, totalTokens = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("prompt_eval_count", out JsonElement pec) && pec.ValueKind == JsonValueKind.Number)
                promptTokens = pec.GetInt64();
            if (root.TryGetProperty("eval_count", out JsonElement ec) && ec.ValueKind == JsonValueKind.Number)
                completionTokens = ec.GetInt64();
            if (promptTokens > 0 || completionTokens > 0)
                totalTokens = promptTokens + completionTokens;
        }
        catch { }

        double cost = (promptTokens > 0 || completionTokens > 0) ? PricingCatalog.EstimateCostUsd(providerName, model, promptTokens, completionTokens) : 0;
        usageTracker.RecordRequest(providerName, promptTokens, completionTokens, totalTokens, headers, latencyMs, cost);
    }

    /// <summary>
    /// Records usage from an OpenAI-format response body reached via the Ollama endpoint path.
    /// </summary>
    private static void RecordOllamaOpenAiUsage(
        UsageTrackerService usageTracker,
        string providerName,
        string responseBody,
        HttpHeaders responseHeaders,
        HttpHeaders? trailingHeaders,
        long latencyMs = 0,
        string model = "")
    {
        var headers = CollectHeadersDict(responseHeaders, trailingHeaders);

        long promptTokens = 0, completionTokens = 0, totalTokens = 0;
        bool hasUsage = false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("usage", out JsonElement usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out JsonElement pt) && pt.ValueKind == JsonValueKind.Number)
                { promptTokens = pt.GetInt64(); hasUsage = true; }
                if (usage.TryGetProperty("completion_tokens", out JsonElement ct) && ct.ValueKind == JsonValueKind.Number)
                { completionTokens = ct.GetInt64(); hasUsage = true; }
                if (usage.TryGetProperty("total_tokens", out JsonElement tt) && tt.ValueKind == JsonValueKind.Number)
                { totalTokens = tt.GetInt64(); hasUsage = true; }
                if (totalTokens == 0 && promptTokens > 0 && completionTokens > 0)
                    totalTokens = promptTokens + completionTokens;
            }
        }
        catch { }

        double cost = hasUsage ? PricingCatalog.EstimateCostUsd(providerName, model, promptTokens, completionTokens) : 0;
        usageTracker.RecordRequest(providerName, promptTokens, completionTokens, totalTokens, headers, latencyMs, cost);
    }

    /// <summary>
    /// Records rate-limit headers from upstream response headers (for streaming paths).
    /// </summary>
    private static void RecordOllamaRateLimitHeaders(
        UsageTrackerService usageTracker,
        string providerName,
        HttpHeaders responseHeaders,
        HttpHeaders? trailingHeaders)
    {
        var headers = CollectHeadersDict(responseHeaders, trailingHeaders);
        if (headers.Count > 0)
            usageTracker.RecordRateLimitHeaders(providerName, headers);
    }

    /// <summary>
    /// Collects HTTP headers into a dictionary for rate-limit extraction.
    /// </summary>
    private static Dictionary<string, string?> CollectHeadersDict(HttpHeaders responseHeaders, HttpHeaders? trailingHeaders)
    {
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in responseHeaders)
            headers[h.Key] = string.Join(", ", h.Value);
        if (trailingHeaders is not null)
        {
            foreach (var h in trailingHeaders)
                headers[h.Key] = string.Join(", ", h.Value);
        }
        return headers;
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
