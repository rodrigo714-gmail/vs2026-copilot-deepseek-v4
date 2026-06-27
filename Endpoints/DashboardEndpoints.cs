internal static class DashboardEndpoints
{
    internal static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dashboard", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var htmlPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "dashboard.html");
            if (File.Exists(htmlPath))
                await ctx.Response.SendFileAsync(htmlPath);
            else
                await ctx.Response.WriteAsync("<h1>dashboard.html not found</h1>");
        });
        return app;
    }
}
