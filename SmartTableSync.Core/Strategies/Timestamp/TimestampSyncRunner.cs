namespace SmartTableSync.Strategies.Timestamp;

/// <summary>Timestamp strategy: rows whose datetime column (SyncPlan.IncrementalColumn, default
/// "LastUpdate") is at or after the last successful sync, minus a one-minute overlap. The
/// position is CustomSyncState.LastSyncDate, which SyncOrchestrator advances after the run, so
/// there is nothing to commit here.</summary>
internal sealed class TimestampSyncRunner : ChangeWindowSyncRunner<DateTime>
{
    public TimestampSyncRunner(
        TableProfile profile, SyncPlan plan, TableSyncCommandFactory commands, IBatchMergeStrategy mergeStrategy, ILogger logger)
        : base(profile, plan, commands, mergeStrategy, logger)
    {
    }

    protected override Task<ChangeWindow<DateTime>> OpenWindowAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct) =>
        Task.FromResult(new ChangeWindow<DateTime>(From: lastSyncDate, To: lastSyncDate));

    protected override Task<DataTable> ReadPageAsync(
        SqlConnection parent, ChangeWindow<DateTime> window, string? afterKey, int pageSize, CancellationToken ct) =>
        LoadPageAsync(Commands.BuildTimestampPageCommand(parent, window.From, afterKey, pageSize), ct);
}
