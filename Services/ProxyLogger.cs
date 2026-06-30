/// <summary>
/// Centralized structured logger for the proxy. Writes to console and optionally
/// to a log file. Controlled by env var PROXY_LOG_LEVEL (info/debug/warn/error/none)
/// and PROXY_LOG_FILE (path, optional).
/// </summary>
internal sealed class ProxyLogger
{
    private readonly string _level;
    private readonly string? _logFile;
    private readonly object _lock = new();

    public ProxyLogger()
    {
        _level = (Environment.GetEnvironmentVariable("PROXY_LOG_LEVEL") ?? "info").ToLowerInvariant();
        _logFile = Environment.GetEnvironmentVariable("PROXY_LOG_FILE");
        if (!string.IsNullOrWhiteSpace(_logFile))
        {
            var dir = Path.GetDirectoryName(_logFile);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        }
    }

    internal bool IsEnabled(string level) => level switch
    {
        "none" => false,
        "error" => level is "error",
        "warn" => level is "error" or "warn",
        "debug" => true,
        _ => level is "error" or "warn" or "info"
    };

    internal void Info(string msg) => Write("info", msg);
    internal void Warn(string msg) => Write("warn", msg);
    internal void Error(string msg) => Write("error", msg);
    internal void Debug(string msg) { if (IsEnabled("debug")) Write("debug", msg); }

    internal void LogRequest(string provider, string model, int candidateIndex, int candidateCount)
    {
        if (!IsEnabled("info")) return;
        string failover = candidateIndex > 1 ? " [FAILOVER]" : "";
        Info($"REQ  {model} -> {provider} ({candidateIndex}/{candidateCount}{failover})");
    }

    internal void LogOk(string provider, string model, int status, double latencyMs, int tokensIn, int tokensOut, decimal cost, int? elo = null, string? tier = null)
    {
        if (!IsEnabled("info")) return;
        string arena = elo.HasValue ? $" [Arena:{elo}]" : "";
        string costStr = tier == "free" ? "$0.00 [FREE]" : $"${cost:F6}";
        Info($"OK   {provider}/{model} {status} {latencyMs:F0}ms in:{tokensIn} out:{tokensOut} {costStr}{arena}");
    }

    internal void LogFail(string provider, string model, int status, double latencyMs, string? detail = null)
    {
        if (!IsEnabled("warn")) return;
        string details = detail is not null ? $" - {detail}" : "";
        Warn($"FAIL {provider}/{model} {status} {latencyMs:F0}ms{details}");
    }

    internal void LogFailover(string provider, string model, int status, double latencyMs)
    {
        if (!IsEnabled("info")) return;
        Info($"FALLOVER {provider}/{model} {status} {latencyMs:F0}ms -> trying next candidate");
    }

    internal void LogStartup(string defaultModel, string[] providers, string[] models, int port, string? proxyApiKey)
    {
        Info($"===== PROXY STARTED =====");
        Info($"Default: {defaultModel} | Providers: {string.Join(", ", providers)} | Models: {models.Length}");
        Info($"URL: http://localhost:{port}/v1  | Auth: {(string.IsNullOrEmpty(proxyApiKey) ? "open" : "PROXY_API_KEY")}");
    }

    private void Write(string level, string msg)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string line = $"[{timestamp}] [{level.ToUpperInvariant()}] {msg}";
        Console.WriteLine(line);
        if (!string.IsNullOrWhiteSpace(_logFile))
        {
            lock (_lock) { File.AppendAllText(_logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); }
        }
    }
}
