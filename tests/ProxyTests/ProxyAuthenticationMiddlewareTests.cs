using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ProxyTests;

public class ProxyAuthenticationMiddlewareTests
{
    [Fact]
    public async Task UseOptionalProxyAuthentication_WithNullKey_SkipsAuthentication()
    {
        using TestServer server = new(
            new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseOptionalProxyAuthentication(null);
                    app.Run(ctx => ctx.Response.WriteAsync("ok"));
                }));

        HttpResponseMessage response = await server.CreateClient().GetAsync("/");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("ok", body);
    }

    [Fact]
    public async Task UseOptionalProxyAuthentication_WithEmptyKey_SkipsAuthentication()
    {
        using TestServer server = new(
            new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseOptionalProxyAuthentication("");
                    app.Run(ctx => ctx.Response.WriteAsync("ok"));
                }));

        HttpResponseMessage response = await server.CreateClient().GetAsync("/");

        Assert.Equal(200, (int)response.StatusCode);
    }

    [Fact]
    public async Task UseOptionalProxyAuthentication_WithValidKey_AndValidBearer_Returns200()
    {
        using TestServer server = new(
            new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseOptionalProxyAuthentication("secret-key-123");
                    app.Run(ctx => ctx.Response.WriteAsync("authorized"));
                }));

        HttpClient client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret-key-123");

        HttpResponseMessage response = await client.GetAsync("/");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("authorized", body);
    }

    [Fact]
    public async Task UseOptionalProxyAuthentication_WithValidKey_AndInvalidBearer_Returns401()
    {
        using TestServer server = new(
            new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseOptionalProxyAuthentication("secret-key-123");
                    app.Run(ctx => ctx.Response.WriteAsync("authorized"));
                }));

        HttpClient client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-key");

        HttpResponseMessage response = await client.GetAsync("/");

        Assert.Equal(401, (int)response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("unauthorized", body);
    }

    [Fact]
    public async Task UseOptionalProxyAuthentication_WithValidKey_AndMissingBearer_Returns401()
    {
        using TestServer server = new(
            new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseOptionalProxyAuthentication("secret-key-123");
                    app.Run(ctx => ctx.Response.WriteAsync("authorized"));
                }));

        HttpResponseMessage response = await server.CreateClient().GetAsync("/");

        Assert.Equal(401, (int)response.StatusCode);
    }

    [Fact]
    public async Task UseOptionalProxyAuthentication_WithValidKey_AndNonBearerAuth_Returns401()
    {
        using TestServer server = new(
            new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseOptionalProxyAuthentication("secret-key-123");
                    app.Run(ctx => ctx.Response.WriteAsync("authorized"));
                }));

        HttpClient client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "c2VjcmV0LWtleS0xMjM=");

        HttpResponseMessage response = await client.GetAsync("/");

        Assert.Equal(401, (int)response.StatusCode);
    }

    [Fact]
    public async Task UseOptionalProxyAuthentication_ResponseContentType_IsJson()
    {
        using TestServer server = new(
            new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseOptionalProxyAuthentication("secret-key-123");
                    app.Run(ctx => ctx.Response.WriteAsync("ok"));
                }));

        HttpResponseMessage response = await server.CreateClient().GetAsync("/");

        Assert.Equal(401, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UseOptionalProxyAuthentication_BearerPrefix_IsCaseInsensitive()
    {
        using TestServer server = new(
            new WebHostBuilder()
                .ConfigureServices(s => s.AddRouting())
                .Configure(app =>
                {
                    app.UseOptionalProxyAuthentication("secret-key");
                    app.Run(ctx => ctx.Response.WriteAsync("ok"));
                }));

        HttpClient client = server.CreateClient();
        // Manually set header with lowercase "bearer"
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "bearer secret-key");

        HttpResponseMessage response = await client.GetAsync("/");

        Assert.Equal(200, (int)response.StatusCode);
    }

    // ── Dashboard carve-out ──────────────────────────────────────────────────
    // A browser cannot attach a bearer token to a plain navigation, so with PROXY_API_KEY set
    // the dashboard used to be a 401 page. The page shell and its assets are therefore exempt —
    // but only those. Everything that returns spend, quota or key metadata stays protected.

    private static TestServer AuthServer(string key = "secret-key") => new(
        new WebHostBuilder()
            .ConfigureServices(s => s.AddRouting())
            .Configure(app =>
            {
                app.UseOptionalProxyAuthentication(key);
                app.Run(ctx => ctx.Response.WriteAsync("ok"));
            }));

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/vendor/chart.umd.min.js")]
    [InlineData("/health")]
    public async Task PublicPaths_AreReachableWithoutAKey(string path)
    {
        using TestServer server = AuthServer();
        HttpResponseMessage response = await server.CreateClient().GetAsync(path);
        Assert.Equal(200, (int)response.StatusCode);
    }

    [Theory]
    [InlineData("/api/usage")]
    [InlineData("/api/billing")]
    [InlineData("/api/free-tier/summary")]
    [InlineData("/api/resilience/cooldowns")]
    [InlineData("/v1/chat/completions")]
    [InlineData("/api/chat")]
    public async Task DataAndChatEndpoints_StillRequireAKey(string path)
    {
        using TestServer server = AuthServer();
        HttpResponseMessage response = await server.CreateClient().GetAsync(path);
        Assert.Equal(401, (int)response.StatusCode);
    }

    [Fact]
    public async Task PrefixMatching_DoesNotLeakToSimilarlyNamedPaths()
    {
        // Without a path-boundary check, the "/dashboard" exemption would also open
        // "/dashboard-secret" and anything else sharing the prefix.
        using TestServer server = AuthServer();

        Assert.Equal(401, (int)(await server.CreateClient().GetAsync("/dashboard-secret")).StatusCode);
        Assert.Equal(401, (int)(await server.CreateClient().GetAsync("/healthz-internal")).StatusCode);
        Assert.Equal(200, (int)(await server.CreateClient().GetAsync("/dashboard/anything")).StatusCode);
    }

    [Fact]
    public async Task XProxyKeyHeader_IsAcceptedForDataEndpoints()
    {
        // How the dashboard page authenticates its own fetches.
        using TestServer server = AuthServer();
        HttpClient client = server.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Proxy-Key", "secret-key");

        Assert.Equal(200, (int)(await client.GetAsync("/api/usage")).StatusCode);
    }

    [Fact]
    public async Task WrongXProxyKey_IsRejected()
    {
        using TestServer server = AuthServer();
        HttpClient client = server.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Proxy-Key", "not-the-key");

        Assert.Equal(401, (int)(await client.GetAsync("/api/usage")).StatusCode);
    }

    [Fact]
    public async Task DashboardCarveOut_CanBeTurnedOff()
    {
        string? previous = Environment.GetEnvironmentVariable("PROXY_DASHBOARD_PUBLIC");
        try
        {
            Environment.SetEnvironmentVariable("PROXY_DASHBOARD_PUBLIC", "false");
            using TestServer server = AuthServer();

            Assert.Equal(401, (int)(await server.CreateClient().GetAsync("/dashboard")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXY_DASHBOARD_PUBLIC", previous);
        }
    }
}