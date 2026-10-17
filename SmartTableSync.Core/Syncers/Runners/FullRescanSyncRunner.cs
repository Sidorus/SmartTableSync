namespace SmartTableSync.Syncers.Runners;

/// <summary>FullRescan strategy, for a table with no way to tell which rows changed: every run
/// reads and MERGEs the whole table, page by page. It doesn't need to detect changes because
/// every write is an idempotent MERGE. Also used by ChangeTrackingSyncRunner as its fallback.</summary>
public sealed class FullRescanSyncRunner : ChangeWindowSyncRunner<NoPosition>
{
    public FullRescanSyncRunner(
        TableProfile profile, SyncPlan plan, TableSyncCommandFactory commands, IBatchMergeStrategy mergeStrategy, ILogger logger)
        : base(profile, plan, commands, mergeStrategy, logger)
    {
    }

    protected override Task<ChangeWindow<NoPosition>> OpenWindowAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct)
    {
        Logger.LogWarning("{Table}: running a full re-scan this cycle", Profile.TableName);
        return Task.FromResult(new ChangeWindow<NoPosition>(default, default));
    }

    protected override Task<DataTable> ReadPageAsync(
        SqlConnection parent, ChangeWindow<NoPosition> window, string? afterKey, int pageSize, CancellationToken ct) =>
        LoadPageAsync(Commands.BuildFullTablePageCommand(parent, afterKey, pageSize), ct);
}
