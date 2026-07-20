using Microsoft.Data.SqlClient;
using SmartTableSync.Configuration;
using SmartTableSync.Planning;
using Xunit;

namespace SmartTableSync.Tests.Configuration;

public class SyncTableConfigTests
{
    private static SyncTableConfig CreateConfig(
        MergeStrategy? mergeStrategyOverride = null,
        int? batchSizeOverride = null,
        int? bulkCopyTimeoutSecondsOverride = null,
        IReadOnlyList<int>? retryableSqlErrorsOverride = null,
        string? identityColumn = null) =>
        new(
            TableName: "TestTable",
            PrimaryKeyColumns: ["Id"],
            IdentityColumn: identityColumn,
            MergeStrategy: MergeStrategy.Tvp,
            BatchSize: 200,
            BulkCopyTimeoutSeconds: 120,
            RetryableSqlErrorNumbers: [],
            TvpTypeName: "TestTableTableType",
            SchemaFingerprint: "abc123",
            ConfiguredAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MergeStrategyOverride: mergeStrategyOverride,
            BatchSizeOverride: batchSizeOverride,
            BulkCopyTimeoutSecondsOverride: bulkCopyTimeoutSecondsOverride,
            RetryableSqlErrorsOverride: retryableSqlErrorsOverride);

    [Fact]
    public void ToSyncPlan_NoOverrides_UsesHeuristicDerivedValues()
    {
        var plan = CreateConfig().ToSyncPlan();

        Assert.Equal(MergeStrategy.Tvp, plan.MergeStrategy);
        Assert.Equal(200, plan.BatchSize);
        Assert.Equal(120, plan.BulkCopyTimeoutSeconds);
    }

    [Fact]
    public void ToSyncPlan_WithBatchSizeOverride_OverrideWins()
    {
        var plan = CreateConfig(batchSizeOverride: 50).ToSyncPlan();

        Assert.Equal(50, plan.BatchSize);
    }

    [Fact]
    public void ToSyncPlan_WithMergeStrategyOverride_OverrideWins()
    {
        // Exactly the DocumentAsset-shaped correction scenario: heuristic derived Tvp,
        // operator overrides to RowByRow because this table's rows are actually huge.
        var plan = CreateConfig(mergeStrategyOverride: MergeStrategy.RowByRow).ToSyncPlan();

        Assert.Equal(MergeStrategy.RowByRow, plan.MergeStrategy);
    }

    [Fact]
    public void ToSyncPlan_WithRetryableSqlErrorsOverride_OverrideWins()
    {
        var plan = CreateConfig(retryableSqlErrorsOverride: [547, 2601]).ToSyncPlan();

        Assert.Equal([547, 2601], plan.RetryableSqlErrorNumbers);
    }

    [Fact]
    public void ToSyncPlan_WithIdentityColumn_UsesKeepIdentityRegardlessOfOverrides()
    {
        var plan = CreateConfig(identityColumn: "Id").ToSyncPlan();

        Assert.Equal(SqlBulkCopyOptions.KeepIdentity, plan.BulkCopyOptions);
    }

    [Fact]
    public void FromPlan_ThenToSyncPlan_RoundTripsWithoutOverrides()
    {
        var profile = new SmartTableSync.Introspection.TableProfile(
            TableName: "TestTable",
            Columns: [],
            PrimaryKeyColumns: ["Id"],
            IdentityColumn: null,
            IndexedLobColumns: [],
            HasIncomingForeignKeys: false,
            RowCount: 0,
            AvgRowSizeBytes: 0,
            MaxLobColumnSizeBytes: new Dictionary<string, long>());

        var originalPlan = new SyncPlan("TestTable", MergeStrategy.Tvp, 200, SqlBulkCopyOptions.Default, 120, [], "TestTableTableType");

        var config = SyncTableConfig.FromPlan(profile, originalPlan, "fingerprint1", DateTime.UtcNow);
        var roundTripped = config.ToSyncPlan();

        Assert.Equal(originalPlan.MergeStrategy, roundTripped.MergeStrategy);
        Assert.Equal(originalPlan.BatchSize, roundTripped.BatchSize);
        Assert.Equal(originalPlan.BulkCopyTimeoutSeconds, roundTripped.BulkCopyTimeoutSeconds);
        Assert.Equal(originalPlan.TvpTypeName, roundTripped.TvpTypeName);
    }

    [Fact]
    public void FromPlan_ThenToSyncPlan_RoundTripsChangeTrackingColumnAndIncrementalStrategy()
    {
        var profile = new SmartTableSync.Introspection.TableProfile(
            TableName: "TestTable",
            Columns: [],
            PrimaryKeyColumns: ["Id"],
            IdentityColumn: null,
            IndexedLobColumns: [],
            HasIncomingForeignKeys: false,
            RowCount: 0,
            AvgRowSizeBytes: 0,
            MaxLobColumnSizeBytes: new Dictionary<string, long>());

        var originalPlan = new SyncPlan(
            "TestTable", MergeStrategy.Tvp, 200, SqlBulkCopyOptions.Default, 120, [], "TestTableTableType",
            ChangeTrackingColumn: null, IncrementalStrategy: IncrementalStrategy.FullRescan);

        var config = SyncTableConfig.FromPlan(profile, originalPlan, "fingerprint1", DateTime.UtcNow);
        var roundTripped = config.ToSyncPlan();

        Assert.Null(roundTripped.ChangeTrackingColumn);
        Assert.Equal(IncrementalStrategy.FullRescan, roundTripped.IncrementalStrategy);
    }

    // --- ResolveRetryableSqlErrorsOverride: item 8b, the MergeStrategyOverride/retry
    // consistency fix. Pure, so testable directly without SyncTableProvisioner's live
    // SqlConnection dependency. ---

    [Fact]
    public void ResolveRetryableSqlErrorsOverride_RowByRowOverrideWithForeignKeysAndNoExistingOverride_Derives547()
    {
        var result = SyncTableConfig.ResolveRetryableSqlErrorsOverride(
            mergeStrategyOverride: MergeStrategy.RowByRow, existingRetryableSqlErrorsOverride: null, hasIncomingForeignKeys: true);

        Assert.Equal([547], result);
    }

    [Fact]
    public void ResolveRetryableSqlErrorsOverride_RowByRowOverrideWithoutForeignKeys_LeavesNullAlone()
    {
        // No FK, so there's genuinely nothing to retry around - correctly stays null/empty,
        // same as the heuristic's own "no FK timing race, purely row-size-driven RowByRow"
        // case (DocumentAsset's actual shape).
        var result = SyncTableConfig.ResolveRetryableSqlErrorsOverride(
            mergeStrategyOverride: MergeStrategy.RowByRow, existingRetryableSqlErrorsOverride: null, hasIncomingForeignKeys: false);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveRetryableSqlErrorsOverride_TvpOverride_NeverDerivesRetryOverride()
    {
        var result = SyncTableConfig.ResolveRetryableSqlErrorsOverride(
            mergeStrategyOverride: MergeStrategy.Tvp, existingRetryableSqlErrorsOverride: null, hasIncomingForeignKeys: true);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveRetryableSqlErrorsOverride_NoMergeStrategyOverrideAtAll_LeavesExistingAlone()
    {
        var result = SyncTableConfig.ResolveRetryableSqlErrorsOverride(
            mergeStrategyOverride: null, existingRetryableSqlErrorsOverride: null, hasIncomingForeignKeys: true);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveRetryableSqlErrorsOverride_ExistingOverrideAlreadySet_NeverOverwritesOperatorChoice()
    {
        // An operator who deliberately set a different retry list (or explicitly opted out
        // with an empty list) has that choice preserved - auto-derivation only fills a gap,
        // it never clobbers an existing, deliberately-set override.
        var result = SyncTableConfig.ResolveRetryableSqlErrorsOverride(
            mergeStrategyOverride: MergeStrategy.RowByRow, existingRetryableSqlErrorsOverride: [2601], hasIncomingForeignKeys: true);

        Assert.Equal([2601], result);
    }
}
