using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class OpenAiEndpoints
{
    internal static IEndpointRouteBuilder MapOpenAiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/models", (HttpContext ctx, ModelCatalogService modelCatalog, ProviderRegistry providerRegistry, ModelSelectionStore modelSelectionStore) =>
        {
            _ = modelCatalog.RefreshAvailableModelsIfNeeded(ctx.RequestAborted);

            List<(string Provider, string Model)> allModels = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

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
                if (modelId.Contains('@'))
                {
                    string bare = modelId[..modelId.IndexOf('@')];
                    if (seen.Add(bare))
                    {
                        allModels.Add((providerName, bare));
                    }
                }
                _ = displayModel;
            }

            foreach (KeyValuePair<string, ProviderInfo> kv in providerRegistry.ModelToProvider)
            {
                if (seen.Add(kv.Key))
                {
                    allModels.Add((kv.Value.Name, kv.Key));
                }
            }

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
            ReasoningCacheService reasoningCache,
            UsageTrackerService usageTracker,
            ProxyLogger proxyLogger,
            ProviderHealthService providerHealth) =>
        {
            CancellationToken ct = ctx.RequestAborted;

            using StreamReader bodyReader = new(ctx.Request.Body, Encoding.UTF8, false, 1024);
            string rawBody = await bodyReader.ReadToEndAsync(ct);

            using JsonDocument doc = JsonDocument.Parse(rawBody);
            JsonElement root = doc.RootElement;
            bool isStream = root.TryGetProperty("stream", out JsonElement sp) && sp.GetBoolean();

            string reqModel = root.TryGetProperty("model", out JsonElement rm) && rm.ValueKind == JsonValueKind.String
                ? rm.GetString()! : providerRegistry.DefaultModel;

            Console.WriteLine($"[OPENAI-ROUTE] Received model='{reqModel}' stream={isStream}");

            // Validate model exists — no silent fallback to default provider
            if (!providerRegistry.IsModelKnown(reqModel))
            {
                Console.WriteLine($"[OPENAI-ROUTE] Model rejected by IsModelKnown: '{reqModel}' (len={reqModel.Length}, hex={string.Join(" ", reqModel.Select(c => ((int)c).ToString("x2")))})");
                ctx.Response.StatusCode = 400;
                ctx.Response.ContentType = "application/json";
                var modelList = providerRegistry.ModelToProvider.Keys.OrderBy(k => k).Take(30);
                await ctx.Response.WriteAsync($"{{\"error\":\"Model '{System.Text.Json.JsonEncodedText.Encode(reqModel)}' is not mapped to any provider. Available models: {string.Join(", ", modelList)}\",\"code\":\"MODEL_NOT_FOUND\"}}", ct);
                return;
            }

            string effectiveModel = providerRegistry.ResolveModel(reqModel);
            Console.WriteLine($"[OPENAI-ROUTE] Resolved model='{effectiveModel}'");

            ProviderInfo? requestedProvider = ExtractProviderHint(reqModel, providerRegistry);
            IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> candidates;
            if (requestedProvider is { } pinnedHint)
            {
                string upstream = providerRegistry.ResolveUpstreamModel(effectiveModel);
                candidates = [(pinnedHint, upstream)];
            }
            else
            {
                candidates = providerRegistry.ResolveRoutePlan(effectiveModel);
            }

            if (candidates.Count > 0)
            {
                Console.WriteLine($"[OPENAI-ROUTE] Candidate[0] Provider='{candidates[0].Provider.Name}' Upstream='{candidates[0].UpstreamModel}' BaseUrl='{candidates[0].Provider.Client.BaseAddress}' ChatPath='{candidates[0].Provider.Capabilities.ChatPath}'");
            }
            else
            {
                Console.WriteLine($"[OPENAI-ROUTE] No candidates resolved for effectiveModel='{effectiveModel}'");
            }

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
                int lastStatus = 0;
                string? lastBody = null;
                int attempts = 0;

                for (int i = 0; i < candidates.Count; i++)
                {
                    (ProviderInfo candidateProvider, string candidateUpstream) = candidates[i];
                    attempts++;
                    proxyLogger.LogRequest(candidateProvider.Name, effectiveModel, i + 1, candidates.Count);

                    string candidateBody = modifiedRequest ?? rawBody;
                    candidateBody = requestTransformer.ReplaceModelInRequestBody(candidateBody, candidateUpstream);
                    candidateBody = requestTransformer.ApplyExecutionDefaults(candidateBody, effectiveModel, candidateProvider.Capabilities);

                    UpstreamFailure failure;
                    long latencyMs;

                    try
                    {
                        if (candidateProvider.Capabilities.ApiFormat == ApiFormat.Ollama)
                        {
                            var swOllama = System.Diagnostics.Stopwatch.StartNew();
                            OllamaCandidateResult ollamaResult = await TryHandleOllamaCloudChatCompletion(
                                ctx, candidateProvider, candidateBody, effectiveModel, candidateUpstream, requestCt, ct);
                            swOllama.Stop();

                            if (ollamaResult.Handled)
                            {
                                StampWinningProvider(ctx, candidateProvider, candidateUpstream, i);
                                providerHealth.RecordSuccess(candidateProvider.Name, effectiveModel);
                                return;
                            }

                            failure = ollamaResult.Failure;
                            latencyMs = swOllama.ElapsedMilliseconds;
                            lastStatus = ollamaResult.StatusCode;
                            lastBody = ollamaResult.ErrorBody;
                            usageTracker.RecordError(candidateProvider.Name, $"HTTP {ollamaResult.StatusCode}", ollamaResult.StatusCode.ToString(), latencyMs);
                        }
                        else
                        {
                            using StringContent content = new(candidateBody, Encoding.UTF8, "application/json");
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            using HttpResponseMessage response = await candidateProvider.Client.SendAsync(
                                new HttpRequestMessage(HttpMethod.Post, candidateProvider.Capabilities.ChatPath) { Content = content },
                                requestCt);
                            sw.Stop();

                            string respBody = await response.Content.ReadAsStringAsync(ct);

                            if (response.IsSuccessStatusCode)
                            {
                                reasoningCache.CacheReasoningFromResponse(respBody);
                                StampWinningProvider(ctx, candidateProvider, candidateUpstream, i);
                                ctx.Response.StatusCode = (int)response.StatusCode;
                                ctx.Response.ContentType = "application/json";
                                await ctx.Response.WriteAsync(respBody, ct);

                                RecordUsageFromResponse(usageTracker, candidateProvider.Name, respBody, response.Headers, response.TrailingHeaders, sw.ElapsedMilliseconds, effectiveModel);
                                providerHealth.RecordSuccess(candidateProvider.Name, effectiveModel);
                                return;
                            }

                            failure = UpstreamFailureClassifier.Classify((int)response.StatusCode, CollectResponseHeaders(response), respBody);
                            latencyMs = sw.ElapsedMilliseconds;
                            lastStatus = (int)response.StatusCode;
                            lastBody = respBody;

                            Console.WriteLine($"[OPENAI-ERROR] Provider='{candidateProvider.Name}' Upstream='{candidateUpstream}' HTTP={lastStatus} Kind={failure.Kind} RespBody='{respBody[..Math.Min(respBody.Length, 500)]}' Latency={latencyMs}ms");
                            usageTracker.RecordError(candidateProvider.Name, $"HTTP {lastStatus}", lastStatus.ToString(), latencyMs);
                        }
                    }
                    catch (Exception ex) when (ProxyDiagnostics.IsRetryableTransportFailure(ex, ctx, ct))
                    {
                        // Connection refused, DNS failure, or nothing back within timeout_seconds.
                        // That is the same "this provider is unavailable" case failover exists for;
                        // it used to abort the request with every other candidate untried.
                        (lastStatus, lastBody) = ProxyDiagnostics.DescribeTransportFailure(ex, candidateProvider, effectiveModel, out failure);
                        latencyMs = 0;

                        Console.WriteLine($"[OPENAI-ERROR] Provider='{candidateProvider.Name}' Upstream='{candidateUpstream}' {ex.GetType().Name}: {ex.Message}");
                        usageTracker.RecordError(candidateProvider.Name, ex.GetType().Name, lastStatus.ToString(), 0, failure.Kind);
                    }

                    providerHealth.RecordFailure(candidateProvider.Name, effectiveModel, failure);

                    // A malformed request fails identically at every provider. Retrying it down
                    // the whole candidate list only delays the same error by several seconds.
                    if (!UpstreamFailureClassifier.ShouldFailover(failure))
                        break;

                    if (i < candidates.Count - 1)
                    {
                        proxyLogger.LogFailover(candidateProvider.Name, effectiveModel, lastStatus, latencyMs);
                        providerHealth.RecordFailover(candidateProvider.Name, candidates[i + 1].Provider.Name,
                            effectiveModel, lastStatus, failure.Kind, latencyMs);
                    }
                }

                ctx.Response.StatusCode = lastStatus > 0 ? lastStatus : StatusCodes.Status502BadGateway;
                ctx.Response.ContentType = "application/json";
                ctx.Response.Headers["X-Proxy-Attempts"] = attempts.ToString();
                await ctx.Response.WriteAsync(
                    string.IsNullOrEmpty(lastBody)
                        ? """{"error":"no provider candidate available","code":"NO_CANDIDATE"}"""
                        : lastBody,
                    ct);
                return;
            }

            // Streaming
            // Streaming failover. `HttpCompletionOption.ResponseHeadersRead` hands back the
            // upstream status before a single body byte arrives, and assigning response headers
            // does not commit the response — only a write or a flush does. So everything up to
            // the success check below is still retryable, and this loop used to be
            // `candidates[0]` with no retry at all.
            int lastStreamStatus = 0;
            string? lastStreamBody = null;
            int streamAttempts = 0;

            for (int i = 0; i < candidates.Count; i++)
            {
                (ProviderInfo provider, string upstreamModel) = candidates[i];
                streamAttempts++;
                proxyLogger.LogRequest(provider.Name, effectiveModel, i + 1, candidates.Count);

                string bodyText = modifiedRequest ?? rawBody;
                bodyText = requestTransformer.ReplaceModelInRequestBody(bodyText, upstreamModel);
                bodyText = requestTransformer.ApplyExecutionDefaults(bodyText, effectiveModel, provider.Capabilities);

                try
                {
                    if (provider.Capabilities.ApiFormat == ApiFormat.Ollama)
                    {
                        OllamaCandidateResult ollamaStream = await HandleOllamaCloudChatCompletion(
                            ctx, provider, bodyText, effectiveModel, upstreamModel, isStream, i, requestCt, ct);

                        if (ollamaStream.Handled)
                        {
                            providerHealth.RecordSuccess(provider.Name, effectiveModel);
                            return;
                        }

                        lastStreamStatus = ollamaStream.StatusCode;
                        lastStreamBody = ollamaStream.ErrorBody;
                        usageTracker.RecordError(provider.Name, $"HTTP {ollamaStream.StatusCode}", ollamaStream.StatusCode.ToString(), 0);
                        providerHealth.RecordFailure(provider.Name, effectiveModel, ollamaStream.Failure);

                        if (!UpstreamFailureClassifier.ShouldFailover(ollamaStream.Failure))
                            break;
                        if (i < candidates.Count - 1)
                        {
                            proxyLogger.LogFailover(provider.Name, effectiveModel, lastStreamStatus, 0);
                            providerHealth.RecordFailover(provider.Name, candidates[i + 1].Provider.Name,
                                effectiveModel, lastStreamStatus, ollamaStream.Failure.Kind, 0);
                        }
                        continue;
                    }

                    using StringContent reqContent = new(bodyText, Encoding.UTF8, "application/json");
                    using HttpRequestMessage upstreamReq = new(HttpMethod.Post, provider.Capabilities.ChatPath)
                    {
                        Content = reqContent,
                        Version = HttpVersion.Version11,
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact
                    };
                    upstreamReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                    var streamSw = System.Diagnostics.Stopwatch.StartNew();
                    using HttpResponseMessage upstreamResp = await provider.Client.SendAsync(
                        upstreamReq, HttpCompletionOption.ResponseHeadersRead, requestCt);
                    streamSw.Stop();

                    if (!upstreamResp.IsSuccessStatusCode)
                    {
                        string errBody = await upstreamResp.Content.ReadAsStringAsync(ct);
                        UpstreamFailure failure = UpstreamFailureClassifier.Classify(
                            (int)upstreamResp.StatusCode, CollectResponseHeaders(upstreamResp), errBody);

                        Console.WriteLine($"[OPENAI-ERROR-STREAM] Provider='{provider.Name}' Upstream='{upstreamModel}' HTTP={(int)upstreamResp.StatusCode} Kind={failure.Kind} RespBody='{(!string.IsNullOrEmpty(errBody) ? errBody[..Math.Min(errBody.Length, 500)] : "(empty)")}' Latency={streamSw.ElapsedMilliseconds}ms");
                        usageTracker.RecordError(provider.Name, $"HTTP {(int)upstreamResp.StatusCode}", ((int)upstreamResp.StatusCode).ToString(), streamSw.ElapsedMilliseconds);

                        lastStreamStatus = (int)upstreamResp.StatusCode;
                        lastStreamBody = errBody;
                        providerHealth.RecordFailure(provider.Name, effectiveModel, failure);

                        if (!UpstreamFailureClassifier.ShouldFailover(failure))
                            break;
                        if (i < candidates.Count - 1)
                        {
                            proxyLogger.LogFailover(provider.Name, effectiveModel, lastStreamStatus, streamSw.ElapsedMilliseconds);
                            providerHealth.RecordFailover(provider.Name, candidates[i + 1].Provider.Name,
                                effectiveModel, lastStreamStatus, failure.Kind, streamSw.ElapsedMilliseconds);
                        }
                        continue;
                    }

                    // Committed to this provider from here on — the next write puts bytes on the wire.
                    StampWinningProvider(ctx, provider, upstreamModel, i);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.Headers.CacheControl = "no-cache";
                    ctx.Response.Headers["X-Accel-Buffering"] = "no";

                    double streamCost = PricingCatalog.EstimateCostUsd(provider.Name, effectiveModel, 0, 0);
                    usageTracker.RecordRequest(provider.Name, 0, 0, 0, null, streamSw.ElapsedMilliseconds, streamCost);
                    var rlHeaders = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var h in upstreamResp.Headers)
                        rlHeaders[h.Key] = string.Join(", ", h.Value);
                    foreach (var h in upstreamResp.TrailingHeaders)
                        rlHeaders[h.Key] = string.Join(", ", h.Value);
                    usageTracker.RecordRateLimitHeaders(provider.Name, rlHeaders);
                    providerHealth.RecordSuccess(provider.Name, effectiveModel);

                    await chatStreaming.StreamAndCache(upstreamResp, ctx.Response, ct);
                    return;
                }
                catch (Exception ex) when (ProxyDiagnostics.IsRetryableTransportFailure(ex, ctx, ct))
                {
                    // Nothing has been written yet, so an unreachable or hung provider is just
                    // another reason to try the next candidate rather than to fail the request.
                    (lastStreamStatus, lastStreamBody) =
                        ProxyDiagnostics.DescribeTransportFailure(ex, provider, effectiveModel, out UpstreamFailure transportFailure);

                    Console.WriteLine($"[OPENAI-ERROR-STREAM] Provider='{provider.Name}' Upstream='{upstreamModel}' {ex.GetType().Name}: {ex.Message}");
                    usageTracker.RecordError(provider.Name, ex.GetType().Name, lastStreamStatus.ToString(), 0, transportFailure.Kind);
                    providerHealth.RecordFailure(provider.Name, effectiveModel, transportFailure);

                    if (i < candidates.Count - 1)
                    {
                        proxyLogger.LogFailover(provider.Name, effectiveModel, lastStreamStatus, 0);
                        providerHealth.RecordFailover(provider.Name, candidates[i + 1].Provider.Name,
                            effectiveModel, lastStreamStatus, transportFailure.Kind, 0);
                    }
                }
            }

            ctx.Response.StatusCode = lastStreamStatus > 0 ? lastStreamStatus : StatusCodes.Status502BadGateway;
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers["X-Proxy-Attempts"] = streamAttempts.ToString();
            await ctx.Response.WriteAsync(
                string.IsNullOrEmpty(lastStreamBody)
                    ? """{"error":"no provider candidate available","code":"NO_CANDIDATE"}"""
                    : lastStreamBody,
                ct);
        });

        return app;
    }

    /// <summary>
    /// Pins a request to a provider named as a <c>provider/model</c> prefix.
    /// </summary>
    /// <remarks>
    /// An explicit <c>@provider</c> suffix always wins. Without that guard,
    /// <c>openai/gpt-oss-120b@groq</c> — a real id, since Groq serves a model whose upstream name
    /// starts with <c>openai/</c> — was pinned to OpenAI, which rejected it as an invalid model.
    /// The suffix is the unambiguous form the proxy itself publishes in <c>/api/tags</c>, so a
    /// prefix that merely looks like a provider name must not override it. `ResolveModel` already
    /// applies the same precedence.
    /// </remarks>
    private static ProviderInfo? ExtractProviderHint(string? requestedModel, ProviderRegistry providerRegistry)
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
            return null;

        if (requestedModel.Contains('@'))
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

    /// <summary>The outcome of one Ollama-format candidate inside the failover loop.</summary>
    private readonly record struct OllamaCandidateResult(
        bool Handled, int StatusCode, string? ErrorBody, UpstreamFailure Failure);

    /// <summary>
    /// Runs one Ollama-format candidate for an OpenAI-surface request.
    ///
    /// It used to return a bare <c>bool</c>, throwing away the upstream status and body on
    /// failure. When such a provider was the only candidate, the caller was then left with no
    /// recorded response and answered <c>502 {"error":"no provider candidate available"}</c> —
    /// which was actively misleading, because a candidate did exist and did answer.
    /// </summary>
    private static async Task<OllamaCandidateResult> TryHandleOllamaCloudChatCompletion(
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
        {
            UpstreamFailure failure = UpstreamFailureClassifier.Classify(
                (int)response.StatusCode, CollectResponseHeaders(response), respBody);
            Console.WriteLine($"[OPENAI-ERROR] Provider='{provider.Name}' Upstream='{upstreamModel}' HTTP={(int)response.StatusCode} Kind={failure.Kind}");
            return new OllamaCandidateResult(false, (int)response.StatusCode, respBody, failure);
        }

        string openAiResponseBody = ConvertOllamaChatToOpenAiCompletion(respBody, effectiveModel);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(openAiResponseBody, clientCt);
        return new OllamaCandidateResult(true, 200, null, UpstreamFailure.Success);
    }

    private static Dictionary<string, string?> CollectResponseHeaders(HttpResponseMessage response) =>
        ProxyDiagnostics.CollectResponseHeaders(response);

    private static void StampWinningProvider(HttpContext ctx, ProviderInfo provider, string upstreamModel, int candidateIndex) =>
        ProxyDiagnostics.StampWinningProvider(ctx, provider, upstreamModel, candidateIndex);

    private static async Task<OllamaCandidateResult> HandleOllamaCloudChatCompletion(
        HttpContext ctx,
        ProviderInfo provider,
        string openAiRequestBody,
        string effectiveModel,
        string upstreamModel,
        bool isStream,
        int candidateIndex,
        CancellationToken requestCt,
        CancellationToken clientCt)
    {
        // Non-streaming: get full response and return as JSON
        if (!isStream)
        {
            string ollamaRequestBody = BuildOllamaChatRequest(openAiRequestBody, upstreamModel, isStream: false);

            using StringContent content = new(ollamaRequestBody, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await provider.Client.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, provider.Capabilities.ChatPath) { Content = content },
                requestCt);

            string respBody = await response.Content.ReadAsStringAsync(clientCt);
            if (!response.IsSuccessStatusCode)
            {
                return new OllamaCandidateResult(false, (int)response.StatusCode, respBody,
                    UpstreamFailureClassifier.Classify((int)response.StatusCode, CollectResponseHeaders(response), respBody));
            }

            string openAiResponseBody = ConvertOllamaChatToOpenAiCompletion(respBody, effectiveModel);
            StampWinningProvider(ctx, provider, upstreamModel, candidateIndex);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(openAiResponseBody, clientCt);
            return new OllamaCandidateResult(true, 200, null, UpstreamFailure.Success);
        }

        // Streaming: request NDJSON stream from Ollama Cloud and convert to SSE in real-time
        string streamRequestBody = BuildOllamaChatRequest(openAiRequestBody, upstreamModel, isStream: true);

        string chatcmplId = $"chatcmpl-{Guid.NewGuid():N}";
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using StringContent streamContent = new(streamRequestBody, Encoding.UTF8, "application/json");
        using HttpRequestMessage streamReq = new(HttpMethod.Post, provider.Capabilities.ChatPath) { Content = streamContent };
        using HttpResponseMessage streamResp = await provider.Client.SendAsync(
            streamReq, HttpCompletionOption.ResponseHeadersRead, requestCt);

        if (!streamResp.IsSuccessStatusCode)
        {
            string errBody = await streamResp.Content.ReadAsStringAsync(clientCt);
            return new OllamaCandidateResult(false, (int)streamResp.StatusCode, errBody,
                UpstreamFailureClassifier.Classify((int)streamResp.StatusCode, CollectResponseHeaders(streamResp), errBody));
        }

        // Only now is the response committed to this provider. The flush below used to run
        // BEFORE the upstream request was even sent, which started the response and made this
        // path impossible to fail over from — and did it with a sync-over-async wait on a
        // request thread.
        StampWinningProvider(ctx, provider, upstreamModel, candidateIndex);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        // Ensure the response is streamed, not buffered.
        await ctx.Response.Body.FlushAsync(clientCt);
        // Disable all response buffering in Kestrel
        var responseBodyFeature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
        responseBodyFeature?.DisableBuffering();

        // Read NDJSON stream line-by-line from Ollama Cloud and convert each chunk to OpenAI SSE format
        using Stream respStream = await streamResp.Content.ReadAsStreamAsync(clientCt);
        using StreamReader ndjsonReader = new(respStream);
        string? line;
        while ((line = await ndjsonReader.ReadLineAsync(clientCt)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using JsonDocument chunk = JsonDocument.Parse(line);
                JsonElement root = chunk.RootElement;

                // Extract content from Ollama message chunk.
                // Reasoning models (e.g. minimax-m3, kimi-k2.7-code) emit their thinking in
                // "message.thinking" first while leaving "message.content" empty. Fall back to
                // thinking when content is empty so VS 2026 BYOM sees a continuous stream.
                string? deltaContent = null;
                if (root.TryGetProperty("message", out JsonElement msg))
                {
                    bool hasContent = msg.TryGetProperty("content", out JsonElement contentEl) &&
                                      contentEl.ValueKind == JsonValueKind.String &&
                                      !string.IsNullOrWhiteSpace(contentEl.GetString());
                    if (hasContent)
                    {
                        deltaContent = contentEl.GetString();
                    }
                    else if (msg.TryGetProperty("thinking", out JsonElement thinkingEl) &&
                             thinkingEl.ValueKind == JsonValueKind.String)
                    {
                        deltaContent = thinkingEl.GetString();
                    }
                }

                bool isDone = root.TryGetProperty("done", out JsonElement doneEl) && doneEl.GetBoolean();

                // Skip chunks with empty/null content — VS 2026 BYOM doesn't handle them well
                if (!string.IsNullOrWhiteSpace(deltaContent))
                {
                    object sseChunk = new
                    {
                        id = chatcmplId,
                        @object = "chat.completion.chunk",
                        created,
                        model = effectiveModel,
                        choices = new[]
                        {
                            new
                            {
                                index = 0,
                                delta = new { role = "assistant", content = deltaContent },
                                finish_reason = (string?)null
                            }
                        }
                    };
                    await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(sseChunk, JsonDefaults.SnakeCase)}\n\n", clientCt);
                    await ctx.Response.Body.FlushAsync(clientCt);
                }

                if (isDone)
                {
                    object finishChunk = new
                    {
                        id = chatcmplId,
                        @object = "chat.completion.chunk",
                        created,
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
                    await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(finishChunk, JsonDefaults.SnakeCase)}\n\n", clientCt);
                    await ctx.Response.WriteAsync("data: [DONE]\n\n", clientCt);
                    await ctx.Response.Body.FlushAsync(clientCt);
                    return new OllamaCandidateResult(true, 200, null, UpstreamFailure.Success);
                }
            }
            catch { }
        }

        // If we exit the loop without a done signal, send finish anyway
        await ctx.Response.WriteAsync("data: [DONE]\n\n", clientCt);
        await ctx.Response.Body.FlushAsync(clientCt);
        return new OllamaCandidateResult(true, 200, null, UpstreamFailure.Success);
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
            writer.WriteStartArray();

            foreach (JsonElement msg in messages.EnumerateArray())
            {
                writer.WriteStartObject();

                bool hasMultiPartContent = false;
                List<string> imageUrls = [];

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

                foreach (JsonProperty mp in msg.EnumerateObject())
                {
                    if (mp.NameEquals("content") && hasMultiPartContent)
                        continue;
                    mp.WriteTo(writer);
                }

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

    private static void RecordUsageFromResponse(
        UsageTrackerService usageTracker,
        string providerName,
        string responseBody,
        HttpHeaders responseHeaders,
        HttpHeaders? trailingHeaders,
        long latencyMs = 0,
        string model = "")
    {
        Dictionary<string, string?> headers = new(StringComparer.OrdinalIgnoreCase);

        void CollectHeaders(HttpHeaders source)
        {
            foreach (var h in source)
            {
                headers[h.Key] = string.Join(", ", h.Value);
            }
        }

        CollectHeaders(responseHeaders);
        if (trailingHeaders is not null)
            CollectHeaders(trailingHeaders);

        long promptTokens = 0, completionTokens = 0, totalTokens = 0;
        bool hasUsage = false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("usage", out JsonElement usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out JsonElement pt) && pt.ValueKind == JsonValueKind.Number)
                {
                    promptTokens = pt.GetInt64();
                    hasUsage = true;
                }
                if (usage.TryGetProperty("completion_tokens", out JsonElement ct) && ct.ValueKind == JsonValueKind.Number)
                {
                    completionTokens = ct.GetInt64();
                    hasUsage = true;
                }
                if (usage.TryGetProperty("total_tokens", out JsonElement tt) && tt.ValueKind == JsonValueKind.Number)
                {
                    totalTokens = tt.GetInt64();
                    hasUsage = true;
                }
                if (totalTokens == 0 && promptTokens > 0 && completionTokens > 0)
                {
                    totalTokens = promptTokens + completionTokens;
                }
            }
        }
        catch { }

        double cost = hasUsage ? PricingCatalog.EstimateCostUsd(providerName, model, promptTokens, completionTokens) : 0;

        usageTracker.RecordRequest(providerName, promptTokens, completionTokens, totalTokens, headers, latencyMs, cost);
    }

    private static string ConvertOllamaChatToOpenAiCompletion(string ollamaResponseBody, string effectiveModel)
    {
        using JsonDocument ollamaDoc = JsonDocument.Parse(ollamaResponseBody);
        JsonElement root = ollamaDoc.RootElement;
        JsonElement message = root.TryGetProperty("message", out JsonElement msg) ? msg : default;

        string content = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out JsonElement contentElement)
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;

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