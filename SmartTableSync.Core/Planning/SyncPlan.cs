namespace SmartTableSync.Planning;

/// <summary>The resolved, ready-to-use configuration for syncing one table — derived by
/// ISyncPlanHeuristic from a TableProfile, or overridden by hand in SyncTableConfig.</summary>
public sealed record SyncPlan(
    string TableName,
    MergeStrategy MergeStrategy,
    int BatchSize,
    SqlBulkCopyOptions BulkCopyOptions,
    int BulkCopyTimeoutSeconds,
    IReadOnlyList<int> RetryableSqlErrorNumbers,
    string? TvpTypeName);
