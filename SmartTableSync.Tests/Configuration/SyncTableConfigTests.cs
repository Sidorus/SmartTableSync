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
}
