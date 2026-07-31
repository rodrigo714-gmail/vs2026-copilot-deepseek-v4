internal static class HealthEndpoints
{
    internal static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", (ProviderRegistry providerRegistry, ModelCatalogService modelCatalog) =>
        {
            // Fire-and-forget refresh to avoid blocking first request
            _ = modelCatalog.RefreshAvailableModelsIfNeeded(CancellationToken.None);
            return Results.Ok(new
            {
                status = "ok",
                name = ProxyVersion.Name,
                version = ProxyVersion.Current,
                model = providerRegistry.DefaultModel,
                available_models = modelCatalog.AvailableModels,
                providers = providerRegistry.Providers.Select(p => p.Name).ToArray(),
                models_last_refresh_utc = modelCatalog.ModelsLastRefreshUtc
            });
        });

        // Which providers are currently stood down, why, and until when.
        app.MapGet("/api/resilience/cooldowns", (ProviderHealthService health) =>
        {
            DateTimeOffset now = DateTimeOffset.Now;
            return Results.Json(new
            {
                cooldowns = health.Snapshot().Select(c => new
                {
                    provider = c.Provider,
                    display_name = ProviderCapabilitiesRegistry.DisplayName(c.Provider),
                    model = c.Model,
                    kind = c.Kind.ToString(),
                    quota_period = c.Period.ToString(),
                    reason = c.Reason,
                    failure_count = c.FailureCount,
                    until_utc = c.UntilUtc.UtcDateTime.ToString("o"),
                    seconds_remaining = Math.Round(c.SecondsRemaining(now))
                }),
                recent_failovers = health.RecentFailovers().Select(f => new
                {
                    at_utc = f.AtUtc.UtcDateTime.ToString("o"),
                    from_provider = f.FromProvider,
                    to_provider = f.ToProvider,
                    model = f.Model,
                    status_code = f.StatusCode,
                    kind = f.Kind.ToString(),
                    latency_ms = f.LatencyMs
                })
            }, JsonDefaults.SnakeCase);
        });

        // Manual re-enable, for when a key has been fixed and waiting out the timer is pointless.
        app.MapPost("/api/resilience/reset", (ProviderHealthService health, string? provider, string? model) =>
        {
            if (string.IsNullOrWhiteSpace(provider))
            {
                health.ClearAll();
                return Results.Ok(new { status = "cleared", scope = "all" });
            }

            bool removed = health.Clear(provider, string.IsNullOrWhiteSpace(model) ? null : model);
            return Results.Ok(new { status = removed ? "cleared" : "not_cooling", provider, model });
        });

        return app;
    }
}
