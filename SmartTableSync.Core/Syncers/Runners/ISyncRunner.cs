namespace SmartTableSync.Syncers.Runners;

/// <summary>
/// Strategy for running one sync pass over a table. ConfigurableTableSyncer depends on this
/// interface alone — it doesn't know "initial" and "incremental" exist as separate concepts,
/// let alone how many implementations there are or how one gets picked over another.
/// TableSyncerFactory and ConfigurableTableSyncer.GetSyncRunner together are the ones that
/// know both concrete strategies exist: TableSyncerFactory builds them, and
/// ConfigurableTableSyncer.GetSyncRunner selects between them by lastSyncDate.
/// </summary>
public interface ISyncRunner
{
    Task<(int synced, int errored, bool retryable)> RunAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct);
}
