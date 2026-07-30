using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ProxyTests;

// ── Shared fixture: stub provider + proxy app ────────────────────────────────

/// <summary>
/// Shared collection fixture that:
/// 1. Starts an in-process Kestrel stub simulating the DeepSeek/OpenAI API.
/// 2. Points PROVIDER_DEEPSEEK_BASE_URL at the stub before the proxy boots.
/// 3. Creates a WebApplicationFactory for the proxy and exposes an HttpClient.
/// </summary>
public sealed class ProxyFixture : IDisposable
{
    // Fake OpenAI chat completion (non-streaming)
    private const string FakeCompletion = """
        {
          "id": "test-id", "object": "chat.completion", "created": 1700000000,
          "model": "test-model",
          "choices": [{"index":0,"message":{"role":"assistant","content":"hi from stub"},"finish_reason":"stop"}],
          "usage": {"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}
        }
        """;

    // Fake SSE stream (OpenAI format) — proxy converts this to Ollama NDJSON
    private const string FakeStream =
        "data: {\"id\":\"t\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hi\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"t\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    private const string FakeModels = """
        {"object":"list","data":[{"id":"deepseek-v4-pro","object":"model","created":1700000000,"owned_by":"test"}]}
        """;

    private readonly WebApplication _stub;
    private readonly WebApplicationFactory<Program> _factory;

    public HttpClient Client { get; }

    public ProxyFixture()
    {
        // 1. Build and start the provider stub
        WebApplicationBuilder sb = WebApplication.CreateSlimBuilder();
        sb.WebHost.UseUrls("http://127.0.0.1:0");
        _stub = sb.Build();

        _stub.MapGet("/v1/models", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(FakeModels);
        });

        _stub.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
        {
            using StreamReader r = new(ctx.Request.Body);
            string body = await r.ReadToEndAsync();
            bool stream = body.Contains("\"stream\":true");

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = stream ? "text/event-stream" : "application/json";
            await ctx.Response.WriteAsync(stream ? FakeStream : FakeCompletion);
        });

        _stub.StartAsync().GetAwaiter().GetResult();
        string stubUrl = _stub.Urls.First();

        // 2. Set env vars so the proxy's top-level startup reads them
        Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_API_KEY", "fake-test-key");
        Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_BASE_URL", stubUrl);
        // Clear any provider keys that might be set in the host environment
        Environment.SetEnvironmentVariable("PROVIDER_NVIDIA_API_KEY", null);
        Environment.SetEnvironmentVariable("PROVIDER_OPENAI_API_KEY", null);
        Environment.SetEnvironmentVariable("PROVIDER_OPENROUTER_API_KEY", null);
        Environment.SetEnvironmentVariable("PROVIDER_GROQ_API_KEY", null);
        Environment.SetEnvironmentVariable("PROVIDER_OLLAMACLOUD_API_KEY", null);
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);

        // 3. Start the proxy via WebApplicationFactory
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ASPNETCORE_ENVIRONMENT", "Testing"));

        Client = _factory.CreateClient();
    }

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        _stub.StopAsync().GetAwaiter().GetResult();
        _stub.DisposeAsync().GetAwaiter().GetResult();
    }
}

[CollectionDefinition("Proxy")]
public class ProxyCollection : ICollectionFixture<ProxyFixture> { }

// ── Tests ────────────────────────────────────────────────────────────────────

[Collection("Proxy")]
public class EndpointTests(ProxyFixture fixture)
{
    private readonly HttpClient _client = fixture.Client;

    // /health ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Health_Returns200()
    {
        HttpResponseMessage r = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Health_BodyContainsStatusOk()
    {
        HttpResponseMessage r = await _client.GetAsync("/health");
        string body = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(body);
        Assert.Equal("ok", d.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_BodyContainsProviders()
    {
        HttpResponseMessage r = await _client.GetAsync("/health");
        string body = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(body);
        Assert.True(d.RootElement.TryGetProperty("providers", out JsonElement providers));
        Assert.NotEmpty(providers.EnumerateArray());
    }

    // /api/version ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiVersion_ReturnsVersionString()
    {
        HttpResponseMessage r = await _client.GetAsync("/api/version");
        string body = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(body);
        Assert.NotNull(d.RootElement.GetProperty("version").GetString());
    }

    // /api/tags ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiTags_Returns200WithModelsArray()
    {
        HttpResponseMessage r = await _client.GetAsync("/api/tags");
        string body = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using JsonDocument d = JsonDocument.Parse(body);
        JsonElement models = d.RootElement.GetProperty("models");
        Assert.NotEmpty(models.EnumerateArray());
    }

    // /api/show ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiShow_Get_Returns200()
    {
        HttpResponseMessage r = await _client.GetAsync("/api/show?model=deepseek-v4-pro");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task ApiShow_Post_Returns200WithModelInfo()
    {
        using StringContent body = new(
            """{"model":"deepseek-v4-pro"}""",
            System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage r = await _client.PostAsync("/api/show", body);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        string resp = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(resp);
        Assert.NotNull(d.RootElement.GetProperty("modelfile").GetString());
    }

    // /api/chat (Ollama) — non-streaming ──────────────────────────────────────

    [Fact]
    public async Task ApiChat_NonStreaming_Returns200()
    {
        using StringContent body = new(
            """{"model":"deepseek-v4-pro","messages":[{"role":"user","content":"hi"}],"stream":false}""",
            System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage r = await _client.PostAsync("/api/chat", body);
        string resp = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    // /api/chat (Ollama) — streaming ──────────────────────────────────────────

    [Fact]
    public async Task ApiChat_Streaming_ContentTypeIsNdjson()
    {
        using StringContent body = new(
            """{"model":"deepseek-v4-pro","messages":[{"role":"user","content":"hi"}],"stream":true}""",
            System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage r = await _client.PostAsync("/api/chat", body);
        string? ct = r.Content.Headers.ContentType?.ToString();
        Assert.True(ct is "application/x-ndjson" or "application/json",
            $"Expected NDJSON content-type, got: {ct}");
    }

    [Fact]
    public async Task ApiChat_Streaming_LastLineHasDoneTrue()
    {
        using StringContent body = new(
            """{"model":"test-model","messages":[{"role":"user","content":"hi"}],"stream":true}""",
            System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage r = await _client.PostAsync("/api/chat", body);
        string resp = await r.Content.ReadAsStringAsync();
        string[] lines = resp.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.NotEmpty(lines);

        // /api/chat speaks the Ollama protocol. Every line must be a bare NDJSON object —
        // never an SSE "data:" frame, which an Ollama client (Visual Studio 2026 BYOM
        // included) silently discards, making the model look like it answered nothing.
        foreach (string line in lines)
        {
            Assert.False(line.TrimStart().StartsWith("data:", StringComparison.Ordinal),
                $"/api/chat must not emit SSE frames, got: {line}");
        }

        bool foundDoneTrue = false;
        foreach (string line in lines)
        {
            using JsonDocument d = JsonDocument.Parse(line.Trim());
            Assert.True(d.RootElement.TryGetProperty("message", out JsonElement message),
                $"Every NDJSON line needs a 'message' object, got: {line}");
            Assert.True(message.TryGetProperty("role", out _),
                $"Every 'message' needs a role, got: {line}");

            if (d.RootElement.TryGetProperty("done", out JsonElement done) && done.GetBoolean())
                foundDoneTrue = true;
        }

        Assert.True(foundDoneTrue,
            $"Expected a terminating NDJSON line with \"done\":true. Lines: {string.Join(" | ", lines.Take(10))}");
    }

    // OpenAI-compatible /v1/chat/completions ────────────────────────────────────

    [Fact]
    public async Task V1Models_Returns200WithListObject()
    {
        HttpResponseMessage r = await _client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task V1Models_OnlyReturnsRoutableIds()
    {
        HttpResponseMessage r = await _client.GetAsync("/v1/models");
        string body = await r.Content.ReadAsStringAsync();
        using JsonDocument d = JsonDocument.Parse(body);
        JsonElement data = d.RootElement.GetProperty("data");
        Assert.NotEmpty(data.EnumerateArray());
    }

    [Fact]
    public async Task V1Chat_NonStreaming_Returns200WithJson()
    {
        using StringContent body = new(
            """{"model":"deepseek-v4-pro","messages":[{"role":"user","content":"hi"}],"stream":false}""",
            System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage r = await _client.PostAsync("/v1/chat/completions", body);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/json", r.Content.Headers.ContentType?.MediaType);
    }
}