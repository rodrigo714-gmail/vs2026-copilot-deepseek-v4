/// <summary>
/// Background service that periodically snapshots token usage for historical trend charts and
/// flushes the durable daily rollup.
/// </summary>
internal sealed class UsageSnapshotService : BackgroundService
{
    private readonly UsageTrackerService _usageTracker;
    private readonly UsageRollupStore _rollup;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<UsageSnapshotService>? _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(60);

    public UsageSnapshotService(
        UsageTrackerService usageTracker,
        UsageRollupStore rollup,
        IHostApplicationLifetime lifetime,
        ILogger<UsageSnapshotService>? logger = null)
    {
        _usageTracker = usageTracker;
        _rollup = rollup;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("📊 Usage snapshot service started (interval: {Interval}s)", _interval.TotalSeconds);

        // Shutdown is the moment usage is most likely to be lost — a proxy that is stopped and
        // restarted a few times a day would otherwise drop up to a minute of usage each time.
        _lifetime.ApplicationStopping.Register(() =>
        {
            try { _rollup.Flush(force: true); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Failed to flush the usage rollup on shutdown"); }
        });

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Inside the try: cancelling the delay is the normal shutdown path, not a fault
                // that should propagate out of the loop.
                await Task.Delay(_interval, stoppingToken);

                _usageTracker.TryTakeSnapshot();
                _rollup.Flush();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to take usage snapshot");
            }
        }

        _rollup.Flush(force: true);
    }
}
