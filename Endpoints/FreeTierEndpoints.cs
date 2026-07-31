internal static class FreeTierEndpoints
{
    internal static IEndpointRouteBuilder MapFreeTierEndpoints(this IEndpointRouteBuilder app)
    {
        // One fetch that answers "how much free allowance do I have, how much have I used, and
        // what is currently stood down". Folding the cooldown in here is what lets the dashboard
        // render a coherent quota panel without correlating three endpoints client-side.
        app.MapGet("/api/free-tier/summary", (
            FreeTierCatalogStore catalog,
            UsageRollupStore rollup,
            ProviderRegistry providerRegistry,
            ProviderHealthService health) =>
        {
            DateTimeOffset now = DateTimeOffset.Now;
            int daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);

            string[] configured = [.. providerRegistry.Providers.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase)];
            IReadOnlyDictionary<string, ProviderDayUsage> monthUsage = rollup.CurrentMonthByProvider();
            IReadOnlyDictionary<string, ProviderDayUsage> dayUsage = rollup.CurrentDayByProvider();
            IReadOnlyList<CooldownState> cooldowns = health.Snapshot();

            long steadyMonthly = catalog.SteadyMonthlyTokens(configured, daysInMonth);
            long usedThisMonth = monthUsage.Values.Sum(u => u.TotalTokens);

            var providers = configured.Select(name =>
            {
                FreeTierEntry? entry = catalog.Get(name);
                monthUsage.TryGetValue(name, out ProviderDayUsage? month);
                dayUsage.TryGetValue(name, out ProviderDayUsage? today);

                long? monthlyAllowance = entry?.MonthlyTokenEquivalent(daysInMonth);
                long usedMonth = month?.TotalTokens ?? 0;
                CooldownState? cooldown = cooldowns.FirstOrDefault(c =>
                    string.Equals(c.Provider, name, StringComparison.OrdinalIgnoreCase));

                return new
                {
                    provider = name,
                    display_name = ProviderCapabilitiesRegistry.DisplayName(name),
                    free_type = (entry?.FreeType ?? FreeTierType.None).ToString(),
                    monthly_tokens = monthlyAllowance,
                    daily_tokens = entry?.DailyTokens,
                    credit_tokens = entry?.CreditTokens,
                    requests_per_minute = entry?.RequestsPerMinute,
                    requests_per_day = entry?.RequestsPerDay,
                    used_this_month = usedMonth,
                    used_today = today?.TotalTokens ?? 0,
                    requests_today = today?.Requests ?? 0,
                    remaining = monthlyAllowance is { } cap ? Math.Max(0, cap - usedMonth) : (long?)null,
                    pct_used = monthlyAllowance is > 0 ? Math.Round(100.0 * usedMonth / monthlyAllowance.Value, 2) : (double?)null,
                    rate_limited_today = today?.RateLimited ?? 0,
                    quota_exhausted_today = today?.QuotaExhausted ?? 0,
                    cost_usd_this_month = Math.Round(month?.CostUsd ?? 0, 6),
                    tos = entry?.Tos ?? "unknown",
                    tos_note = entry?.TosNote,
                    signup_url = entry?.SignupUrl,
                    verified_at = entry?.VerifiedAt,
                    notes = entry?.Notes,
                    cooldown = cooldown is null ? null : new
                    {
                        kind = cooldown.Kind.ToString(),
                        quota_period = cooldown.Period.ToString(),
                        reason = cooldown.Reason,
                        until_utc = cooldown.UntilUtc.UtcDateTime.ToString("o"),
                        seconds_remaining = Math.Round(cooldown.SecondsRemaining(now))
                    }
                };
            }).ToArray();

            return Results.Json(new
            {
                curated_at = catalog.CuratedAt,
                persistent = rollup.IsPersistent,
                totals = new
                {
                    // Pool-deduped and excluding uncapped/one-time tiers, so this figure is one
                    // a user can actually plan against.
                    steady_monthly_tokens = steadyMonthly,
                    signup_credit_tokens = catalog.SignupCreditTokens(configured),
                    uncapped_providers = catalog.UncappedProviders(configured),
                    used_this_month = usedThisMonth,
                    remaining = Math.Max(0, steadyMonthly - usedThisMonth),
                    pct_used = steadyMonthly > 0 ? Math.Round(100.0 * usedThisMonth / steadyMonthly, 2) : 0
                },
                providers
            }, JsonDefaults.SnakeCase);
        });

        return app;
    }
}
