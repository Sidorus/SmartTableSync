namespace SmartTableSync.Syncers.Runners;

/// <summary>Runs the incremental-sync path for one table: a single windowed query against
/// the parent, handed straight to the table's IBatchMergeStrategy. No loop, no resume
/// state — unlike initial sync, an incremental window is always small enough to handle in
/// one pass.</summary>
public sealed class IncrementalSyncRunner : ISyncRunner
{
    private readonly TableSyncCommandFactory _commandFactory;
    private readonly IBatchMergeStrategy _mergeStrategy;

    public IncrementalSyncRunner(TableSyncCommandFactory commandFactory, IBatchMergeStrategy mergeStrategy)
    {
        _commandFactory = commandFactory;
        _mergeStrategy = mergeStrategy;
    }

    public async Task<(int synced, int errored, bool retryable)> RunAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct)
    {
        await using var cmd = _commandFactory.BuildIncrementalCommand(parent, lastSyncDate);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        using var batch = await DataTableLoader.LoadAsync(reader, ct);

        if (batch.Rows.Count == 0) return (0, 0, false);

        return await _mergeStrategy.MergeBatchAsync(batch, child, ct);
    }
}
