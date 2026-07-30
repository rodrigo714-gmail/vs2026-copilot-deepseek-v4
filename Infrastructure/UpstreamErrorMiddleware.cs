using System.Text.Json;

/// <summary>
/// Turns upstream transport failures into responses a client can act on.
/// Without this, an unreachable provider host or an expired per-model timeout surfaces
/// as an empty HTTP 500, which Visual Studio 2026 BYOM reports as a generic
/// "the model failed to respond" with no indication of which provider broke.
/// </summary>
internal static class UpstreamErrorMiddleware
{
    internal static IApplicationBuilder UseUpstreamErrorHandling(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next(ctx);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // The client hung up — nothing to report, and nothing to write to.
                if (ctx.RequestAborted.IsCancellationRequested)
                    return;

                // Streaming responses have already flushed headers; the connection is the error.
                if (ctx.Response.HasStarted)
                {
                    Console.WriteLine($"[UPSTREAM] {ex.GetType().Name} after response started: {ex.Message}");
                    return;
                }

                (int status, string code, string message) = ex switch
                {
                    HttpRequestException http =>
                        (StatusCodes.Status502BadGateway, "UPSTREAM_UNREACHABLE",
                         $"Could not reach the upstream provider: {http.Message}"),
                    _ =>
                        (StatusCodes.Status504GatewayTimeout, "UPSTREAM_TIMEOUT",
                         "The upstream provider did not respond within the model's configured timeout_seconds."),
                };

                string provider = ctx.Response.Headers.TryGetValue("X-Proxy-Provider", out var p) ? p.ToString() : "unknown";
                string model = ctx.Response.Headers.TryGetValue("X-Proxy-Resolved-Model", out var m) ? m.ToString() : "unknown";
                Console.WriteLine($"[UPSTREAM] {code} provider='{provider}' model='{model}': {ex.Message}");

                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    error = message,
                    code,
                    provider,
                    model
                }, JsonDefaults.SnakeCase));
            }
        });

        return app;
    }
}
