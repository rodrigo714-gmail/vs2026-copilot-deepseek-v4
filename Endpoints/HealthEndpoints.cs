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
                model = providerRegistry.DefaultModel,
                available_models = modelCatalog.AvailableModels,
                providers = providerRegistry.Providers.Select(p => p.Name).ToArray(),
                models_last_refresh_utc = modelCatalog.ModelsLastRefreshUtc
            });
        });

        return app;
    }
}
