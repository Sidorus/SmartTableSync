namespace SmartTableSync.Planning;

internal sealed class SyncPlanHeuristic : ISyncPlanHeuristic
{
    public SyncPlan DerivePlan(
        TableProfile profile,
        PlanHeuristicOptions options,
        string? timestampColumn = null,
        bool allowFullRescan = false,
        string? rowVersionColumn = null,
        IncrementalStrategy? strategyOverride = null,
        string? childIdentityColumn = null)
    {
        // An explicit FullRescan or WholeTableCopy override is itself the operator's consent to
        // read the whole table every run.
        var candidate = new StrategyCandidate(
            profile, options, timestampColumn, rowVersionColumn,
            AllowFullRescan: allowFullRescan || strategyOverride is IncrementalStrategy.FullRescan or IncrementalStrategy.WholeTableCopy);
        var incrementalStrategy = SelectIncrementalStrategy(candidate, strategyOverride);

        // A whole-table copy merges nothing. Otherwise RowByRow when the table references another
        // table (its row can arrive before the referenced row does, and fail the FK check) or when
        // rows are large enough that one failure shouldn't fail a whole batch. Either reason alone
        // is enough.
        var mergeStrategy = incrementalStrategy.Kind == IncrementalStrategy.WholeTableCopy
            ? MergeStrategy.Replace
            : profile.ReferencesOtherTables || profile.MaxLobRowSizeBytes > options.LargeRowThresholdBytes
                ? MergeStrategy.RowByRow
                : MergeStrategy.Tvp;

        // Batch size keys off the MAX observed row size for LOB-bearing tables, not the
        // average - an average hides the rare oversized-row case (DocumentAsset: mostly
        // small rows, worst case ~52 MB). Narrow tables with no LOB columns have no such
        // outlier risk, so average is a safe, representative estimate there.
        var effectiveRowSize = profile.MaxLobColumnSizeBytes.Count > 0
            ? profile.MaxLobRowSizeBytes
            : profile.AvgRowSizeBytes;
        if (effectiveRowSize <= 0)
            effectiveRowSize = 1;

        var rawBatchSize = (int)Math.Min(int.MaxValue, options.TargetBatchPayloadBytes / effectiveRowSize);
        var batchSize = Math.Clamp(rawBatchSize, options.MinBatchSize, options.MaxBatchSize);

        // Only a child table that itself has an IDENTITY column needs the parent's values kept
        // explicitly; SmartTableSync's own child tables have none.
        var bulkCopyOptions = childIdentityColumn is not null
            ? SqlBulkCopyOptions.KeepIdentity
            : SqlBulkCopyOptions.Default;

        var maxRowSizeMb = effectiveRowSize / (1024.0 * 1024.0);
        var bulkCopyTimeoutSeconds = (int)Math.Max(
            options.BaseTimeoutSeconds,
            options.BaseTimeoutSeconds + maxRowSizeMb * options.SecondsPerMb);

        // 547 = FK constraint violation. Only retryable when RowByRow was chosen for FK-
        // timing reasons specifically - a RowByRow choice driven by row size alone has no
        // FK timing race to retry around.
        IReadOnlyList<int> retryableSqlErrorNumbers = mergeStrategy == MergeStrategy.RowByRow && profile.ReferencesOtherTables
            ? [547]
            : [];

        var tvpTypeName = mergeStrategy == MergeStrategy.Tvp
            ? SyncTableName.TableTypeName(profile.TableName)
            : null;

        return new SyncPlan(
            TableName: profile.TableName,
            MergeStrategy: mergeStrategy,
            BatchSize: batchSize,
            BulkCopyOptions: bulkCopyOptions,
            BulkCopyTimeoutSeconds: bulkCopyTimeoutSeconds,
            RetryableSqlErrorNumbers: retryableSqlErrorNumbers,
            TvpTypeName: tvpTypeName,
            IncrementalColumn: incrementalStrategy.IncrementalColumn(candidate),
            IncrementalStrategy: incrementalStrategy.Kind,
            ChildIdentityColumn: childIdentityColumn);
    }

    // An override wins but must still be applicable, so a misconfiguration fails here, at
    // provisioning time, instead of producing a runner with nothing to query. Without an
    // override, the first applicable strategy in registry order wins; FullRescan is refused for a
    // large table nobody consented to re-scan, and WholeTableCopy is never picked by that order.
    // A table without a key is the exception: every other strategy matches, pages and resumes
    // by the key, so WholeTableCopy is the only one that can sync it.
    private static IIncrementalStrategy SelectIncrementalStrategy(StrategyCandidate candidate, IncrementalStrategy? strategyOverride)
    {
        var tableName = candidate.Profile.TableName;

        if (candidate.Profile.PrimaryKeyColumns.Count == 0)
        {
            return SelectForTableWithoutKey(candidate, strategyOverride);
        }

        if (strategyOverride is { } forced)
        {
            var strategy = IncrementalStrategies.For(forced);
            return strategy.WhyNotApplicable(candidate) is { } reason
                ? throw new InvalidOperationException(
                    $"{tableName}: SmartTableSync:IncrementalStrategyOverrides forces {forced}, but {reason}")
                : strategy;
        }

        foreach (var strategy in IncrementalStrategies.All.Where(s => s.Kind != IncrementalStrategy.WholeTableCopy))
        {
            if (strategy.WhyNotApplicable(candidate) is null)
            {
                return strategy;
            }
        }

        throw new InvalidOperationException(
            $"{tableName}: {IncrementalStrategies.For(IncrementalStrategy.FullRescan).WhyNotApplicable(candidate)}");
    }

    private static IIncrementalStrategy SelectForTableWithoutKey(StrategyCandidate candidate, IncrementalStrategy? strategyOverride)
    {
        var tableName = candidate.Profile.TableName;
        const string noKey = "the table has no PRIMARY KEY and no unique index whose key columns are all NOT NULL " +
            "(and that isn't filtered or disabled)";

        if (strategyOverride is { } forced && forced != IncrementalStrategy.WholeTableCopy)
        {
            throw new InvalidOperationException(
                $"{tableName}: SmartTableSync:IncrementalStrategyOverrides forces {forced}, but {noKey}, and {forced} matches " +
                $"rows by their key. Remove the override (the table is then copied whole every run), or give the table a key: " +
                $"add one, or name the columns that identify a row in SmartTableSync:KeyColumns:{tableName} (for example \"ColA,ColB\").");
        }

        var wholeTableCopy = IncrementalStrategies.For(IncrementalStrategy.WholeTableCopy);
        return wholeTableCopy.WhyNotApplicable(candidate) is { } reason
            ? throw new InvalidOperationException(
                $"{tableName}: {noKey}, so it can only be synced by copying the whole table every run, but {reason}")
            : wholeTableCopy;
    }
}
