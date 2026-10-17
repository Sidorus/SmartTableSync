namespace SmartTableSync.Planning;

/// <summary>The resolved, ready-to-use configuration for syncing one table — derived by
/// ISyncPlanHeuristic from a TableProfile, with any overrides from SyncTableConfig applied.</summary>
/// <param name="BulkCopyTimeoutSeconds">Command timeout for every statement against the table,
/// not only bulk copy. The name matches the persisted SyncTableConfig column.</param>
/// <param name="IncrementalColumn">The column the incremental strategy filters on: the
/// datetime column for Timestamp, the rowversion column for RowVersion, null for FullRescan and
/// SqlChangeTracking.</param>
internal sealed record SyncPlan(
    string TableName,
    MergeStrategy MergeStrategy,
    int BatchSize,
    SqlBulkCopyOptions BulkCopyOptions,
    int BulkCopyTimeoutSeconds,
    IReadOnlyList<int> RetryableSqlErrorNumbers,
    string? TvpTypeName,
    string? IncrementalColumn = null,
    IncrementalStrategy IncrementalStrategy = IncrementalStrategy.Timestamp);
