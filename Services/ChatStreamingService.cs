using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal sealed class ChatStreamingService
{
    private readonly ReasoningCacheService _reasoningCacheService;
    private readonly UsageTracker _usageTracker;
    private readonly ProxyLogger _logger;

    public ChatStreamingService(ReasoningCacheService reasoningCacheService, UsageTracker usageTracker, ProxyLogger logger)
    {
        _reasoningCacheService = reasoningCacheService;
        _usageTracker = usageTracker;
        _logger = logger;
    }

    internal async Task StreamAndCache(HttpResponseMessage upstream, HttpResponse downstream, CancellationToken ct)
    {
        int promptTokens = 0, completionTokens = 0;
        try
        {
            using Stream upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
            using StreamReader reader = new(upstreamStream);
            await using StreamWriter writer = new(downstream.Body, leaveOpen: true) { NewLine = "\n" };

            StringBuilder sb = new(4096);
            List<string>? tcIds = null;
            bool hasTc = false;
            bool receivedData = false;

            while (true)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct); }
                catch (HttpIOException) when (receivedData) { break; }
                if (line == null) break;
                receivedData = true;

                if (line.StartsWith("data:"))
                {
                    string json = line.Substring(5).TrimStart();
                    if (json.Length > 0 && json != "[DONE]")
                    {
                        using JsonDocument chunk = JsonDocument.Parse(json);
                        JsonElement cr = chunk.RootElement;
                        if (cr.TryGetProperty("usage", out JsonElement ue) && ue.ValueKind == JsonValueKind.Object)
                        {
                            if (ue.TryGetProperty("prompt_tokens", out JsonElement pt) && pt.ValueKind == JsonValueKind.Number)
                                promptTokens = pt.GetInt32();
                            if (ue.TryGetProperty("completion_tokens", out JsonElement ct2) && ct2.ValueKind == JsonValueKind.Number)
                                completionTokens = ct2.GetInt32();
                        }
                        if (cr.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
                        {
                            JsonElement delta = choices[0].TryGetProperty("delta", out JsonElement d) ? d
                                : choices[0].TryGetProperty("message", out JsonElement mm) ? mm : default;
                            if (delta.ValueKind != JsonValueKind.Undefined)
                            {
                                if (delta.TryGetProperty("reasoning_content", out JsonElement rc) && rc.ValueKind == JsonValueKind.String)
                                { string? rct = rc.GetString(); if (!string.IsNullOrEmpty(rct)) sb.Append(rct); }
                                if (delta.TryGetProperty("tool_calls", out JsonElement tcs) && tcs.ValueKind == JsonValueKind.Array)
                                {
                                    hasTc = true;
                                    foreach (JsonElement tc in tcs.EnumerateArray())
                                        if (tc.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String)
                                        { tcIds ??= []; string id = idE.GetString()!; if (!tcIds.Contains(id)) tcIds.Add(id); }
                                }
                            }
                            if (choices[0].TryGetProperty("finish_reason", out JsonElement fr) && fr.ValueKind != JsonValueKind.Null)
                            {
                                string reasoning = sb.ToString();
                                if (!string.IsNullOrEmpty(reasoning))
                                {
                                    string key = hasTc && tcIds != null && tcIds.Count > 0
                                        ? "toolcall:" + string.Join("|", tcIds)
                                        : _reasoningCacheService.NextAssistantKey();
                                    _reasoningCacheService.Set(key, reasoning);
                                }
                            }
                        }
                    }
                }
                await writer.WriteLineAsync(line);
                await writer.FlushAsync(ct);
            }
        }
        catch (HttpIOException) { }

        if (promptTokens > 0 || completionTokens > 0)
            _usageTracker.RecordSuccess("stream", "unknown", 0, promptTokens, completionTokens);
    }


    internal async Task StreamNdjsonPassthrough(HttpResponseMessage upstream, HttpResponse downstream, CancellationToken ct)
    {
        using Stream upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(upstreamStream);
        await using StreamWriter writer = new(downstream.Body, leaveOpen: true) { NewLine = "\n" };
        while (true)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct); }
            catch (HttpIOException) { break; }
            if (line == null) break;
            await writer.WriteLineAsync(line);
            await writer.FlushAsync(ct);
        }
    }

    /// <summary>
    /// Converts an upstream OpenAI SSE stream into the Ollama NDJSON stream that
    /// <c>/api/chat</c> clients expect (Visual Studio 2026 BYOM, Ollama CLI, ollama-js).
    /// Each <c>data: {chat.completion.chunk}</c> becomes one
    /// <c>{"model":…,"message":{"role":"assistant","content":…},"done":false}</c> line,
    /// followed by a single terminating <c>"done":true</c> line carrying token counts.
    /// Forwarding the SSE verbatim — which is what this used to do — makes every
    /// OpenAI-format provider look silent to an Ollama client.
    /// </summary>
    internal async Task StreamOllamaAndCache(HttpResponseMessage upstream, HttpResponse downstream, string model, CancellationToken ct)
    {
        int promptTokens = 0, completionTokens = 0;
        StringBuilder reasoning = new(1024);
        SortedDictionary<int, ToolCallBuffer> toolCalls = [];
        bool emittedContent = false;
        string doneReason = "stop";
        long startTimestamp = Stopwatch.GetTimestamp();

        try
        {
            using Stream upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
            using StreamReader reader = new(upstreamStream);
            await using StreamWriter writer = new(downstream.Body, leaveOpen: true) { NewLine = "\n" };

            while (true)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct); }
                catch (HttpIOException) { break; }
                if (line is null) break;
                if (line.Length == 0) continue;

                string json = line.StartsWith("data:", StringComparison.Ordinal) ? line[5..].Trim() : line.Trim();
                if (json.Length == 0 || json == "[DONE]" || json[0] != '{') continue;

                string? contentDelta = null;
                string? reasoningDelta = null;

                try
                {
                    using JsonDocument chunk = JsonDocument.Parse(json);
                    JsonElement root = chunk.RootElement;

                    if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        if (usage.TryGetProperty("prompt_tokens", out JsonElement pt) && pt.ValueKind == JsonValueKind.Number)
                            promptTokens = pt.GetInt32();
                        if (usage.TryGetProperty("completion_tokens", out JsonElement cpt) && cpt.ValueKind == JsonValueKind.Number)
                            completionTokens = cpt.GetInt32();
                    }

                    if (!root.TryGetProperty("choices", out JsonElement choices)
                        || choices.ValueKind != JsonValueKind.Array
                        || choices.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    JsonElement choice = choices[0];
                    JsonElement delta = choice.TryGetProperty("delta", out JsonElement d) ? d
                        : choice.TryGetProperty("message", out JsonElement m) ? m
                        : default;

                    if (delta.ValueKind == JsonValueKind.Object)
                    {
                        if (delta.TryGetProperty("content", out JsonElement contentEl) && contentEl.ValueKind == JsonValueKind.String)
                            contentDelta = contentEl.GetString();

                        if (delta.TryGetProperty("reasoning_content", out JsonElement rcEl) && rcEl.ValueKind == JsonValueKind.String)
                            reasoningDelta = rcEl.GetString();

                        if (delta.TryGetProperty("tool_calls", out JsonElement tcs) && tcs.ValueKind == JsonValueKind.Array)
                            AccumulateToolCalls(tcs, toolCalls);
                    }

                    if (choice.TryGetProperty("finish_reason", out JsonElement fr) && fr.ValueKind == JsonValueKind.String)
                        doneReason = fr.GetString() ?? "stop";
                }
                catch (JsonException)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(reasoningDelta))
                    reasoning.Append(reasoningDelta);

                // Emit a chunk when there is anything for the client to render.
                if (!string.IsNullOrEmpty(contentDelta) || !string.IsNullOrEmpty(reasoningDelta))
                {
                    if (!string.IsNullOrEmpty(contentDelta))
                        emittedContent = true;

                    await writer.WriteLineAsync(BuildOllamaChunk(model, contentDelta, reasoningDelta));
                    await writer.FlushAsync(ct);
                }
            }

            // Reasoning-only responses (DeepSeek and friends can spend the whole budget
            // thinking) would otherwise reach the client as a blank answer.
            string reasoningText = reasoning.ToString();
            if (!emittedContent && reasoningText.Length > 0)
            {
                await writer.WriteLineAsync(BuildOllamaChunk(model, reasoningText, thinking: null));
                await writer.FlushAsync(ct);
            }

            if (reasoningText.Length > 0)
            {
                string key = toolCalls.Count > 0
                    ? "toolcall:" + string.Join("|", toolCalls.Values.Select(t => t.Id).Where(id => !string.IsNullOrEmpty(id)))
                    : _reasoningCacheService.NextAssistantKey();
                _reasoningCacheService.Set(key, reasoningText);
            }

            await writer.WriteLineAsync(BuildOllamaDone(
                model, doneReason, toolCalls, promptTokens, completionTokens,
                Stopwatch.GetElapsedTime(startTimestamp)));
            await writer.FlushAsync(ct);
        }
        catch (HttpIOException) { }

        if (promptTokens > 0 || completionTokens > 0)
            _usageTracker.RecordSuccess("ollama", model, 0, promptTokens, completionTokens);
    }

    /// <summary>OpenAI streams tool calls as fragments; Ollama expects them whole, at the end.</summary>
    private sealed class ToolCallBuffer
    {
        internal string Id = "";
        internal string Name = "";
        internal readonly StringBuilder Arguments = new();
    }

    private static void AccumulateToolCalls(JsonElement toolCallDeltas, SortedDictionary<int, ToolCallBuffer> buffers)
    {
        int fallbackIndex = buffers.Count;
        foreach (JsonElement tc in toolCallDeltas.EnumerateArray())
        {
            int index = tc.TryGetProperty("index", out JsonElement idxE) && idxE.ValueKind == JsonValueKind.Number
                ? idxE.GetInt32()
                : fallbackIndex;

            if (!buffers.TryGetValue(index, out ToolCallBuffer? buffer))
            {
                buffer = new ToolCallBuffer();
                buffers[index] = buffer;
            }

            if (tc.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String)
                buffer.Id = idE.GetString() ?? buffer.Id;

            if (!tc.TryGetProperty("function", out JsonElement fn) || fn.ValueKind != JsonValueKind.Object)
                continue;

            if (fn.TryGetProperty("name", out JsonElement nameE) && nameE.ValueKind == JsonValueKind.String)
            {
                string? name = nameE.GetString();
                if (!string.IsNullOrEmpty(name))
                    buffer.Name = name;
            }

            if (fn.TryGetProperty("arguments", out JsonElement argsE) && argsE.ValueKind == JsonValueKind.String)
                buffer.Arguments.Append(argsE.GetString());
        }
    }

    private static string BuildOllamaChunk(string model, string? content, string? thinking)
    {
        using MemoryStream ms = new(256);
        using (Utf8JsonWriter w = new(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteString("created_at", DateTime.UtcNow.ToString("o"));
            w.WritePropertyName("message");
            w.WriteStartObject();
            w.WriteString("role", "assistant");
            w.WriteString("content", content ?? "");
            if (!string.IsNullOrEmpty(thinking))
                w.WriteString("thinking", thinking);
            w.WriteEndObject();
            w.WriteBoolean("done", false);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildOllamaDone(
        string model,
        string doneReason,
        SortedDictionary<int, ToolCallBuffer> toolCalls,
        int promptTokens,
        int completionTokens,
        TimeSpan elapsed)
    {
        using MemoryStream ms = new(512);
        using (Utf8JsonWriter w = new(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteString("created_at", DateTime.UtcNow.ToString("o"));
            w.WritePropertyName("message");
            w.WriteStartObject();
            w.WriteString("role", "assistant");
            w.WriteString("content", "");

            if (toolCalls.Count > 0)
            {
                w.WritePropertyName("tool_calls");
                w.WriteStartArray();
                foreach (ToolCallBuffer tc in toolCalls.Values)
                {
                    if (string.IsNullOrEmpty(tc.Name))
                        continue;

                    w.WriteStartObject();
                    w.WritePropertyName("function");
                    w.WriteStartObject();
                    w.WriteString("name", tc.Name);
                    w.WritePropertyName("arguments");
                    WriteToolArguments(w, tc.Arguments.ToString());
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            w.WriteEndObject();
            w.WriteBoolean("done", true);
            w.WriteString("done_reason", toolCalls.Count > 0 ? "tool_calls" : doneReason);
            w.WriteNumber("total_duration", (long)(elapsed.TotalMilliseconds * 1_000_000));
            w.WriteNumber("prompt_eval_count", promptTokens);
            w.WriteNumber("eval_count", completionTokens);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>OpenAI sends tool arguments as a JSON string; Ollama expects a JSON object.</summary>
    private static void WriteToolArguments(Utf8JsonWriter writer, string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(arguments);
            parsed.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(arguments);
        }
    }
}
