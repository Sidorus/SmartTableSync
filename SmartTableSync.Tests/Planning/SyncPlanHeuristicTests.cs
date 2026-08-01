using Microsoft.Data.SqlClient;
using SmartTableSync.Introspection;
using SmartTableSync.Planning;
using Xunit;

namespace SmartTableSync.Tests.Planning;

public class SyncPlanHeuristicTests
{
    private static readonly PlanHeuristicOptions DefaultOptions = new();
    private readonly SyncPlanHeuristic _heuristic = new();

    private static TableProfile CreateProfile(
        string tableName = "TestTable",
        string? identityColumn = null,
        bool hasIncomingForeignKeys = false,
        long avgRowSizeBytes = 500,
        long rowCount = 1000,
        IReadOnlyDictionary<string, long>? maxLobColumnSizeBytes = null) =>
        new(
            TableName: tableName,
            Columns: [],
            PrimaryKeyColumns: ["Id"],
            IdentityColumn: identityColumn,
            IndexedLobColumns: [],
            HasIncomingForeignKeys: hasIncomingForeignKeys,
            RowCount: rowCount,
            AvgRowSizeBytes: avgRowSizeBytes,
            MaxLobColumnSizeBytes: maxLobColumnSizeBytes ?? new Dictionary<string, long>());

    [Fact]
    public void DerivePlan_WithIncomingForeignKeys_ChoosesRowByRowAndFlags547AsRetryable()
    {
        // Reproduces ProductAttributeValue's actual reason for row-by-row: FK timing
        // races against Data Sync's own delivery of the referenced Attribute table.
        var profile = CreateProfile(hasIncomingForeignKeys: true);

        var plan = _heuristic.DerivePlan(profile, DefaultOptions);

        Assert.Equal(MergeStrategy.RowByRow, plan.MergeStrategy);
        Assert.Contains(547, plan.RetryableSqlErrorNumbers);
    }

    [Fact]
    public void DerivePlan_WithLargeMaxRowSizeAndNoForeignKeys_ChoosesRowByRow()
    {
        // Regression test for the exact gap identified during planning: DocumentAsset is
        // row-by-row for large-row isolation, NOT foreign-key timing. A heuristic keyed
        // only on FK presence would wrongly pick Tvp for a table shaped like this.
        var maxLobSizes = new Dictionary<string, long> { ["FileContent"] = 52_000_000 };
        var profile = CreateProfile(hasIncomingForeignKeys: false, maxLobColumnSizeBytes: maxLobSizes);

        var plan = _heuristic.DerivePlan(profile, DefaultOptions);

        Assert.Equal(MergeStrategy.RowByRow, plan.MergeStrategy);
        // No FK timing race here, so nothing should be flagged retryable — a RowByRow
        // choice driven purely by row size has no FK race to retry around.
        Assert.Empty(plan.RetryableSqlErrorNumbers);
    }

    [Fact]
    public void DerivePlan_WithNoForeignKeysAndSmallRows_ChoosesTvp()
    {
        // ProductCatalogItem's actual shape: no FK issues, no oversized rows.
        var profile = CreateProfile(hasIncomingForeignKeys: false, avgRowSizeBytes: 2_000);

        var plan = _heuristic.DerivePlan(profile, DefaultOptions);

        Assert.Equal(MergeStrategy.Tvp, plan.MergeStrategy);
        Assert.Equal($"{profile.TableName}TableType", plan.TvpTypeName);
    }

    [Fact]
    public void DerivePlan_WithIdentityColumn_UsesKeepIdentity()
    {
        var profile = CreateProfile(identityColumn: "Id");

        var plan = _heuristic.DerivePlan(profile, DefaultOptions);

        Assert.Equal(SqlBulkCopyOptions.KeepIdentity, plan.BulkCopyOptions);
    }

    [Fact]
    public void DerivePlan_WithoutIdentityColumn_UsesDefaultBulkCopyOptions()
    {
        var profile = CreateProfile(identityColumn: null);

        var plan = _heuristic.DerivePlan(profile, DefaultOptions);

        Assert.Equal(SqlBulkCopyOptions.Default, plan.BulkCopyOptions);
    }

    [Fact]
    public void DerivePlan_LargeMaxRowSize_ProducesSmallBatchAndLongerTimeoutThanBaseline()
    {
        var smallRowProfile = CreateProfile(avgRowSizeBytes: 500);
        var largeRowProfile = CreateProfile(maxLobColumnSizeBytes: new Dictionary<string, long> { ["Data"] = 52_000_000 });

        var smallPlan = _heuristic.DerivePlan(smallRowProfile, DefaultOptions);
        var largePlan = _heuristic.DerivePlan(largeRowProfile, DefaultOptions);

        Assert.True(largePlan.BatchSize < smallPlan.BatchSize);
        Assert.Equal(DefaultOptions.MinBatchSize, largePlan.BatchSize);
        Assert.True(largePlan.BulkCopyTimeoutSeconds > smallPlan.BulkCopyTimeoutSeconds);
    }

    [Fact]
    public void DerivePlan_TimeoutNeverGoesBelowBaseTimeoutSeconds()
    {
        var profile = CreateProfile(avgRowSizeBytes: 1);

        var plan = _heuristic.DerivePlan(profile, DefaultOptions);

        Assert.True(plan.BulkCopyTimeoutSeconds >= DefaultOptions.BaseTimeoutSeconds);
    }

    [Fact]
    public void DerivePlan_BatchSizeNeverExceedsMaxBatchSize()
    {
        // Tiny rows would otherwise produce an enormous raw batch size.
        var profile = CreateProfile(avgRowSizeBytes: 1);

        var plan = _heuristic.DerivePlan(profile, DefaultOptions);

        Assert.True(plan.BatchSize <= DefaultOptions.MaxBatchSize);
    }

    [Fact]
    public void DerivePlan_BatchSizeNeverBelowMinBatchSize()
    {
        var profile = CreateProfile(maxLobColumnSizeBytes: new Dictionary<string, long> { ["Data"] = 500_000_000 });

        var plan = _heuristic.DerivePlan(profile, DefaultOptions);

        Assert.True(plan.BatchSize >= DefaultOptions.MinBatchSize);
    }

    [Fact]
    public void DerivePlan_WithChangeTrackingColumn_ChoosesTimestampStrategy()
    {
        var profile = CreateProfile();

        var plan = _heuristic.DerivePlan(profile, DefaultOptions, changeTrackingColumn: "LastUpdate");

        Assert.Equal(IncrementalStrategy.Timestamp, plan.IncrementalStrategy);
        Assert.Equal("LastUpdate", plan.ChangeTrackingColumn);
    }

    [Fact]
    public void DerivePlan_NoChangeTrackingColumnAndSmallTable_FallsBackToFullRescan()
    {
        var profile = CreateProfile(rowCount: 1_000);

        var plan = _heuristic.DerivePlan(profile, DefaultOptions, changeTrackingColumn: null);

        Assert.Equal(IncrementalStrategy.FullRescan, plan.IncrementalStrategy);
        Assert.Null(plan.ChangeTrackingColumn);
    }

    [Fact]
    public void DerivePlan_NoChangeTrackingColumnAndLargeTableWithoutAllowFullRescan_Throws()
    {
        var profile = CreateProfile(rowCount: DefaultOptions.FullRescanRowCountThreshold + 1);

        Assert.Throws<InvalidOperationException>(
            () => _heuristic.DerivePlan(profile, DefaultOptions, changeTrackingColumn: null, allowFullRescan: false));
    }

    [Fact]
    public void DerivePlan_NoChangeTrackingColumnAndLargeTableWithAllowFullRescan_ChoosesFullRescan()
    {
        var profile = CreateProfile(rowCount: DefaultOptions.FullRescanRowCountThreshold + 1);

        var plan = _heuristic.DerivePlan(profile, DefaultOptions, changeTrackingColumn: null, allowFullRescan: true);

        Assert.Equal(IncrementalStrategy.FullRescan, plan.IncrementalStrategy);
    }

    [Fact]
    public void DerivePlan_WithRowVersionColumn_ChoosesRowVersionStrategy()
    {
        var profile = CreateProfile();

        var plan = _heuristic.DerivePlan(profile, DefaultOptions, rowVersionColumn: "RowVer");

        Assert.Equal(IncrementalStrategy.RowVersion, plan.IncrementalStrategy);
        Assert.Equal("RowVer", plan.ChangeTrackingColumn);
    }

    [Fact]
    public void DerivePlan_RowVersionColumnTakesPriorityOverChangeTrackingColumn()
    {
        // Detecting a rowversion-typed column is a fact, not a guess - it wins even when a
        // configured/default datetime column also exists on the same table.
        var profile = CreateProfile();

        var plan = _heuristic.DerivePlan(profile, DefaultOptions, changeTrackingColumn: "LastUpdate", rowVersionColumn: "RowVer");

        Assert.Equal(IncrementalStrategy.RowVersion, plan.IncrementalStrategy);
        Assert.Equal("RowVer", plan.ChangeTrackingColumn);
    }

    [Fact]
    public void DerivePlan_RowVersionColumnTakesPriorityOverFullRescanForLargeTables()
    {
        // Even a large table with no configured datetime column and no AllowFullRescanFor
        // opt-in should resolve RowVersion (not throw) once a rowversion column is detected.
        var profile = CreateProfile(rowCount: DefaultOptions.FullRescanRowCountThreshold + 1);

        var plan = _heuristic.DerivePlan(profile, DefaultOptions, changeTrackingColumn: null, allowFullRescan: false, rowVersionColumn: "RowVer");

        Assert.Equal(IncrementalStrategy.RowVersion, plan.IncrementalStrategy);
    }

    [Fact]
    public void DerivePlan_StrategyOverrideTimestamp_WinsOverDetectedRowVersionColumn()
    {
        // The whole reason this override exists: an operator wants Timestamp even though a
        // rowversion column - which would otherwise always win - is also present.
        var profile = CreateProfile();

        var plan = _heuristic.DerivePlan(
            profile, DefaultOptions, changeTrackingColumn: "LastUpdate", rowVersionColumn: "RowVer",
            strategyOverride: IncrementalStrategy.Timestamp);

        Assert.Equal(IncrementalStrategy.Timestamp, plan.IncrementalStrategy);
        Assert.Equal("LastUpdate", plan.ChangeTrackingColumn);
    }

    [Fact]
    public void DerivePlan_StrategyOverrideFullRescan_WinsOverDetectedRowVersionColumn()
    {
        var profile = CreateProfile();

        var plan = _heuristic.DerivePlan(
            profile, DefaultOptions, rowVersionColumn: "RowVer", strategyOverride: IncrementalStrategy.FullRescan);

        Assert.Equal(IncrementalStrategy.FullRescan, plan.IncrementalStrategy);
    }

    [Fact]
    public void DerivePlan_StrategyOverrideRowVersion_WithNoRowVersionColumn_Throws()
    {
        var profile = CreateProfile();

        Assert.Throws<InvalidOperationException>(() => _heuristic.DerivePlan(
            profile, DefaultOptions, changeTrackingColumn: "LastUpdate", rowVersionColumn: null,
            strategyOverride: IncrementalStrategy.RowVersion));
    }

    [Fact]
    public void DerivePlan_StrategyOverrideTimestamp_WithNoChangeTrackingColumn_Throws()
    {
        var profile = CreateProfile();

        Assert.Throws<InvalidOperationException>(() => _heuristic.DerivePlan(
            profile, DefaultOptions, changeTrackingColumn: null, rowVersionColumn: null,
            strategyOverride: IncrementalStrategy.Timestamp));
    }

    [Fact]
    public void DerivePlan_StrategyOverrideFullRescan_NeedsNoColumnAtAll()
    {
        var profile = CreateProfile();

        var plan = _heuristic.DerivePlan(
            profile, DefaultOptions, changeTrackingColumn: null, rowVersionColumn: null,
            strategyOverride: IncrementalStrategy.FullRescan);

        Assert.Equal(IncrementalStrategy.FullRescan, plan.IncrementalStrategy);
    }
}
