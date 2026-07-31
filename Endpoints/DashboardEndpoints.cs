using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DashboardEndpoints
{
    internal static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        // JSON API: full usage data for all providers
        app.MapGet("/api/usage", (UsageTrackerService usageTracker, ProviderRegistry providerRegistry) =>
        {
            // Ensure all configured providers are tracked (even those with zero usage)
            foreach (var provider in providerRegistry.Providers)
            {
                usageTracker.EnsureProvider(provider.Name);
            }

            var data = usageTracker.GetAllStats();
            return Results.Json(data, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
            });
        });

        // JSON API: usage data for a single provider
        app.MapGet("/api/usage/{provider}", (string provider, UsageTrackerService usageTracker) =>
        {
            var stats = usageTracker.GetProviderStats(provider);
            if (stats is null)
                return Results.NotFound(new { error = $"Provider '{provider}' not found" });

            return Results.Json(new
            {
                name = provider,
                displayName = ProviderCapabilitiesRegistry.DisplayName(provider),
                stats
            }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
            });
        });

        // JSON API: provider billing/usage data from provider-specific APIs
        app.MapGet("/api/billing", async (ProviderBillingService billingService) =>
        {
            var billing = await billingService.GetAllBillingInfo();
            return Results.Json(billing, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false
            });
        });

        // JSON API: billing data for a single provider
        app.MapGet("/api/billing/{provider}", async (string provider, ProviderBillingService billingService) =>
        {
            var info = await billingService.GetBillingInfo(provider);
            if (info is null)
                return Results.NotFound(new { error = $"Provider '{provider}' not found" });
            return Results.Json(info, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false
            });
        });

        // Serve the dashboard page. The markup lives in wwwroot/dashboard.html rather than in a
        // ~770-line C# verbatim string: as a real .html file it gets syntax highlighting, needs
        // no "" escaping, and can be edited without a rebuild.
        app.MapGet("/dashboard", async (HttpContext ctx) =>
        {
            string? path = FindDashboardFile();
            if (path is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync("wwwroot/dashboard.html was not found next to the application.", ctx.RequestAborted);
                return;
            }

            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(path, ctx.RequestAborted);
        });

        return app;
    }

    private static string? _cachedDashboardPath;

    /// <summary>
    /// Locates wwwroot/dashboard.html. Walks up from both the binary directory and the working
    /// directory because a test host resolves its content root from the test assembly, not the
    /// application's — a 404 that only reproduces under test is otherwise very confusing.
    /// </summary>
    private static string? FindDashboardFile()
    {
        if (_cachedDashboardPath is not null && File.Exists(_cachedDashboardPath))
            return _cachedDashboardPath;

        foreach (string root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(root);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "wwwroot", "dashboard.html");
                if (File.Exists(candidate))
                    return _cachedDashboardPath = candidate;
                dir = dir.Parent;
            }
        }
        return null;
    }
}
