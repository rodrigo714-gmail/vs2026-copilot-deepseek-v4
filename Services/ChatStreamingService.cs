using System.Text;
using System.Text.Json;

internal sealed class ChatStreamingService
{
    private readonly ReasoningCacheService _reasoningCacheService;

    public ChatStreamingService(ReasoningCacheService reasoningCacheService)
    {
        _reasoningCacheService = reasoningCacheService;
    }

    internal async Task StreamAndCache(HttpResponseMessage upstream, HttpResponse downstream, CancellationToken ct)
    {
        try
        {
            using Stream upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
            using StreamReader reader = new(upstreamStream);
            await using StreamWriter writer = new(downstream.Body, leaveOpen: true) { NewLine = "\n" };

            StringBuilder sb = new(4096);
            List<string>? tcIds = null;
            bool hasTc = false;
            string? assistantKey = null;
            bool receivedData = false;

            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (HttpIOException) when (receivedData)
                {
                    // Upstream closed the connection after we already sent data.
                    // This is a normal end of stream — break gracefully.
                    break;
                }

                if (line == null) break;
                receivedData = true;

                if (line.StartsWith("data:"))
                {
                    string json = line.Substring(5).TrimStart();
                    if (json.Length > 0 && json != "[DONE]")
                    {
                        try
                        {
                            using JsonDocument chunk = JsonDocument.Parse(json);
                            JsonElement cr = chunk.RootElement;
                            if (cr.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
                            {
                                JsonElement delta = choices[0].TryGetProperty("delta", out JsonElement d) ? d
                                    : choices[0].TryGetProperty("message", out JsonElement mm) ? mm : default;

                                if (delta.ValueKind != JsonValueKind.Undefined)
                                {
                                    if (delta.TryGetProperty("reasoning_content", out JsonElement rc) && rc.ValueKind == JsonValueKind.String)
                                    {
                                        string? rct = rc.GetString();
                                        if (!string.IsNullOrEmpty(rct)) sb.Append(rct);
                                    }

                                    if (delta.TryGetProperty("tool_calls", out JsonElement tcs) && tcs.ValueKind == JsonValueKind.Array)
                                    {
                                        hasTc = true;
                                        foreach (JsonElement tc in tcs.EnumerateArray())
                                        {
                                            if (tc.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String)
                                            {
                                                tcIds ??= [];
                                                string id = idE.GetString()!;
                                                if (!tcIds.Contains(id)) tcIds.Add(id);
                                            }
                                        }
                                    }

                                    if (choices[0].TryGetProperty("finish_reason", out JsonElement fr) && fr.ValueKind != JsonValueKind.Null)
                                    {
                                        string reasoning = sb.ToString();
                                        if (!string.IsNullOrEmpty(reasoning))
                                        {
                                            string key;
                                            if (hasTc && tcIds != null && tcIds.Count > 0)
                                                key = $"toolcall:{string.Join("|", tcIds)}";
                                            else
                                                key = assistantKey ??= _reasoningCacheService.NextAssistantKey();

                                            _reasoningCacheService.Set(key, reasoning);
                                        }
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // parse errors are non-critical
                        }

                        await writer.WriteAsync("data: ");
                        await writer.WriteAsync(json);
                        await writer.WriteLineAsync();
                    }
                    else
                    {
                        await writer.WriteLineAsync(line);
                    }
                }
                else
                {
                    await writer.WriteLineAsync(line);
                }

                await writer.FlushAsync(ct);
            }
        }
        catch (HttpIOException)
        {
            // Upstream connection dropped before any data was received.
            // The 200 header was already sent to the client, so we cannot switch
            // to a JSON error response. Simply end the stream — the client will
            // see an incomplete response and will retry automatically.
        }
    }

    internal async Task StreamNdjsonPassthrough(HttpResponseMessage upstream, HttpResponse downstream, CancellationToken ct)
    {
        using Stream upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(upstreamStream);
        await using StreamWriter writer = new(downstream.Body, leaveOpen: true) { NewLine = "\n" };

        while (true)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line == null)
            {
                break;
            }

            await writer.WriteLineAsync(line);
            await writer.FlushAsync(ct);
        }
    }

    internal async Task StreamOllamaAndCache(HttpResponseMessage upstream, HttpResponse downstream, string model, CancellationToken ct)
    {
        using Stream upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(upstreamStream);
        await using StreamWriter writer = new(downstream.Body, leaveOpen: true) { NewLine = "\n" };

        StringBuilder reasoningSb = new(4096);
        List<string>? tcIds = null;
        bool hasTc = false;
        string finishReason = "stop";

        while (true)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (!line.StartsWith("data:")) continue;

            string json = line.Substring(5).TrimStart();
            if (json.Length == 0 || json == "[DONE]") continue;

            string? contentDelta = null;
            JsonElement? toolCallsDelta = null;
            try
            {
                using JsonDocument chunk = JsonDocument.Parse(json);
                JsonElement cr = chunk.RootElement;
                if (cr.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
                {
                    JsonElement choice0 = choices[0];
                    JsonElement delta = choice0.TryGetProperty("delta", out JsonElement d) ? d
                        : choice0.TryGetProperty("message", out JsonElement mm) ? mm : default;

                    if (delta.ValueKind != JsonValueKind.Undefined)
                    {
                        if (delta.TryGetProperty("content", out JsonElement ce) && ce.ValueKind == JsonValueKind.String)
                            contentDelta = ce.GetString();

                        if (delta.TryGetProperty("reasoning_content", out JsonElement rc) && rc.ValueKind == JsonValueKind.String)
                        {
                            string? rct = rc.GetString();
                            if (!string.IsNullOrEmpty(rct)) reasoningSb.Append(rct);
                        }

                        if (delta.TryGetProperty("tool_calls", out JsonElement tcs) && tcs.ValueKind == JsonValueKind.Array)
                        {
                            hasTc = true;
                            toolCallsDelta = tcs.Clone();
                            foreach (JsonElement tc in tcs.EnumerateArray())
                            {
                                if (tc.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String)
                                {
                                    tcIds ??= [];
                                    string id = idE.GetString()!;
                                    if (!tcIds.Contains(id)) tcIds.Add(id);
                                }
                            }
                        }
                    }

                    if (choice0.TryGetProperty("finish_reason", out JsonElement fr) && fr.ValueKind == JsonValueKind.String)
                    {
                        finishReason = fr.GetString() ?? "stop";
                        string reasoning = reasoningSb.ToString();
                        if (!string.IsNullOrEmpty(reasoning))
                        {
                            string key = hasTc && tcIds != null && tcIds.Count > 0
                                ? $"toolcall:{string.Join("|", tcIds)}"
                                : _reasoningCacheService.NextAssistantKey();
                            _reasoningCacheService.Set(key, reasoning);
                        }
                    }
                }
            }
            catch
            {
                continue;
            }

            if (contentDelta == null && toolCallsDelta == null) continue;

            Dictionary<string, object?> message = new()
            {
                ["role"] = "assistant",
                ["content"] = contentDelta ?? ""
            };
            if (toolCallsDelta != null)
                message["tool_calls"] = toolCallsDelta.Value;

            Dictionary<string, object?> ndjson = new()
            {
                ["model"] = model,
                ["created_at"] = DateTime.UtcNow.ToString("o"),
                ["message"] = message,
                ["done"] = false
            };

            await writer.WriteAsync(JsonSerializer.Serialize(ndjson, JsonDefaults.SnakeCase));
            await writer.WriteLineAsync();
            await writer.FlushAsync(ct);
        }

        Dictionary<string, object?> final = new()
        {
            ["model"] = model,
            ["created_at"] = DateTime.UtcNow.ToString("o"),
            ["message"] = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = "" },
            ["done_reason"] = finishReason,
            ["done"] = true
        };
        await writer.WriteAsync(JsonSerializer.Serialize(final, JsonDefaults.SnakeCase));
        await writer.WriteLineAsync();
        await writer.FlushAsync(ct);
    }
}
