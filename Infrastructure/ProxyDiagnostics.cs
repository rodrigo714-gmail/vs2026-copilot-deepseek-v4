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
}
