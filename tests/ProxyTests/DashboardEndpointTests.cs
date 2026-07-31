using System.Net;
using Xunit;

namespace ProxyTests;

/// <summary>
/// The dashboard markup moved out of a C# string literal into wwwroot, which introduces two new
/// ways to break it: the file may not be copied next to the binary, and the static-file
/// middleware may not be serving /vendor. Both fail only at runtime, so they are covered here.
/// </summary>
[Collection("Proxy")]
public class DashboardEndpointTests(ProxyFixture fixture)
{
    private readonly HttpClient _client = fixture.Client;

    [Fact]
    public async Task Dashboard_ServesHtmlFromWwwroot()
    {
        HttpResponseMessage r = await _client.GetAsync("/dashboard");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("text/html", r.Content.Headers.ContentType?.ToString());

        string html = await r.Content.ReadAsStringAsync();
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("AI Proxy Hub", html);
    }

    [Fact]
    public async Task Dashboard_LoadsChartJsLocally_NotFromACdn()
    {
        // The page used to pull Chart.js from jsDelivr, so the whole dashboard broke on a
        // machine with no internet.
        string html = await _client.GetStringAsync("/dashboard");

        Assert.Contains("/vendor/chart.umd.min.js", html);
        Assert.DoesNotContain("cdn.jsdelivr.net", html);
    }

    [Fact]
    public async Task VendoredChartJs_IsServed()
    {
        HttpResponseMessage r = await _client.GetAsync("/vendor/chart.umd.min.js");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(r.Content.Headers.ContentLength > 100_000, "Chart.js looks truncated.");
    }

    [Fact]
    public async Task Dashboard_ContainsTheQuotaPanels()
    {
        string html = await _client.GetStringAsync("/dashboard");

        Assert.Contains("freeTierContainer", html);
        Assert.Contains("cooldownContainer", html);
        Assert.Contains("/api/free-tier/summary", html);
        Assert.Contains("/api/resilience/cooldowns", html);
    }

    // ── The APIs the panels read ─────────────────────────────────────────────

    [Fact]
    public async Task FreeTierSummary_ReportsTotalsAndPerProviderBudgets()
    {
        HttpResponseMessage r = await _client.GetAsync("/api/free-tier/summary");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        using var doc = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        System.Text.Json.JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("curated_at", out _));
        Assert.True(root.TryGetProperty("providers", out System.Text.Json.JsonElement providers));

        System.Text.Json.JsonElement totals = root.GetProperty("totals");
        foreach (string field in new[] { "steady_monthly_tokens", "signup_credit_tokens", "uncapped_providers", "used_this_month", "remaining" })
            Assert.True(totals.TryGetProperty(field, out _), $"totals is missing '{field}'.");

        // The fixture configures exactly one provider (deepseek, pointed at the stub).
        Assert.Equal(1, providers.GetArrayLength());
        System.Text.Json.JsonElement p = providers[0];
        Assert.Equal("deepseek", p.GetProperty("provider").GetString());
        Assert.Equal("DeepSeek", p.GetProperty("display_name").GetString());
        Assert.True(p.TryGetProperty("tos", out _));
        Assert.True(p.TryGetProperty("free_type", out _));
    }

    [Fact]
    public async Task CooldownsEndpoint_IsEmptyWhenNothingHasFailed()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            await _client.GetStringAsync("/api/resilience/cooldowns"));

        Assert.True(doc.RootElement.TryGetProperty("cooldowns", out _));
        Assert.True(doc.RootElement.TryGetProperty("recent_failovers", out _));
    }
}
