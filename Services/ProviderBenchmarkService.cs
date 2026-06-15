using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Periodically probes every available model (via its resolved provider) with a minimal chat request,
/// measuring latency and success, and writes a timestamped metrics report to docs/testing.
/// Controlled by BENCHMARK_INTERVAL_MINUTES (0 or unset = disabled).
/// </summary>
internal sealed class ProviderBenchmarkService : BackgroundService
{
    private readonly ProviderRegistry _providerRegistry;
    private readonly ModelCatalogService _modelCatalog;
    private readonly ILogger<ProviderBenchmarkService> _logger;

    public ProviderBenchmarkService(
        ProviderRegistry providerRegistry,
        ModelCatalogService modelCatalog,
        ILogger<ProviderBenchmarkService> logger)
    {
        _providerRegistry = providerRegistry;
        _modelCatalog = modelCatalog;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int intervalMinutes = int.TryParse(Environment.GetEnvironmentVariable("BENCHMARK_INTERVAL_MINUTES"), out int parsed)
            ? parsed
            : 0;

        if (intervalMinutes <= 0)
        {
            _logger.LogInformation("ProviderBenchmarkService disabled (set BENCHMARK_INTERVAL_MINUTES > 0 to enable).");
            return;
        }

        string outputDir = Environment.GetEnvironmentVariable("BENCHMARK_OUTPUT_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "docs", "testing", "logs");

        TimeSpan interval = TimeSpan.FromMinutes(intervalMinutes);
        _logger.LogInformation("ProviderBenchmarkService enabled (interval: {Interval} min, output: {Dir}).", intervalMinutes, outputDir);

        // Initial delay so the proxy can finish startup/model discovery first.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunBenchmarkCycle(outputDir, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Benchmark cycle failed.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunBenchmarkCycle(string outputDir, CancellationToken ct)
    {
        string[] models = _modelCatalog.AvailableModels;
        List<BenchmarkResult> results = new(models.Length);

        foreach (string model in models)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> candidates = _providerRegistry.ResolveCandidates(model);
            if (candidates.Count == 0)
            {
                continue;
            }

            (ProviderInfo provider, string upstreamModel) = candidates[0];
            results.Add(await ProbeModel(provider, model, upstreamModel, ct));
        }

        await WriteReport(outputDir, results, ct);
    }

    private static async Task<BenchmarkResult> ProbeModel(ProviderInfo provider, string model, string upstreamModel, CancellationToken ct)
    {
        bool isOllama = provider.Capabilities.ApiFormat == ApiFormat.Ollama;
        string path = provider.Capabilities.ChatPath;

        string requestBody = isOllama
            ? JsonSerializer.Serialize(new
            {
                model = upstreamModel,
                stream = false,
                messages = new[] { new { role = "user", content = "ping" } }
            })
            : JsonSerializer.Serialize(new
            {
                model = upstreamModel,
                max_tokens = 1,
                messages = new[] { new { role = "user", content = "ping" } }
            });

        long startTicks = Environment.TickCount64;

        // Bound each probe so the cycle always terminates regardless of provider behavior.
        using CancellationTokenSource probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            using StringContent content = new(requestBody, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await provider.Client.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, path) { Content = content },
                probeCts.Token);

            long latencyMs = Environment.TickCount64 - startTicks;
            return new BenchmarkResult(model, provider.Name, upstreamModel, response.IsSuccessStatusCode, (int)response.StatusCode, latencyMs, null);
        }
        catch (Exception ex)
        {
            long latencyMs = Environment.TickCount64 - startTicks;
            return new BenchmarkResult(model, provider.Name, upstreamModel, false, 0, latencyMs, ex.GetType().Name);
        }
    }

    private async Task WriteReport(string outputDir, List<BenchmarkResult> results, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);

        var report = new
        {
            timestampUtc = DateTime.UtcNow,
            totalModels = results.Count,
            succeeded = results.Count(r => r.Success),
            failed = results.Count(r => !r.Success),
            results = results
                .OrderBy(r => r.Success ? 0 : 1)
                .ThenBy(r => r.LatencyMs)
                .Select(r => new
                {
                    model = r.Model,
                    provider = r.Provider,
                    upstreamModel = r.UpstreamModel,
                    success = r.Success,
                    statusCode = r.StatusCode,
                    latencyMs = r.LatencyMs,
                    error = r.Error
                })
        };

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        string fileName = $"provider-benchmark-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
        string filePath = Path.Combine(outputDir, fileName);

        await File.WriteAllTextAsync(filePath, json, ct);
        _logger.LogInformation("Benchmark report written: {File} ({Ok}/{Total} OK).", filePath, report.succeeded, report.totalModels);
    }

    private readonly record struct BenchmarkResult(
        string Model,
        string Provider,
        string UpstreamModel,
        bool Success,
        int StatusCode,
        long LatencyMs,
        string? Error);
}
