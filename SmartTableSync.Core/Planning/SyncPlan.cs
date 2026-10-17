namespace SmartTableSync.Planning;

/// <summary>The resolved, ready-to-use configuration for syncing one table — derived by
/// ISyncPlanHeuristic from a TableProfile, or overridden by hand in SyncTableConfig.</summary>
/// <param name="IncrementalColumn">The column driving incremental detection — a datetime
/// column name when IncrementalStrategy is Timestamp, a rowversion column name when
/// RowVersion, or null when FullRescan (nothing to filter by).</param>
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
