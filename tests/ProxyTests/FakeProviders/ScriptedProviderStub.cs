using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace ProxyTests.FakeProviders;

/// <summary>One canned upstream reply.</summary>
internal sealed record ScriptedResponse(int StatusCode, string? Body, IReadOnlyList<(string Key, string Value)>? Headers = null)
{
    /// <summary>Reply normally — the stub renders the right shape for the request (JSON or SSE).</summary>
    internal static readonly ScriptedResponse Ok = new(200, null);
}

/// <summary>
/// An in-process upstream provider whose chat responses a test can script exactly.
///
/// <see cref="ProxyFixture"/> cannot express failover scenarios: it boots a single stub that
/// always answers 200, and <see cref="FakeProviderHandler"/> only serves model catalogs, never
/// <c>/v1/chat/completions</c>. This stub fills that gap — it records how many chat attempts it
/// received (so a test can assert how many candidates were burned) and replays a queue of
/// scripted replies, falling back to success once the queue is empty.
/// </summary>
internal sealed class ScriptedProviderStub : IAsyncDisposable
{
    private const string CompletionJson = """
        {
          "id": "scripted", "object": "chat.completion", "created": 1700000000,
          "model": "scripted-model",
          "choices": [{"index":0,"message":{"role":"assistant","content":"HELLO_FROM_{NAME}"},"finish_reason":"stop"}],
          "usage": {"prompt_tokens":7,"completion_tokens":3,"total_tokens":10}
        }
        """;

    private static readonly string SseStream =
        "data: {\"id\":\"scripted\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"scripted-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"HELLO_FROM_{NAME}\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"scripted\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"scripted-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<ScriptedResponse> _script = new();
    private int _chatAttempts;
    private int _broken;

    /// <summary>Label baked into successful replies, so a test can tell the stubs apart.</summary>
    internal string Name { get; }

    /// <summary>Base URL to point a PROVIDER_*_BASE_URL at.</summary>
    internal string Url { get; }

    /// <summary>How many chat requests this stub has received since the last <see cref="Reset"/>.</summary>
    internal int ChatAttempts => Volatile.Read(ref _chatAttempts);

    internal ScriptedProviderStub(string name, string[] models)
    {
        Name = name;

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();

        string modelsBody = BuildModelsBody(models);
        _app.MapGet("/v1/models", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(modelsBody);
        });

        _app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
        {
            Interlocked.Increment(ref _chatAttempts);

            // Simulates an unreachable provider: resetting the connection surfaces to the proxy's
            // HttpClient as an HttpRequestException, the same shape as a refused connection, a DNS
            // failure or a dropped upstream.
            if (Volatile.Read(ref _broken) != 0)
            {
                ctx.Abort();
                return;
            }

            using StreamReader reader = new(ctx.Request.Body);
            string requestBody = await reader.ReadToEndAsync();
            bool wantsStream = requestBody.Contains("\"stream\":true") || requestBody.Contains("\"stream\": true");

            ScriptedResponse reply = _script.TryDequeue(out ScriptedResponse? next) ? next : ScriptedResponse.Ok;

            if (reply.Headers is not null)
            {
                foreach ((string key, string value) in reply.Headers)
                    ctx.Response.Headers[key] = value;
            }

            ctx.Response.StatusCode = reply.StatusCode;

            if (reply.StatusCode is >= 200 and < 300 && reply.Body is null)
            {
                ctx.Response.ContentType = wantsStream ? "text/event-stream" : "application/json";
                await ctx.Response.WriteAsync(
                    (wantsStream ? SseStream : CompletionJson).Replace("{NAME}", Name));
                return;
            }

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(reply.Body ?? "");
        });

        _app.StartAsync().GetAwaiter().GetResult();
        Url = _app.Urls.First();
    }

    /// <summary>Queues a failing reply. Repeat calls to fail more than one attempt.</summary>
    internal ScriptedProviderStub Fail(int statusCode, string body, params (string Key, string Value)[] headers)
    {
        _script.Enqueue(new ScriptedResponse(statusCode, body, headers.Length > 0 ? headers : null));
        return this;
    }

    /// <summary>Queues <paramref name="times"/> failing replies.</summary>
    internal ScriptedProviderStub FailTimes(int times, int statusCode, string body)
    {
        for (int i = 0; i < times; i++) Fail(statusCode, body);
        return this;
    }

    /// <summary>
    /// Makes every chat request reset the connection until the returned handle is disposed, so a
    /// test can simulate a provider that is unreachable rather than merely erroring.
    /// </summary>
    internal IDisposable Break()
    {
        Volatile.Write(ref _broken, 1);
        return new Restore(this);
    }

    private sealed class Restore(ScriptedProviderStub stub) : IDisposable
    {
        public void Dispose() => Volatile.Write(ref stub._broken, 0);
    }

    /// <summary>Clears the script, the attempt counter and any simulated outage.</summary>
    internal void Reset()
    {
        while (_script.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _chatAttempts, 0);
        Volatile.Write(ref _broken, 0);
    }

    private static string BuildModelsBody(string[] models)
    {
        IEnumerable<string> entries = models.Select(m =>
            $$"""{"id":{{System.Text.Json.JsonSerializer.Serialize(m)}},"object":"model","created":1700000000,"owned_by":"scripted"}""");
        return $$"""{"object":"list","data":[{{string.Join(",", entries)}}]}""";
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
