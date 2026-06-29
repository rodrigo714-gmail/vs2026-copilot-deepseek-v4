using System.Text.Json;

internal static class UsageEndpoints
{
    internal static IEndpointRouteBuilder MapUsageEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /usage — full report with cost breakdown, latency stats, Arena data
        app.MapGet("/usage", (UsageTracker tracker) =>
        {
            var report = tracker.GetReport();
            return Results.Json(new
            {
                since = report.SinceUtc.ToString("o"),
                until = report.UntilUtc.ToString("o"),
                uptime_hours = Math.Round((report.UntilUtc - report.SinceUtc).TotalHours, 1),
                total_requests = report.Models.Sum(m => m.TotalRequests),
                total_cost_usd = report.TotalCostUsd,
                models = report.Models.Select(m => new
                {
                    provider = m.Provider,
                    model = m.Model,
                    tier = m.Tier,
                    requests = m.TotalRequests,
                    success_rate_pct = m.SuccessRatePct,
                    latency_avg_ms = m.AvgLatencyMs,
                    latency_min_ms = m.MinLatencyMs,
                    latency_max_ms = m.MaxLatencyMs,
                    tokens_in = m.TotalPromptTokens,
                    tokens_out = m.TotalCompletionTokens,
                    tokens_cached = m.TotalCachedTokens,
                    avg_tokens_per_req = m.TotalRequests > 0
                        ? Math.Round((double)(m.TotalPromptTokens + m.TotalCompletionTokens) / m.TotalRequests, 0)
                        : 0,
                    cost_usd = m.TotalCostUsd,
                    price = new { input_per_m = m.PriceInputPerM, output_per_m = m.PriceOutputPerM },
                    arena = new { elo = m.ArenaElo, webdev = m.ArenaWebDev, agent_win_rate_pct = m.ArenaAgentWinRate },
                    estimated_tps = m.EstimatedTps,
                    notes = m.Notes
                })
            }, JsonDefaults.SnakeCase);
        });

        // GET /usage/summary — lightweight text summary for quick terminal view
        app.MapGet("/usage/summary", (UsageTracker tracker) =>
        {
            var report = tracker.GetReport();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Usage since {report.SinceUtc:yyyy-MM-dd HH:mm} UTC ({Math.Round((report.UntilUtc - report.SinceUtc).TotalHours, 1)}h)");
            sb.AppendLine($"Total cost: ${report.TotalCostUsd:F4}");
            sb.AppendLine($"Total requests: {report.Models.Sum(m => m.TotalRequests)}");
            sb.AppendLine();
            sb.AppendLine($"{"Provider",-10} {"Model",-25} {"Tier",-8} {"Req",-6} {"OK%",-6} {"AvgMs",-7} {"Cost",-10} {"$/M out",-8} {"ELO",-5}");
            sb.AppendLine(new string('-', 95));
            foreach (var m in report.Models.OrderByDescending(m => m.TotalCostUsd))
            {
                sb.AppendLine($"{m.Provider,-10} {m.Model,-25} {m.Tier,-8} {m.TotalRequests,-6} {m.SuccessRatePct,-5:F1}% {m.AvgLatencyMs,-6:F0} ${m.TotalCostUsd,-8:F6} ${m.PriceOutputPerM,-7:F2} {m.ArenaElo?.ToString() ?? "-",-5}");
            }
            return Results.Content(sb.ToString(), "text/plain");
        });

        // POST /usage/reset — reset all counters (admin)
        app.MapPost("/usage/reset", (UsageTracker tracker) =>
        {
            tracker.Reset();
            return Results.Ok(new { status = "reset", timestamp = DateTime.UtcNow.ToString("o") });
        });

        // GET /usage/pricing — full price catalog
        app.MapGet("/usage/pricing", () =>
        {
            var all = PricingCatalog.All.Select(p => new
            {
                provider = p.Provider,
                model = p.ModelMatch ?? "*",
                tier = p.Tier,
                input_per_m = p.InputPerMillion,
                output_per_m = p.OutputPerMillion,
                cached_input_per_m = p.CachedInputPerMillion,
                arena_elo = p.ArenaElo,
                arena_webdev = p.ArenaWebDev,
                arena_agent_wr = p.ArenaAgentWinRate,
                estimated_tps = p.EstimatedTps,
                notes = p.Notes
            }).OrderBy(p => p.provider).ThenBy(p => p.model);
            return Results.Json(new { pricing = all }, JsonDefaults.SnakeCase);
        });

        return app;
    }
}
