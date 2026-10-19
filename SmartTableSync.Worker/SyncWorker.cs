using Microsoft.Extensions.Options;
using SmartTableSync.Abstractions;

namespace SmartTableSync.Worker;

/// <summary>Runs the sync right away, then every <see cref="WorkerSettings.IntervalMinutes"/>
/// minutes until the container stops. A failed run is logged and the next one tries again.</summary>
public sealed class SyncWorker : BackgroundService
{
    private readonly ISyncOrchestrator _orchestrator;
    private readonly IOptions<WorkerSettings> _settings;
    private readonly ILogger<SyncWorker> _logger;

    public SyncWorker(ISyncOrchestrator orchestrator, IOptions<WorkerSettings> settings, ILogger<SyncWorker> logger)
    {
        _orchestrator = orchestrator;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _settings.Value.IntervalMinutes));
        _logger.LogInformation("SmartTableSync worker started; syncing every {IntervalMinutes} minutes", interval.TotalMinutes);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await _orchestrator.RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "SmartTableSync run failed; trying again in {IntervalMinutes} minutes", interval.TotalMinutes);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
