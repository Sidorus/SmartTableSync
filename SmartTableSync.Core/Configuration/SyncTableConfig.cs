namespace SmartTableSync.Configuration;

/// <summary>The persisted row for one managed table in dbo.SyncTableConfig — introspected
/// shape + heuristically-derived plan + optional hand-set overrides, surviving container
/// redeploys because it lives in the child DB rather than a local file.</summary>
public sealed record SyncTableConfig(
    string TableName,
    IReadOnlyList<string> PrimaryKeyColumns,
    string? IdentityColumn,
    MergeStrategy MergeStrategy,
    int BatchSize,
    int BulkCopyTimeoutSeconds,
    IReadOnlyList<int> RetryableSqlErrorNumbers,
    string? TvpTypeName,
    string SchemaFingerprint,
    DateTime ConfiguredAtUtc,
    MergeStrategy? MergeStrategyOverride,
    int? BatchSizeOverride,
    int? BulkCopyTimeoutSecondsOverride,
    IReadOnlyList<int>? RetryableSqlErrorsOverride,
    string? ChangeTrackingColumn = null,
    IncrementalStrategy IncrementalStrategy = IncrementalStrategy.Timestamp)
{
    /// <summary>Maps to a SyncPlan, applying any non-null override over the corresponding
    /// heuristically-derived value. BulkCopyOptions isn't stored separately — it's fully
    /// determined by IdentityColumn, so it's recomputed here rather than persisted twice.
    /// ChangeTrackingColumn/IncrementalStrategy have no override — the one configuration
    /// surface for them is SyncSettings (app config), resolved once at provisioning time.</summary>
    public SyncPlan ToSyncPlan() => new(
        TableName: TableName,
        MergeStrategy: MergeStrategyOverride ?? MergeStrategy,
        BatchSize: BatchSizeOverride ?? BatchSize,
        BulkCopyOptions: IdentityColumn is not null ? SqlBulkCopyOptions.KeepIdentity : SqlBulkCopyOptions.Default,
        BulkCopyTimeoutSeconds: BulkCopyTimeoutSecondsOverride ?? BulkCopyTimeoutSeconds,
        RetryableSqlErrorNumbers: RetryableSqlErrorsOverride ?? RetryableSqlErrorNumbers,
        TvpTypeName: TvpTypeName,
        ChangeTrackingColumn: ChangeTrackingColumn,
        IncrementalStrategy: IncrementalStrategy);

    public static SyncTableConfig FromPlan(
        TableProfile profile,
        SyncPlan plan,
        string schemaFingerprint,
        DateTime configuredAtUtc,
        MergeStrategy? mergeStrategyOverride = null,
        int? batchSizeOverride = null,
        int? bulkCopyTimeoutSecondsOverride = null,
        IReadOnlyList<int>? retryableSqlErrorsOverride = null) => new(
        TableName: profile.TableName,
        PrimaryKeyColumns: profile.PrimaryKeyColumns,
        IdentityColumn: profile.IdentityColumn,
        MergeStrategy: plan.MergeStrategy,
        BatchSize: plan.BatchSize,
        BulkCopyTimeoutSeconds: plan.BulkCopyTimeoutSeconds,
        RetryableSqlErrorNumbers: plan.RetryableSqlErrorNumbers,
        TvpTypeName: plan.TvpTypeName,
        SchemaFingerprint: schemaFingerprint,
        ConfiguredAtUtc: configuredAtUtc,
        MergeStrategyOverride: mergeStrategyOverride,
        BatchSizeOverride: batchSizeOverride,
        BulkCopyTimeoutSecondsOverride: bulkCopyTimeoutSecondsOverride,
        RetryableSqlErrorsOverride: retryableSqlErrorsOverride,
        ChangeTrackingColumn: plan.ChangeTrackingColumn,
        IncrementalStrategy: plan.IncrementalStrategy);

    /// <summary>An operator overriding MergeStrategy to RowByRow for FK-timing reasons expects
    /// the matching [547] retry behavior the heuristic itself would have applied for the same
    /// reason (SyncPlanHeuristic.DerivePlan couples them: RowByRow-because-of-FK always implies
    /// [547]) — without this, overriding just the strategy silently drops FK-retry handling
    /// unless the operator also knows to set RetryableSqlErrorsOverride separately. Pure, so
    /// it's unit-testable without the live SqlConnection EnsureProvisionedAsync needs.</summary>
    public static IReadOnlyList<int>? ResolveRetryableSqlErrorsOverride(
        MergeStrategy? mergeStrategyOverride, IReadOnlyList<int>? existingRetryableSqlErrorsOverride, bool hasIncomingForeignKeys) =>
        mergeStrategyOverride == MergeStrategy.RowByRow && existingRetryableSqlErrorsOverride is null && hasIncomingForeignKeys
            ? [547]
            : existingRetryableSqlErrorsOverride;
}
