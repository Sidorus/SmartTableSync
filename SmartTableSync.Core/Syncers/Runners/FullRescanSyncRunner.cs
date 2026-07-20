namespace SmartTableSync.Syncers.Runners;

/// <summary>Runs the incremental-sync path for a table with no usable change-tracking
/// column: re-reads and re-MERGEs the entire table every cycle instead of a windowed delta.
/// Doesn't detect which rows changed — it doesn't need to, relying on the same idempotent-
/// MERGE invariant already established everywhere in this codebase. Reuses
/// TableSyncCommandFactory.BuildInitialBatchCommand as-is (the same keyset-paginated query
/// InitialSyncRunner uses), always starting from resumeKey = null since this is a repeating
/// full pass every cycle, not a one-time load with a persisted checkpoint.</summary>
public sealed class FullRescanSyncRunner : ISyncRunner
{
    private readonly TableProfile _profile;
    private readonly SyncPlan _plan;
    private readonly TableSyncCommandFactory _commandFactory;
    private readonly IBatchMergeStrategy _mergeStrategy;
    private readonly ILogger _logger;

    public FullRescanSyncRunner(
        TableProfile profile, SyncPlan plan, TableSyncCommandFactory commandFactory, IBatchMergeStrategy mergeStrategy, ILogger logger)
    {
        _profile = profile;
        _plan = plan;
        _commandFactory = commandFactory;
        _mergeStrategy = mergeStrategy;
        _logger = logger;
    }

    // lastSyncDate is unused — every cycle re-reads and re-MERGEs the whole table
    // regardless of when it last ran. Present only so this satisfies ISyncRunner.
    public async Task<(int synced, int errored, bool retryable)> RunAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct)
    {
        _logger.LogWarning("{Table}: no change-tracking column — running a full re-scan this cycle", _profile.TableName);

        int totalSynced = 0;
        int totalErrored = 0;
        string? resumeKey = null;

        while (true)
        {
            await using var cmd = _commandFactory.BuildInitialBatchCommand(parent, resumeKey, _plan.BatchSize);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            using var batch = await DataTableLoader.LoadAsync(reader, ct);
            var rowsRead = batch.Rows.Count;

            if (rowsRead == 0) break;

            var (synced, errored, _) = await _mergeStrategy.MergeBatchAsync(batch, child, ct);
            totalSynced += synced;
            totalErrored += errored;

            resumeKey = CompositeKey.Extract(batch.Rows[rowsRead - 1], _profile.PrimaryKeyColumns);

            if (rowsRead < _plan.BatchSize) break;
        }

        return (totalSynced, totalErrored, false);
    }
}
