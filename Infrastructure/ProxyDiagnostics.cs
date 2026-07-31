using Microsoft.AspNetCore.Http;

/// <summary>
/// Shared helpers for the diagnostic response headers and for reading upstream headers.
///
/// Deliberately in one place: both endpoint surfaces need them, and the last time this codebase
/// grew a second copy of a small per-provider helper the two drifted apart unnoticed.
/// </summary>
internal static class ProxyDiagnostics
{
    /// <summary>
    /// Names the provider that actually served the request. Called immediately before the
    /// response body is written, so a request that failed over never advertises the candidate
    /// that failed.
    /// </summary>
    internal static void StampWinningProvider(HttpContext ctx, ProviderInfo provider, string upstreamModel, int candidateIndex)
    {
        ctx.Response.Headers["X-Proxy-Provider"] = provider.Name;
        ctx.Response.Headers["X-Proxy-Upstream-Model"] = upstreamModel;
        ctx.Response.Headers["X-Proxy-Candidate-Index"] = candidateIndex.ToString();
    }

    /// <summary>
    /// Flattens response and content headers into one case-insensitive map for
    /// <see cref="UpstreamFailureClassifier"/>. <c>Retry-After</c> belongs on the response, but
    /// some providers put their reset hints on the content headers instead, so both are read.
    /// </summary>
    internal static Dictionary<string, string?> CollectResponseHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in response.Headers)
            headers[h.Key] = string.Join(",", h.Value);
        foreach (var h in response.Content.Headers)
            headers[h.Key] = string.Join(",", h.Value);
        return headers;
    }

    internal static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value[..Math.Min(value.Length, max)];

    /// <summary>
    /// Whether an exception thrown while talking to a provider can be answered by trying the next
    /// candidate.
    ///
    /// Two cases must NOT be retried. If the client hung up there is nobody left to answer, and if
    /// the response has already started bytes are on the wire and committed to that provider.
    /// Everything else — connection refused, DNS failure, TLS failure, or nothing back within the
    /// model's <c>timeout_seconds</c> — is exactly the "this provider is unavailable" case that
    /// failover exists for.
    /// </summary>
    internal static bool IsRetryableTransportFailure(Exception ex, HttpContext ctx, CancellationToken clientCt) =>
        ex is HttpRequestException or OperationCanceledException
        && !clientCt.IsCancellationRequested
        && !ctx.Response.HasStarted;

    /// <summary>
    /// Turns a transport exception into a failure the candidate loop can carry, with an error body
    /// in the same shape <see cref="UpstreamErrorMiddleware"/> produces so a client sees one
    /// consistent format whether the walk ended here or there.
    /// </summary>
    internal static (int StatusCode, string Body) DescribeTransportFailure(Exception ex, ProviderInfo provider, string model, out UpstreamFailure failure)
    {
        bool unreachable = ex is HttpRequestException;
        failure = unreachable ? UpstreamFailure.Unreachable : UpstreamFailure.TimedOut;

        (int status, string code, string message) = unreachable
            ? (StatusCodes.Status502BadGateway, "UPSTREAM_UNREACHABLE",
               $"Could not reach the upstream provider: {ex.Message}")
            : (StatusCodes.Status504GatewayTimeout, "UPSTREAM_TIMEOUT",
               "The upstream provider did not respond within the model's configured timeout_seconds.");

        string body = System.Text.Json.JsonSerializer.Serialize(new
        {
            error = message,
            code,
            provider = provider.Name,
            model
        }, JsonDefaults.SnakeCase);

        return (status, body);
    }
}
