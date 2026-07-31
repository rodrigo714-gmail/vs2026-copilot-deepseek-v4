internal static class ProxyAuthenticationMiddleware
{
    /// <summary>
    /// Paths a browser must be able to reach without an <c>Authorization</c> header, because a
    /// browser cannot send one when you type a URL.
    ///
    /// Only the page shell and its assets are exempt. The data endpoints — <c>/api/usage</c>,
    /// <c>/api/billing</c>, <c>/api/free-tier</c>, <c>/api/resilience</c> — stay protected, since
    /// they expose spend, quota and key metadata. The page reads the key from <c>?key=</c> or
    /// <c>localStorage</c> and sends it as a bearer token on its own fetches.
    /// </summary>
    private static readonly string[] PublicPathPrefixes =
    [
        "/dashboard",
        "/vendor/",
        "/health"
    ];

    internal static IApplicationBuilder UseOptionalProxyAuthentication(this IApplicationBuilder app, string? proxyApiKey)
    {
        if (string.IsNullOrEmpty(proxyApiKey))
        {
            return app;
        }

        // Opt-out for anyone who would rather the dashboard were behind the token too.
        bool dashboardPublic = !string.Equals(
            Environment.GetEnvironmentVariable("PROXY_DASHBOARD_PUBLIC"), "false", StringComparison.OrdinalIgnoreCase);

        app.Use(async (ctx, next) =>
        {
            if (dashboardPublic && IsPublicPath(ctx.Request.Path))
            {
                await next();
                return;
            }

            if (IsAuthorised(ctx, proxyApiKey))
            {
                await next();
            }
            else
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("""{"error":"unauthorized"}""");
            }
        });

        return app;
    }

    /// <summary>
    /// Prefix matching, but only on a path boundary: without the boundary check a request for
    /// <c>/dashboard-secret</c> would be let through by the <c>/dashboard</c> exemption.
    /// </summary>
    private static bool IsPublicPath(PathString path)
    {
        string value = path.Value ?? string.Empty;

        foreach (string prefix in PublicPathPrefixes)
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (value.Length == prefix.Length || prefix.EndsWith('/') || value[prefix.Length] == '/')
                return true;
        }
        return false;
    }

    private static bool IsAuthorised(HttpContext ctx, string proxyApiKey)
    {
        string? auth = ctx.Request.Headers.Authorization;
        if (auth is not null
            && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && auth["Bearer ".Length..] == proxyApiKey)
        {
            return true;
        }

        // The dashboard page fetches its data with this header; a browser cannot attach a bearer
        // token to a plain navigation, so the page reads the key from ?key= / localStorage and
        // sends it here instead.
        string? headerKey = ctx.Request.Headers["X-Proxy-Key"];
        return headerKey == proxyApiKey;
    }
}
