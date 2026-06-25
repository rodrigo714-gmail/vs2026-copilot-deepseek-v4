/// <summary>
/// Background service that periodically snapshots token usage for historical trend charts.
/// Runs every 60 seconds and delegates to <see cref="UsageTrackerService.TryTakeSnapshot"/>.
/// </summary>
internal sealed class UsageSnapshotService : BackgroundService
{
    private readonly UsageTrackerService _usageTracker;
    private readonly ILogger<UsageSnapshotService>? _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(60);

    public UsageSnapshotService(UsageTrackerService usageTracker, ILogger<UsageSnapshotService>? logger = null)
    {
        _usageTracker = usageTracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("📊 Usage snapshot service started (interval: {Interval}s)", _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_interval, stoppingToken);

            try
            {
                _usageTracker.TryTakeSnapshot();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to take usage snapshot");
            }
        }
    }
}