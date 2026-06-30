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

    internal async Task StreamOllamaAndCache(HttpResponseMessage upstream, HttpResponse downstream, string model, CancellationToken ct)
    {
        int promptTokens = 0, completionTokens = 0;
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
                if (line == null) break;
                if (line.TrimStart().StartsWith("{"))
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("usage", out JsonElement ue) && ue.ValueKind == JsonValueKind.Object)
                        {
                            if (ue.TryGetProperty("prompt_tokens", out JsonElement pt) && pt.ValueKind == JsonValueKind.Number) promptTokens = pt.GetInt32();
                            if (ue.TryGetProperty("completion_tokens", out JsonElement ct2) && ct2.ValueKind == JsonValueKind.Number) completionTokens = ct2.GetInt32();
                        }
                    }
                    catch { }
                }
                await writer.WriteLineAsync(line);
                await writer.FlushAsync(ct);
            }
        }
        catch (HttpIOException) { }
        if (promptTokens > 0 || completionTokens > 0)
            _usageTracker.RecordSuccess("ollama", model, 0, promptTokens, completionTokens);
    }
}
