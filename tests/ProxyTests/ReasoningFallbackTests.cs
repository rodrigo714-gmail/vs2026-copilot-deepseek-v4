using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ProxyTests;

/// <summary>
/// DeepSeek-style providers put reasoning in <c>reasoning_content</c>; Cerebras, Groq and
/// OpenRouter name the field <c>reasoning</c>. A model that spends its whole token budget
/// thinking returns empty <c>content</c>, and the proxy must fall back to the reasoning text
/// for either field name — otherwise the BYOM client renders a blank reply.
/// </summary>
[Collection("Failover")]
public class ReasoningFallbackTests(FailoverFixture fixture)
{
    private static StringContent OllamaBody(bool stream) => new(
        $$"""{"model":"{{FailoverFixture.SharedModel}}","messages":[{"role":"user","content":"hi"}],"stream":{{(stream ? "true" : "false")}}}""",
        Encoding.UTF8, "application/json");

    /// <summary>Learns which stub is candidate 0 so the scripted reply lands on the provider that serves.</summary>
    private async Task<FakeProviders.ScriptedProviderStub> PrimaryStubAsync()
    {
        await fixture.ResetAsync();
        using HttpResponseMessage probe = await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: false));
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        string primary = probe.Headers.GetValues("X-Proxy-Provider").Single();
        return fixture.StubNamed(primary);
    }

    [Theory]
    [InlineData("reasoning_content")]
    [InlineData("reasoning")]
    public async Task ApiChat_NonStreaming_ReasoningOnlyAnswer_FallsBackToContent(string field)
    {
        FakeProviders.ScriptedProviderStub primary = await PrimaryStubAsync();
        primary.Fail(200, $$$"""
            {"id":"r1","object":"chat.completion","created":1700000000,"model":"scripted-model",
             "choices":[{"index":0,"message":{"role":"assistant","content":"","{{{field}}}":"REASONING_ONLY_ANSWER"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":5,"completion_tokens":9,"total_tokens":14}}
            """);

        using HttpResponseMessage r = await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: false));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal("REASONING_ONLY_ANSWER", d.RootElement.GetProperty("message").GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("reasoning_content")]
    [InlineData("reasoning")]
    public async Task ApiChat_Streaming_ReasoningOnlyAnswer_FallsBackToContent(string field)
    {
        FakeProviders.ScriptedProviderStub primary = await PrimaryStubAsync();
        primary.Fail(200,
            $"data: {{\"id\":\"r1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"scripted-model\",\"choices\":[{{\"index\":0,\"delta\":{{\"role\":\"assistant\",\"{field}\":\"THINK \"}},\"finish_reason\":null}}]}}\n\n" +
            $"data: {{\"id\":\"r1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"scripted-model\",\"choices\":[{{\"index\":0,\"delta\":{{\"{field}\":\"HARDER\"}},\"finish_reason\":null}}]}}\n\n" +
            "data: {\"id\":\"r1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"scripted-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n");

        using HttpResponseMessage r = await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: true));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string body = await r.Content.ReadAsStringAsync();

        string content = "";
        bool doneTrue = false;
        foreach (string line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.False(line.TrimStart().StartsWith("data:", StringComparison.Ordinal),
                "/api/chat must emit Ollama NDJSON, never SSE data: frames");
            using JsonDocument d = JsonDocument.Parse(line);
            if (d.RootElement.TryGetProperty("message", out JsonElement msg)
                && msg.TryGetProperty("content", out JsonElement c)
                && c.ValueKind == JsonValueKind.String)
            {
                content += c.GetString();
            }
            if (d.RootElement.TryGetProperty("done", out JsonElement done) && done.ValueKind == JsonValueKind.True)
                doneTrue = true;
        }

        Assert.True(doneTrue, "stream must terminate with done:true");
        Assert.Contains("THINK HARDER", content);
    }
}
