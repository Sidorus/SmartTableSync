namespace SmartTableSync.Configuration;

/// <summary>The persisted row for one managed table in dbo.SyncTableConfig — introspected
/// shape + heuristically-derived plan + optional hand-set overrides, surviving container
/// redeploys because it lives in the child DB rather than a local file.</summary>
internal sealed record SyncTableConfig(
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
    string? IncrementalColumn = null,
    IncrementalStrategy IncrementalStrategy = IncrementalStrategy.Timestamp)
{
    /// <summary>Maps to a SyncPlan, applying every non-null override over the derived value.
    /// BulkCopyOptions is recomputed from IdentityColumn rather than stored. IncrementalColumn
    /// and IncrementalStrategy have no override column; they come from SyncSettings.
    ///
    /// TvpTypeName follows the RESOLVED merge strategy: the stored name is null unless the
    /// heuristic itself chose Tvp, so a MergeStrategyOverride of Tvp falls back to the same
    /// "{TableName}TableType" convention. Without that, the TVP type name would be empty and
    /// every batch (and, in Auto mode, the generated CREATE TYPE) would fail.</summary>
    public SyncPlan ToSyncPlan()
    {
        var resolvedMergeStrategy = MergeStrategyOverride ?? MergeStrategy;
        var resolvedTvpTypeName = resolvedMergeStrategy == MergeStrategy.Tvp
            ? TvpTypeName ?? $"{TableName}TableType"
            : null;

        return new SyncPlan(
            TableName: TableName,
            MergeStrategy: resolvedMergeStrategy,
            BatchSize: BatchSizeOverride ?? BatchSize,
            BulkCopyOptions: IdentityColumn is not null ? SqlBulkCopyOptions.KeepIdentity : SqlBulkCopyOptions.Default,
            BulkCopyTimeoutSeconds: BulkCopyTimeoutSecondsOverride ?? BulkCopyTimeoutSeconds,
            RetryableSqlErrorNumbers: RetryableSqlErrorsOverride ?? RetryableSqlErrorNumbers,
            TvpTypeName: resolvedTvpTypeName,
            IncrementalColumn: IncrementalColumn,
            IncrementalStrategy: IncrementalStrategy);
    }

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
        IncrementalColumn: plan.IncrementalColumn,
        IncrementalStrategy: plan.IncrementalStrategy);

    /// <summary>An operator overriding MergeStrategy to RowByRow for FK-timing reasons expects
    /// the matching [547] retry behavior the heuristic itself would have applied for the same
    /// reason (SyncPlanHeuristic.DerivePlan couples them: RowByRow-because-of-FK always implies
    /// [547]) — without this, overriding just the strategy silently drops FK-retry handling
    /// unless the operator also knows to set RetryableSqlErrorsOverride separately. Pure, so
    /// it's unit-testable without the live SqlConnection EnsureProvisionedAsync needs.</summary>
    public static IReadOnlyList<int>? ResolveRetryableSqlErrorsOverride(
        MergeStrategy? mergeStrategyOverride, IReadOnlyList<int>? existingRetryableSqlErrorsOverride, bool referencesOtherTables) =>
        mergeStrategyOverride == MergeStrategy.RowByRow && existingRetryableSqlErrorsOverride is null && referencesOtherTables
            ? [547]
            : existingRetryableSqlErrorsOverride;
}
