namespace SmartTableSync.Planning;

/// <summary>The resolved, ready-to-use configuration for syncing one table — derived by
/// ISyncPlanHeuristic from a TableProfile, with any overrides from SyncTableConfig applied.</summary>
/// <param name="BulkCopyTimeoutSeconds">Command timeout for every statement against the table,
/// not only bulk copy. The name matches the persisted SyncTableConfig column.</param>
/// <param name="IncrementalColumn">The column the incremental strategy filters on: the
/// datetime column for Timestamp, the rowversion column for RowVersion, null for FullRescan and
/// SqlChangeTracking.</param>
/// <param name="ChildIdentityColumn">The IDENTITY column of the CHILD table, when it has one.
/// SmartTableSync creates child tables without IDENTITY (every key value comes from the parent),
/// so this is only set for a child table created that way by an earlier version or by hand;
/// copying into it then needs SET IDENTITY_INSERT, and with it ALTER on the table.</param>
internal sealed record SyncPlan(
    string TableName,
    MergeStrategy MergeStrategy,
    int BatchSize,
    SqlBulkCopyOptions BulkCopyOptions,
    int BulkCopyTimeoutSeconds,
    IReadOnlyList<int> RetryableSqlErrorNumbers,
    string? TvpTypeName,
    string? IncrementalColumn = null,
    IncrementalStrategy IncrementalStrategy = IncrementalStrategy.Timestamp,
    string? ChildIdentityColumn = null);
