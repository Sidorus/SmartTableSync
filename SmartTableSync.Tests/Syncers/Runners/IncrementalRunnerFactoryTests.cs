using Microsoft.Extensions.Logging.Abstractions;
using SmartTableSync.Introspection;
using SmartTableSync.Planning;
using SmartTableSync.State;
using SmartTableSync.Syncers.MergeStrategies;
using SmartTableSync.Syncers.Runners;
using SmartTableSync.Syncers.Sql;
using Xunit;

namespace SmartTableSync.Tests.Syncers.Runners;

public class IncrementalRunnerFactoryTests
{
    private static readonly TableProfile Profile = new(
        TableName: "TestTable",
        Columns: [],
        PrimaryKeyColumns: ["Id"],
        IdentityColumn: null,
        IndexedLobColumns: [],
        HasIncomingForeignKeys: false,
        RowCount: 100,
        AvgRowSizeBytes: 100,
        MaxLobColumnSizeBytes: new Dictionary<string, long>());

    private static SyncPlan CreatePlan(IncrementalStrategy strategy) => new(
        TableName: "TestTable",
        MergeStrategy: MergeStrategy.Tvp,
        BatchSize: 100,
        BulkCopyOptions: Microsoft.Data.SqlClient.SqlBulkCopyOptions.Default,
        BulkCopyTimeoutSeconds: 120,
        RetryableSqlErrorNumbers: [],
        TvpTypeName: "TestTableTableType",
        ChangeTrackingColumn: strategy == IncrementalStrategy.Timestamp ? "LastUpdate" : null,
        IncrementalStrategy: strategy);

    private static TableSyncCommandFactory CommandFactory(SyncPlan plan) => new(Profile, plan);

    private static IBatchMergeStrategy MergeStrategyFor(SyncPlan plan) =>
        BatchMergeStrategyFactory.Create(Profile, plan, NullLogger.Instance);

    private static readonly ISyncStateRepository SyncState = new SyncStateRepository();

    [Fact]
    public void Create_TimestampStrategy_ReturnsIncrementalSyncRunner()
    {
        var plan = CreatePlan(IncrementalStrategy.Timestamp);

        var runner = IncrementalRunnerFactory.Create(Profile, plan, CommandFactory(plan), MergeStrategyFor(plan), SyncState, NullLogger.Instance);

        Assert.IsType<IncrementalSyncRunner>(runner);
    }

    [Fact]
    public void Create_FullRescanStrategy_ReturnsFullRescanSyncRunner()
    {
        var plan = CreatePlan(IncrementalStrategy.FullRescan);

        var runner = IncrementalRunnerFactory.Create(Profile, plan, CommandFactory(plan), MergeStrategyFor(plan), SyncState, NullLogger.Instance);

        Assert.IsType<FullRescanSyncRunner>(runner);
    }

    [Fact]
    public void Create_RowVersionStrategy_ReturnsRowVersionSyncRunner()
    {
        var plan = CreatePlan(IncrementalStrategy.RowVersion);

        var runner = IncrementalRunnerFactory.Create(Profile, plan, CommandFactory(plan), MergeStrategyFor(plan), SyncState, NullLogger.Instance);

        Assert.IsType<RowVersionSyncRunner>(runner);
    }
}
