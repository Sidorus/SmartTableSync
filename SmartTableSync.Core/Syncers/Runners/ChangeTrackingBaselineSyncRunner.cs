namespace SmartTableSync.Syncers.Runners;

/// <summary>Wraps a SqlChangeTracking table's initial-sync runner: before the initial copy
/// starts, persists the current Change Tracking version as the table's position, so the first
/// incremental cycle reads only what changed since the copy began instead of re-scanning the
/// whole table. Captured BEFORE the copy — a version captured after it would miss changes made
/// while the copy ran. Only set when no CT position exists yet: a resumed initial sync keeps its
/// original baseline, or changes made before the interruption would be skipped.</summary>
public sealed class ChangeTrackingBaselineSyncRunner : ISyncRunner
{
    private readonly ISyncRunner _initialSyncRunner;
    private readonly string _tableName;
    private readonly ISyncStateRepository _syncState;
    private readonly ILogger _logger;

    public ChangeTrackingBaselineSyncRunner(
        ISyncRunner initialSyncRunner, string tableName, ISyncStateRepository syncState, ILogger logger)
    {
        _initialSyncRunner = initialSyncRunner;
        _tableName = tableName;
        _syncState = syncState;
        _logger = logger;
    }

    public async Task<(int synced, int errored, bool retryable)> RunAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct)
    {
        var existing = ChangeTrackingVersions.ParsePosition(
            await _syncState.GetIncrementalPositionAsync(child, _tableName, ct));

        if (existing is null)
        {
            var (_, currentVersion) = await ChangeTrackingVersions.GetBoundsAsync(parent, null, _tableName, ct);
            await _syncState.UpdateIncrementalPositionAsync(
                child, _tableName, ChangeTrackingVersions.FormatPosition(currentVersion), ct);

            _logger.LogInformation(
                "{Table}: Change Tracking baseline set to version {Version} before initial sync", _tableName, currentVersion);
        }

        return await _initialSyncRunner.RunAsync(parent, child, lastSyncDate, ct);
    }
}
