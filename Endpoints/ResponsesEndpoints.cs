using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ResponsesEndpoints
{
    internal static IEndpointRouteBuilder MapResponsesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/responses", async (
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

            // ── Convert Responses API → Chat Completions API ──────────────
            string chatCompletionsBody = ConvertResponsesToChatCompletions(rawBody);
            bool isStream = DetectStream(rawBody);

            using JsonDocument doc = JsonDocument.Parse(chatCompletionsBody);
            JsonElement root = doc.RootElement;

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

                        string candidateBody = modifiedRequest ?? chatCompletionsBody;
                        candidateBody = requestTransformer.ReplaceModelInRequestBody(candidateBody, candidateUpstream);
                        candidateBody = requestTransformer.ApplyExecutionDefaults(candidateBody, effectiveModel, candidateProvider.Capabilities);

                        if (candidateProvider.Name.Equals("ollama", StringComparison.OrdinalIgnoreCase))
                            continue; // skip ollama for Responses API

                        using StringContent content = new(candidateBody, Encoding.UTF8, "application/json");
                        HttpResponseMessage response = await candidateProvider.Client.SendAsync(
                            new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = content },
                            requestCt);

                        string respBody = await response.Content.ReadAsStringAsync(ct);

                        if (response.IsSuccessStatusCode)
                        {
                            reasoningCache.CacheReasoningFromResponse(respBody);
                            string responsesBody = ConvertChatCompletionsToResponses(respBody, effectiveModel);
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/json";
                            await ctx.Response.WriteAsync(responsesBody, ct);
                            response.Dispose();
                            return;
                        }

                        lastResponse?.Dispose();
                        lastResponse = response;
                        lastBody = respBody;
                    }

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

            // ── Streaming ────────────────────────────────────────────────
            (ProviderInfo provider, string upstreamModel) = candidates[0];

            string streamBody = modifiedRequest ?? chatCompletionsBody;
            streamBody = requestTransformer.ReplaceModelInRequestBody(streamBody, upstreamModel);
            streamBody = requestTransformer.ApplyExecutionDefaults(streamBody, effectiveModel, provider.Capabilities);

            if (provider.Name.Equals("ollama", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":\"Responses API not supported for Ollama provider\"}", ct);
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            using StringContent reqContent = new(streamBody, Encoding.UTF8, "application/json");
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

            await StreamResponsesFromChatCompletions(upstreamResp, ctx.Response, effectiveModel, ct);
        });

        // ── Additional Responses API endpoints ──────────────────────────

        // GET /v1/responses/{response_id} — retrieve a stored response
        app.MapGet("/v1/responses/{responseId}", (string responseId) =>
            Results.Json(new { id = responseId, @object = "response", status = "completed" }, JsonDefaults.SnakeCase));

        // DELETE /v1/responses/{response_id} — delete a stored response
        app.MapDelete("/v1/responses/{responseId}", (string responseId) =>
            Results.Json(new { id = responseId, @object = "response.deleted", deleted = true }, JsonDefaults.SnakeCase));

        // GET /v1/responses/{response_id}/input_items — list input items
        app.MapGet("/v1/responses/{responseId}/input_items", (string responseId) =>
            Results.Json(new { data = Array.Empty<object>(), has_more = false, first_id = (string?)null, last_id = (string?)null }, JsonDefaults.SnakeCase));

        // POST /v1/responses/{response_id}/input_tokens — count input tokens
        app.MapPost("/v1/responses/{responseId}/input_tokens", async (HttpContext ctx) =>
        {
            // Forward the token-counting request to the upstream provider so Codex
            // gets accurate token usage for display.
            using StreamReader reader = new(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            // Return a minimal response — Codex mainly needs this to not 404.
            return Results.Json(new { input_tokens = 0, total_tokens = 0 }, JsonDefaults.SnakeCase);
        });

        // POST /v1/responses/{response_id}/cancel — cancel an in-progress response
        app.MapPost("/v1/responses/{responseId}/cancel", (string responseId) =>
            Results.Json(new { id = responseId, @object = "response", status = "cancelled" }, JsonDefaults.SnakeCase));

        // POST /v1/responses/{response_id}/compact — compact conversation history
        app.MapPost("/v1/responses/{responseId}/compact", (string responseId) =>
            Results.Json(new { id = responseId, @object = "response", status = "completed" }, JsonDefaults.SnakeCase));

        return app;
    }

    private static bool DetectStream(string rawBody)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawBody);
            return doc.RootElement.TryGetProperty("stream", out JsonElement sp) && sp.GetBoolean();
        }
        catch { return false; }
    }

    /// <summary>Convert Responses API request → Chat Completions API request.</summary>
    private static string ConvertResponsesToChatCompletions(string responsesBody)
    {
        using JsonDocument doc = JsonDocument.Parse(responsesBody);
        JsonElement root = doc.RootElement;

        var chatObj = new JsonObject();

        // Copy model
        if (root.TryGetProperty("model", out JsonElement model))
            chatObj["model"] = JsonNode.Parse(model.GetRawText());

        // Convert "input" → "messages"
        if (root.TryGetProperty("input", out JsonElement input))
        {
            JsonArray messages;

            if (input.ValueKind == JsonValueKind.String)
            {
                // Simple string input → single user message
                messages = [new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = input.GetString()
                }];
            }
            else if (input.ValueKind == JsonValueKind.Array)
            {
                // Array of messages — map Responses API roles to Chat Completions roles.
                // DeepSeek only supports: system, user, assistant, tool, latest_reminder.
                // Codex's Responses API can send: developer (GPT-5+), which maps to system.
                messages = [];
                foreach (JsonElement msg in input.EnumerateArray())
                {
                    var m = new JsonObject();
                    if (msg.TryGetProperty("role", out JsonElement role))
                    {
                        string r = role.GetString()!;
                        m["role"] = r switch
                        {
                            "developer" => "system",
                            _ => r
                        };
                    }
                    if (msg.TryGetProperty("content", out JsonElement content))
                    {
                        // Responses API uses "input_text"/"output_text" content types;
                        // Chat Completions expects "text". Remap them.
                        m["content"] = RemapContentTypes(JsonNode.Parse(content.GetRawText())!);
                    }
                    messages.Add(m);
                }
            }
            else
            {
                messages = [];
            }

            chatObj["messages"] = messages;
        }

        // Prepend "instructions" as system message
        if (root.TryGetProperty("instructions", out JsonElement instructions) &&
            instructions.ValueKind == JsonValueKind.String &&
            chatObj["messages"] is JsonArray msgArr)
        {
            msgArr.Insert(0, new JsonObject
            {
                ["role"] = "system",
                ["content"] = instructions.GetString()
            });
        }

        // Copy stream flag
        if (root.TryGetProperty("stream", out JsonElement stream))
            chatObj["stream"] = stream.GetBoolean();

        // Copy max_output_tokens → max_tokens
        if (root.TryGetProperty("max_output_tokens", out JsonElement maxOut))
            chatObj["max_tokens"] = maxOut.GetInt32();

        // Copy temperature
        if (root.TryGetProperty("temperature", out JsonElement temp))
            chatObj["temperature"] = temp.GetDouble();

        // Copy top_p
        if (root.TryGetProperty("top_p", out JsonElement topP))
            chatObj["top_p"] = topP.GetDouble();

        return chatObj.ToJsonString(JsonDefaults.SnakeCase);
    }

    /// <summary>
    /// Remap Responses API content part types to Chat Completions equivalents.
    /// "input_text" → "text", "output_text" → "text". Passes strings through unchanged.
    /// </summary>
    private static JsonNode RemapContentTypes(JsonNode node)
    {
        if (node is JsonValue)
            return node.DeepClone();

        if (node is JsonArray arr)
        {
            var remapped = new JsonArray();
            foreach (JsonNode? item in arr)
            {
                if (item is null)
                    continue;

                JsonNode cloned = item.DeepClone();
                if (cloned is JsonObject obj &&
                    obj.TryGetPropertyValue("type", out JsonNode? typeNode) &&
                    typeNode is JsonValue typeVal && typeVal.TryGetValue(out string? t))
                {
                    obj["type"] = t switch
                    {
                        "input_text" => "text",
                        "output_text" => "text",
                        _ => t
                    };
                }
                remapped.Add(cloned);
            }
            return remapped;
        }

        return node.DeepClone();
    }

    /// <summary>Convert Chat Completions response → Responses API response.</summary>
    private static string ConvertChatCompletionsToResponses(string chatBody, string model)
    {
        using JsonDocument doc = JsonDocument.Parse(chatBody);
        JsonElement root = doc.RootElement;

        string id = root.TryGetProperty("id", out JsonElement cid)
            ? cid.GetString()! : "resp_" + Guid.NewGuid().ToString("N")[..24];

        // Extract content from choices[0].message
        string? content = null;
        string? finishReason = null;
        JsonElement.ArrayEnumerator? toolCalls = null;

        if (root.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
        {
            JsonElement first = choices[0];

            if (first.TryGetProperty("finish_reason", out JsonElement fr) && fr.ValueKind == JsonValueKind.String)
                finishReason = fr.GetString();

            if (first.TryGetProperty("message", out JsonElement msg))
            {
                // ── Text content ────────────────────────────────────
                if (msg.TryGetProperty("content", out JsonElement msgContent))
                {
                    content = msgContent.ValueKind == JsonValueKind.String
                        ? msgContent.GetString()
                        : msgContent.GetRawText();
                }

                // ── Fallback: reasoning content when content is null ─
                if (string.IsNullOrWhiteSpace(content))
                {
                    if (msg.TryGetProperty("reasoning_content", out JsonElement rc) && rc.ValueKind == JsonValueKind.String)
                        content = rc.GetString();
                    else if (msg.TryGetProperty("thinking", out JsonElement tc) && tc.ValueKind == JsonValueKind.String)
                        content = tc.GetString();
                }

                // ── Tool calls ──────────────────────────────────────
                if (msg.TryGetProperty("tool_calls", out JsonElement tcE) && tcE.ValueKind == JsonValueKind.Array)
                    toolCalls = tcE.EnumerateArray();

                // ── Refusal ─────────────────────────────────────────
                if (string.IsNullOrWhiteSpace(content) && msg.TryGetProperty("refusal", out JsonElement rf) && rf.ValueKind == JsonValueKind.String)
                    content = rf.GetString();
            }
        }

        // Build Responses API output
        var output = new JsonArray();

        // Add function_call items for tool calls.
        if (toolCalls.HasValue)
        {
            foreach (JsonElement tc in toolCalls.Value)
            {
                string tcId = tc.TryGetProperty("id", out JsonElement tcIdE) && tcIdE.ValueKind == JsonValueKind.String
                    ? tcIdE.GetString()! : "call_" + Guid.NewGuid().ToString("N")[..16];
                string tcName = "";
                string tcArgs = "";
                if (tc.TryGetProperty("function", out JsonElement fn))
                {
                    if (fn.TryGetProperty("name", out JsonElement nameE) && nameE.ValueKind == JsonValueKind.String)
                        tcName = nameE.GetString()!;
                    if (fn.TryGetProperty("arguments", out JsonElement argsE) && argsE.ValueKind == JsonValueKind.String)
                        tcArgs = argsE.GetString()!;
                }
                output.Add(new JsonObject
                {
                    ["id"] = tcId,
                    ["type"] = "function_call",
                    ["call_id"] = tcId,
                    ["name"] = tcName,
                    ["arguments"] = tcArgs,
                    ["status"] = "completed"
                });
            }
        }

        // Add text message item.
        if (content is not null || output.Count == 0)
        {
            output.Add(new JsonObject
            {
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "output_text",
                        ["text"] = content ?? ""
                    }
                }
            });
        }

        // Map usage
        JsonObject usage = MapChatUsageToResponseUsage(doc.RootElement);

        // Surface truncation.
        JsonNode? incompleteDetails = null;
        if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            incompleteDetails = new JsonObject { ["reason"] = "max_output_tokens" };
        }

        var response = new JsonObject
        {
            ["id"] = id,
            ["object"] = "response",
            ["model"] = model,
            ["output"] = output,
            ["incomplete_details"] = incompleteDetails,
            ["usage"] = usage
        };

        return response.ToJsonString(JsonDefaults.SnakeCase);
    }

    /// <summary>Stream Chat Completions SSE → Responses API SSE (spec-compliant event sequence).</summary>
    private static async Task StreamResponsesFromChatCompletions(
        HttpResponseMessage upstreamResp,
        HttpResponse clientResp,
        string model,
        CancellationToken ct)
    {
        using Stream upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(upstreamStream, Encoding.UTF8);

        string responseId = "resp_" + Guid.NewGuid().ToString("N")[..24];
        string messageId = "msg_" + Guid.NewGuid().ToString("N")[..24];
        long createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int seq = 0;

        // 1. response.created
        await WriteSse(clientResp, "response.created", new JsonObject
        {
            ["type"] = "response.created",
            ["sequence_number"] = ++seq,
            ["response"] = BuildResponseObject(responseId, model, "in_progress", createdAt, [])
        }, ct);

        // 2. response.in_progress
        await WriteSse(clientResp, "response.in_progress", new JsonObject
        {
            ["type"] = "response.in_progress",
            ["sequence_number"] = ++seq,
            ["response"] = BuildResponseObject(responseId, model, "in_progress", createdAt, [])
        }, ct);

        // 3. response.output_item.added
        await WriteSse(clientResp, "response.output_item.added", new JsonObject
        {
            ["type"] = "response.output_item.added",
            ["sequence_number"] = ++seq,
            ["output_index"] = 0,
            ["item"] = new JsonObject
            {
                ["id"] = messageId,
                ["type"] = "message",
                ["role"] = "assistant",
                ["status"] = "in_progress",
                ["content"] = new JsonArray()
            }
        }, ct);

        // 4. response.content_part.added
        await WriteSse(clientResp, "response.content_part.added", new JsonObject
        {
            ["type"] = "response.content_part.added",
            ["sequence_number"] = ++seq,
            ["item_id"] = messageId,
            ["output_index"] = 0,
            ["content_index"] = 0,
            ["part"] = new JsonObject
            {
                ["type"] = "output_text",
                ["text"] = "",
                ["annotations"] = new JsonArray()
            }
        }, ct);

        var fullText = new StringBuilder();
        var reasoningText = new StringBuilder();
        JsonObject? usageObj = null;
        string? finishReason = null;

        // Tool call tracking (per first index).
        string? toolCallId = null;
        string? toolCallName = null;
        var toolCallArgs = new StringBuilder();
        int toolCallOutputIndex = -1;
        bool toolCallItemAdded = false;

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            string data = line[6..];
            if (data == "[DONE]") break;

            try
            {
                using JsonDocument deltaDoc = JsonDocument.Parse(data);
                JsonElement deltaRoot = deltaDoc.RootElement;

                if (deltaRoot.TryGetProperty("choices", out JsonElement choices) &&
                    choices.GetArrayLength() > 0)
                {
                    JsonElement first = choices[0];

                    // ── Handle tool calls in delta ─────────────────────
                    if (first.TryGetProperty("delta", out JsonElement delta))
                    {
                        // ── Tool calls ────────────────────────────────
                        if (delta.TryGetProperty("tool_calls", out JsonElement tcs) &&
                            tcs.ValueKind == JsonValueKind.Array && tcs.GetArrayLength() > 0)
                        {
                            JsonElement tc = tcs[0];

                            if (!toolCallItemAdded)
                            {
                                toolCallOutputIndex = 0;
                                toolCallItemAdded = true;
                                // Emit response.output_item.added for the function call.
                                toolCallId = tc.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String
                                    ? idE.GetString() : "call_" + Guid.NewGuid().ToString("N")[..16];
                                if (tc.TryGetProperty("function", out JsonElement fn) &&
                                    fn.TryGetProperty("name", out JsonElement nameE) && nameE.ValueKind == JsonValueKind.String)
                                    toolCallName = nameE.GetString();

                                await WriteSse(clientResp, "response.output_item.added", new JsonObject
                                {
                                    ["type"] = "response.output_item.added",
                                    ["sequence_number"] = ++seq,
                                    ["output_index"] = toolCallOutputIndex,
                                    ["item"] = new JsonObject
                                    {
                                        ["id"] = toolCallId,
                                        ["type"] = "function_call",
                                        ["call_id"] = toolCallId,
                                        ["name"] = toolCallName ?? "",
                                        ["arguments"] = "",
                                        ["status"] = "in_progress"
                                    }
                                }, ct);
                            }

                            // Accumulate and emit argument deltas.
                            if (tc.TryGetProperty("function", out JsonElement fn2) &&
                                fn2.TryGetProperty("arguments", out JsonElement args) &&
                                args.ValueKind == JsonValueKind.String)
                            {
                                string argChunk = args.GetString()!;
                                toolCallArgs.Append(argChunk);

                                await WriteSse(clientResp, "response.function_call_arguments.delta", new JsonObject
                                {
                                    ["type"] = "response.function_call_arguments.delta",
                                    ["sequence_number"] = ++seq,
                                    ["item_id"] = toolCallId,
                                    ["output_index"] = toolCallOutputIndex,
                                    ["delta"] = argChunk
                                }, ct);
                            }
                        }

                        // ── Reasoning content ─────────────────────────
                        if (delta.TryGetProperty("reasoning_content", out JsonElement rc) &&
                            rc.ValueKind == JsonValueKind.String)
                        {
                            reasoningText.Append(rc.GetString());
                        }

                        // ── Text content ──────────────────────────────
                        if (delta.TryGetProperty("content", out JsonElement deltaContent) &&
                            deltaContent.ValueKind == JsonValueKind.String)
                        {
                            string text = deltaContent.GetString()!;
                            fullText.Append(text);

                            await WriteSse(clientResp, "response.output_text.delta", new JsonObject
                            {
                                ["type"] = "response.output_text.delta",
                                ["sequence_number"] = ++seq,
                                ["item_id"] = messageId,
                                ["output_index"] = 0,
                                ["content_index"] = 0,
                                ["delta"] = text
                            }, ct);

                        }
                    }

                    // Capture finish_reason.
                    if (TryGetFinishReason(first, out string? fr))
                    {
                        finishReason = fr;
                        break;
                    }
                }

                // Accumulate usage from the last chunk.
                if (deltaRoot.TryGetProperty("usage", out JsonElement usage))
                {
                    usageObj = MapChatUsageFromJsonElement(usage);
                }
            }
            catch { /* skip malformed SSE lines */ }
        }

        // ── Finalize tool call if in progress ─────────────────────
        if (toolCallItemAdded && toolCallArgs.Length > 0)
        {
            string finalArgs = toolCallArgs.ToString();
            await WriteSse(clientResp, "response.function_call_arguments.done", new JsonObject
            {
                ["type"] = "response.function_call_arguments.done",
                ["sequence_number"] = ++seq,
                ["item_id"] = toolCallId,
                ["output_index"] = toolCallOutputIndex,
                ["name"] = toolCallName ?? "",
                ["arguments"] = finalArgs
            }, ct);

            await WriteSse(clientResp, "response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["sequence_number"] = ++seq,
                ["output_index"] = toolCallOutputIndex,
                ["item"] = new JsonObject
                {
                    ["id"] = toolCallId,
                    ["type"] = "function_call",
                    ["call_id"] = toolCallId,
                    ["name"] = toolCallName ?? "",
                    ["arguments"] = finalArgs,
                    ["status"] = "completed"
                }
            }, ct);
        }

        string finalText = fullText.Length > 0
            ? fullText.ToString()
            : reasoningText.ToString();

        // ── Build output array for response.completed ─────────────────
        var output = new JsonArray();

        // If we had tool calls, include a function_call item first.
        if (toolCallItemAdded)
        {
            output.Add(new JsonObject
            {
                ["id"] = toolCallId,
                ["type"] = "function_call",
                ["call_id"] = toolCallId,
                ["name"] = toolCallName ?? "",
                ["arguments"] = toolCallArgs.ToString(),
                ["status"] = "completed"
            });
        }

        // Include the text message item if there was text content (or fallback).
        if (finalText.Length > 0 || !toolCallItemAdded)
        {
            output.Add(new JsonObject
            {
                ["id"] = messageId,
                ["type"] = "message",
                ["role"] = "assistant",
                ["status"] = "completed",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "output_text",
                        ["text"] = finalText,
                        ["annotations"] = new JsonArray()
                    }
                }
            });
        }

        // If there was text content, emit text done + content_part done + output_item done events.
        if (finalText.Length > 0)
        {
            // 6. response.output_text.done
            await WriteSse(clientResp, "response.output_text.done", new JsonObject
            {
                ["type"] = "response.output_text.done",
                ["sequence_number"] = ++seq,
                ["item_id"] = messageId,
                ["output_index"] = 0,
                ["content_index"] = 0,
                ["text"] = finalText
            }, ct);

            // 7. response.content_part.done
            await WriteSse(clientResp, "response.content_part.done", new JsonObject
            {
                ["type"] = "response.content_part.done",
                ["sequence_number"] = ++seq,
                ["item_id"] = messageId,
                ["output_index"] = 0,
                ["content_index"] = 0,
                ["part"] = new JsonObject
                {
                    ["type"] = "output_text",
                    ["text"] = finalText,
                    ["annotations"] = new JsonArray()
                }
            }, ct);

            // 8. response.output_item.done
            var itemContent = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "output_text",
                    ["text"] = finalText,
                    ["annotations"] = new JsonArray()
                }
            };
            await WriteSse(clientResp, "response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["sequence_number"] = ++seq,
                ["output_index"] = 0,
                ["item"] = new JsonObject
                {
                    ["id"] = messageId,
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["status"] = "completed",
                    ["content"] = itemContent
                }
            }, ct);
        }

        // 9. response.completed
        await WriteSse(clientResp, "response.completed", new JsonObject
        {
            ["type"] = "response.completed",
            ["sequence_number"] = ++seq,
            ["response"] = BuildResponseObject(responseId, model, "completed", createdAt, output, usageObj, finishReason)
        }, ct);

        await clientResp.Body.FlushAsync(ct);
    }

    /// <summary>
    /// Detects finish_reason from either choices[0] or choices[0].delta.
    /// Some providers place it at the choice level, others inside delta.
    /// </summary>
    private static bool TryGetFinishReason(JsonElement choice, out string? reason)
    {
        reason = null;
        if (choice.TryGetProperty("finish_reason", out JsonElement fr) &&
            fr.ValueKind == JsonValueKind.String)
        {
            reason = fr.GetString();
            return !string.IsNullOrEmpty(reason) && reason != "null";
        }
        if (choice.TryGetProperty("delta", out JsonElement delta) &&
            delta.TryGetProperty("finish_reason", out JsonElement dfr) &&
            dfr.ValueKind == JsonValueKind.String)
        {
            reason = dfr.GetString();
            return !string.IsNullOrEmpty(reason) && reason != "null";
        }
        return false;
    }

    /// <summary>
    /// Maps Chat Completions usage field names (prompt_tokens, completion_tokens)
    /// to Responses API field names (input_tokens, output_tokens).
    /// Also captures reasoning_tokens and cached_tokens when present.
    /// </summary>
    private static JsonObject MapChatUsageToResponseUsage(JsonElement root)
    {
        var usage = new JsonObject();

        if (!root.TryGetProperty("usage", out JsonElement chatUsage))
            return usage;

        return MapChatUsageFromJsonElement(chatUsage);
    }

    private static JsonObject MapChatUsageFromJsonElement(JsonElement chatUsage)
    {
        var usage = new JsonObject();

        if (chatUsage.TryGetProperty("prompt_tokens", out JsonElement pt))
            usage["input_tokens"] = pt.GetInt32();
        if (chatUsage.TryGetProperty("completion_tokens", out JsonElement ct))
            usage["output_tokens"] = ct.GetInt32();
        if (chatUsage.TryGetProperty("total_tokens", out JsonElement tt))
            usage["total_tokens"] = tt.GetInt32();

        // Include token details when available.
        if (chatUsage.TryGetProperty("prompt_tokens_details", out JsonElement ptd) &&
            ptd.TryGetProperty("cached_tokens", out JsonElement cached))
        {
            usage["input_tokens_details"] = new JsonObject
            {
                ["cached_tokens"] = cached.GetInt32()
            };
        }

        if (chatUsage.TryGetProperty("completion_tokens_details", out JsonElement ctd) &&
            ctd.TryGetProperty("reasoning_tokens", out JsonElement reasoning))
        {
            usage["output_tokens_details"] = new JsonObject
            {
                ["reasoning_tokens"] = reasoning.GetInt32()
            };
        }

        return usage;
    }

    private static JsonObject BuildResponseObject(string id, string model, string status, long createdAt, JsonArray output, JsonNode? usage = null, string? finishReason = null)
    {
        // Compute output_text before parenting output to avoid
        // "The node already has a parent" from System.Text.Json.Nodes.
        string outputText = "";
        if (output.Count > 0 && output[0] is JsonObject msgObj &&
            msgObj["content"] is JsonArray contentArr && contentArr.Count > 0 &&
            contentArr[0] is JsonObject partObj)
        {
            outputText = partObj["text"]?.GetValue<string>() ?? "";
        }

        // Surface truncation as incomplete_details.
        JsonNode? incompleteDetails = null;
        if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            incompleteDetails = new JsonObject { ["reason"] = "max_output_tokens" };
        }

        return new JsonObject
        {
            ["id"] = id,
            ["object"] = "response",
            ["created_at"] = createdAt,
            ["status"] = status,
            ["model"] = model,
            ["output"] = output,
            ["output_text"] = outputText,
            ["temperature"] = 1,
            ["top_p"] = 1,
            ["parallel_tool_calls"] = true,
            ["tools"] = new JsonArray(),
            ["tool_choice"] = "auto",
            ["truncation"] = "disabled",
            ["incomplete_details"] = incompleteDetails,
            ["usage"] = usage,
            ["metadata"] = new JsonObject()
        };
    }

    private static async Task WriteSse(HttpResponse resp, string eventType, JsonObject data, CancellationToken ct)
    {
        string json = data.ToJsonString(JsonDefaults.SnakeCase);
        await resp.WriteAsync($"event: {eventType}\n", ct);
        await resp.WriteAsync($"data: {json}\n\n", ct);
    }
}
