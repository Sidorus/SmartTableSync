namespace SmartTableSync.Planning;

public sealed class SyncPlanHeuristic : ISyncPlanHeuristic
{
    public SyncPlan DerivePlan(
        TableProfile profile,
        PlanHeuristicOptions options,
        string? changeTrackingColumn = null,
        bool allowFullRescan = false)
    {
        // RowByRow when either an incoming FK could race against Data Sync's own delivery
        // of the referenced table (ProductAttributeValue's actual reason), or rows are large
        // enough that per-row failure isolation matters more than batching efficiency
        // (DocumentAsset's actual reason — no FK involved, just large rows). A single
        // "FK presence" rule alone would miss the second case entirely.
        var mergeStrategy = profile.HasIncomingForeignKeys || profile.MaxLobRowSizeBytes > options.LargeRowThresholdBytes
            ? MergeStrategy.RowByRow
            : MergeStrategy.Tvp;

        // Batch size keys off the MAX observed row size for LOB-bearing tables, not the
        // average — an average hides the rare oversized-row case (DocumentAsset: mostly
        // small rows, worst case ~52 MB). Narrow tables with no LOB columns have no such
        // outlier risk, so average is a safe, representative estimate there.
        var effectiveRowSize = profile.MaxLobColumnSizeBytes.Count > 0
            ? profile.MaxLobRowSizeBytes
            : profile.AvgRowSizeBytes;
        if (effectiveRowSize <= 0)
            effectiveRowSize = 1;

        var rawBatchSize = (int)Math.Min(int.MaxValue, options.TargetBatchPayloadBytes / effectiveRowSize);
        var batchSize = Math.Clamp(rawBatchSize, options.MinBatchSize, options.MaxBatchSize);

        var bulkCopyOptions = profile.IdentityColumn is not null
            ? SqlBulkCopyOptions.KeepIdentity
            : SqlBulkCopyOptions.Default;

        var maxRowSizeMb = effectiveRowSize / (1024.0 * 1024.0);
        var bulkCopyTimeoutSeconds = (int)Math.Max(
            options.BaseTimeoutSeconds,
            options.BaseTimeoutSeconds + maxRowSizeMb * options.SecondsPerMb);

        // 547 = FK constraint violation. Only retryable when RowByRow was chosen for FK-
        // timing reasons specifically — a RowByRow choice driven by row size alone has no
        // FK timing race to retry around.
        IReadOnlyList<int> retryableSqlErrorNumbers = mergeStrategy == MergeStrategy.RowByRow && profile.HasIncomingForeignKeys
            ? [547]
            : [];

        var tvpTypeName = mergeStrategy == MergeStrategy.Tvp
            ? $"{profile.TableName}TableType"
            : null;

        var incrementalStrategy = DeriveIncrementalStrategy(profile, options, changeTrackingColumn, allowFullRescan);

        return new SyncPlan(
            TableName: profile.TableName,
            MergeStrategy: mergeStrategy,
            BatchSize: batchSize,
            BulkCopyOptions: bulkCopyOptions,
            BulkCopyTimeoutSeconds: bulkCopyTimeoutSeconds,
            RetryableSqlErrorNumbers: retryableSqlErrorNumbers,
            TvpTypeName: tvpTypeName,
            ChangeTrackingColumn: changeTrackingColumn,
            IncrementalStrategy: incrementalStrategy);
    }

    // No gate on the safe case (a usable column exists), explicit consent for the expensive
    // case (a full re-scan every cycle) — same philosophy as Auto-mode DDL execution, here
    // applied per-table instead of globally.
    private static IncrementalStrategy DeriveIncrementalStrategy(
        TableProfile profile, PlanHeuristicOptions options, string? changeTrackingColumn, bool allowFullRescan)
    {
        if (changeTrackingColumn is not null)
            return IncrementalStrategy.Timestamp;

        if (profile.RowCount <= options.FullRescanRowCountThreshold || allowFullRescan)
            return IncrementalStrategy.FullRescan;

        throw new InvalidOperationException(
            $"{profile.TableName}: no usable change-tracking column and {profile.RowCount} rows exceeds the " +
            $"FullRescan threshold ({options.FullRescanRowCountThreshold}) — set " +
            $"SmartTableSync:ChangeTrackingColumns:{profile.TableName} to the correct column name, or add " +
            $"'{profile.TableName}' to SmartTableSync:AllowFullRescanFor to accept a full re-scan every cycle.");
    }
}
