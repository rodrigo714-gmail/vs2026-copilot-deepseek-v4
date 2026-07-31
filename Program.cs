[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ProxyTests")]

// Cargar .env automáticamente si existe
string envPath = Path.Combine(AppContext.BaseDirectory, ".env");
if (!File.Exists(envPath))
{
    envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
}
if (File.Exists(envPath))
{
    Console.WriteLine($"  Configuration loaded from: {envPath}");
    foreach (string line in File.ReadAllLines(envPath))
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("#"))
            continue;
        int eq = trimmed.IndexOf('=');
        if (eq < 1)
            continue;
        string key = trimmed[..eq].Trim();
        string value = trimmed[(eq + 1)..].Trim().Trim('"');
        if (!string.IsNullOrEmpty(key))
            Environment.SetEnvironmentVariable(key, value);
    }
}

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

int port = int.TryParse(Environment.GetEnvironmentVariable("PROXY_PORT"), out int p) ? p : 11434;
string? proxyApiKey = Environment.GetEnvironmentVariable("PROXY_API_KEY");

builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddSingleton<ProviderHttpClientFactory>();
builder.Services.AddSingleton<ProviderHealthService>();
builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddSingleton<ModelSelectionStore>();
builder.Services.AddSingleton<ModelCatalogService>();
builder.Services.AddSingleton<ReasoningCacheService>();
builder.Services.AddSingleton<RequestTransformer>();
builder.Services.AddSingleton<OllamaResponseBuilder>();
builder.Services.AddSingleton<UsageTracker>();
builder.Services.AddSingleton<ProxyLogger>();
builder.Services.AddSingleton<ChatStreamingService>();
builder.Services.AddSingleton<UsageRollupStore>();
builder.Services.AddSingleton<FreeTierCatalogStore>();
builder.Services.AddSingleton<UsageTrackerService>();
builder.Services.AddSingleton<ProviderBillingService>();

builder.Services.AddHostedService<ProviderBenchmarkService>();
builder.Services.AddHostedService<UsageSnapshotService>();

WebApplication app = builder.Build();
app.UseUpstreamErrorHandling();
app.UseOptionalProxyAuthentication(proxyApiKey);
// Serves wwwroot/: the dashboard markup and the vendored Chart.js. Static-file middleware ships
// in the Microsoft.AspNetCore.App shared framework, so this adds no package reference.
app.UseStaticFiles();

ModelCatalogService modelCatalog = app.Services.GetRequiredService<ModelCatalogService>();
ProviderRegistry providerRegistry = app.Services.GetRequiredService<ProviderRegistry>();
await modelCatalog.RefreshAvailableModels(CancellationToken.None);

app.MapOpenAiEndpoints();
app.MapUsageEndpoints();
app.MapDashboardEndpoints();
app.MapFreeTierEndpoints();
app.MapOllamaEndpoints();
app.MapHealthEndpoints();

// Plain ASCII: the Windows console defaults to codepage 850/437, which mangles
// box-drawing characters and emoji into noise.
string[] providerNames = [.. providerRegistry.Providers.Select(pv => pv.Name)];
Console.WriteLine();
Console.WriteLine($"  {ProxyVersion.Name} - one Ollama/OpenAI endpoint, many providers");
Console.WriteLine("  ==============================================================");
Console.WriteLine($"  Version   : {ProxyVersion.Current}");
Console.WriteLine($"  Providers : {providerNames.Length} ({string.Join(", ", providerNames)})");
Console.WriteLine($"  Models    : {modelCatalog.AvailableModels.Length} discovered");
Console.WriteLine($"  Default   : {providerRegistry.DefaultModel}");
Console.WriteLine($"  OpenAI API: http://localhost:{port}/v1/chat/completions");
Console.WriteLine($"  Ollama API: http://localhost:{port}/api/chat   (Visual Studio 2026 BYOM)");
Console.WriteLine($"  Dashboard : http://localhost:{port}/dashboard");
Console.WriteLine($"  Auth      : {(string.IsNullOrEmpty(proxyApiKey) ? "open (PROXY_API_KEY not set)" : "required (PROXY_API_KEY)")}");
Console.WriteLine();

app.Run();

public partial class Program { }
