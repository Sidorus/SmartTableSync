namespace SmartTableSync.Syncers.Runners;

/// <summary>Captures how a strategy marks "now" and recognizes its own stored positions, so the
/// initial copy can record where incremental sync should start.</summary>
/// <param name="CaptureAsync">Reads the strategy's current position for a table (parent
/// connection, table name) and returns it in the format the strategy stores.</param>
/// <param name="IsOwnPosition">Whether a stored position was written by this strategy; anything
/// else (none, or another strategy's format after a switch) is replaced.</param>
public sealed record IncrementalBaseline(
    Func<SqlConnection, string, CancellationToken, Task<string>> CaptureAsync,
    Func<string?, bool> IsOwnPosition);

/// <summary>Wraps the initial-sync runner for strategies with their own position (RowVersion,
/// SqlChangeTracking): records the current position BEFORE the initial copy starts, so the first
/// incremental pass reads only what changed since the copy began instead of the whole table.
/// Captured before, not after: changes made while the copy runs must fall inside the first
/// incremental window. A resumed initial sync keeps the baseline it recorded the first time, or
/// changes made before the interruption would be skipped.</summary>
public sealed class BaselineCapturingSyncRunner : ISyncRunner
{
    private readonly ISyncRunner _initialSyncRunner;
    private readonly IncrementalBaseline _baseline;
    private readonly string _tableName;
    private readonly ISyncStateRepository _syncState;
    private readonly ILogger _logger;

    public BaselineCapturingSyncRunner(
        ISyncRunner initialSyncRunner, IncrementalBaseline baseline, string tableName, ISyncStateRepository syncState, ILogger logger)
    {
        _initialSyncRunner = initialSyncRunner;
        _baseline = baseline;
        _tableName = tableName;
        _syncState = syncState;
        _logger = logger;
    }

    public async Task<SyncOutcome> RunAsync(SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct)
    {
        var storedPosition = await _syncState.GetIncrementalPositionAsync(child, _tableName, ct);

        if (!_baseline.IsOwnPosition(storedPosition))
        {
            var position = await _baseline.CaptureAsync(parent, _tableName, ct);
            await _syncState.UpdateIncrementalPositionAsync(child, _tableName, position, ct);
            _logger.LogInformation("{Table}: incremental baseline {Position} recorded before initial sync", _tableName, position);
        }

        return await _initialSyncRunner.RunAsync(parent, child, lastSyncDate, ct);
    }
}
