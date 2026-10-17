using Microsoft.Extensions.Logging.Abstractions;
using SmartTableSync.Introspection;
using SmartTableSync.Observability;
using SmartTableSync.Planning;
using SmartTableSync.State;
using SmartTableSync.Strategies;
using SmartTableSync.Syncers.MergeStrategies;
using SmartTableSync.Syncers.Runners;
using SmartTableSync.Syncers.Sql;
using Xunit;

namespace SmartTableSync.Tests.Strategies;

public class IncrementalStrategiesTests
{
    private static readonly TableProfile Profile = new(
        TableName: "TestTable",
        Columns: [],
        PrimaryKeyColumns: ["Id"],
        IdentityColumn: null,
        IndexedLobColumns: [],
        ReferencesOtherTables: false,
        RowCount: 100,
        AvgRowSizeBytes: 100,
        MaxLobColumnSizeBytes: new Dictionary<string, long>());

    private static RunnerServices Services(IncrementalStrategy kind)
    {
        var plan = new SyncPlan(
            "TestTable", MergeStrategy.Tvp, 100, Microsoft.Data.SqlClient.SqlBulkCopyOptions.Default, 120, [],
            "TestTableTableType", IncrementalColumn: kind == IncrementalStrategy.Timestamp ? "LastUpdate" : null,
            IncrementalStrategy: kind);

        return new RunnerServices(
            Profile, plan, new TableSyncCommandFactory(Profile, plan),
            BatchMergeStrategyFactory.Create(Profile, plan, NullLogger.Instance),
            new SyncStateRepository(), new NoOpSyncMetricsReporter(), NullLogger.Instance);
    }

    [Fact]
    public void All_RegistersEveryStrategyExactlyOnce()
    {
        Assert.Equal(
            Enum.GetValues<IncrementalStrategy>().OrderBy(k => k),
            IncrementalStrategies.All.Select(s => s.Kind).OrderBy(k => k));
    }

    [Fact]
    public void All_IsInAutoSelectionPriorityOrder()
    {
        Assert.Equal(
            [IncrementalStrategy.SqlChangeTracking, IncrementalStrategy.RowVersion, IncrementalStrategy.Timestamp, IncrementalStrategy.FullRescan],
            IncrementalStrategies.All.Select(s => s.Kind));
    }

    [Theory]
    [InlineData(IncrementalStrategy.Timestamp, typeof(TimestampSyncRunner))]
    [InlineData(IncrementalStrategy.FullRescan, typeof(FullRescanSyncRunner))]
    [InlineData(IncrementalStrategy.RowVersion, typeof(RowVersionSyncRunner))]
    [InlineData(IncrementalStrategy.SqlChangeTracking, typeof(ChangeTrackingSyncRunner))]
    public void For_CreatesTheStrategysRunner(IncrementalStrategy kind, Type expectedRunner)
    {
        var runner = IncrementalStrategies.For(kind).CreateRunner(Services(kind));

        Assert.IsType(expectedRunner, runner);
    }

    [Theory]
    [InlineData(IncrementalStrategy.Timestamp, false)]
    [InlineData(IncrementalStrategy.FullRescan, false)]
    [InlineData(IncrementalStrategy.RowVersion, true)]
    [InlineData(IncrementalStrategy.SqlChangeTracking, true)]
    public void Baseline_IsDefinedOnlyForStrategiesWithTheirOwnPosition(IncrementalStrategy kind, bool hasBaseline)
    {
        Assert.Equal(hasBaseline, IncrementalStrategies.For(kind).Baseline is not null);
    }

    [Theory]
    [InlineData(IncrementalStrategy.RowVersion, "0x00000000000007D1", true)]
    [InlineData(IncrementalStrategy.RowVersion, "CT:42", false)]
    [InlineData(IncrementalStrategy.SqlChangeTracking, "CT:42", true)]
    [InlineData(IncrementalStrategy.SqlChangeTracking, "0x00000000000007D1", false)]
    [InlineData(IncrementalStrategy.SqlChangeTracking, null, false)]
    public void Baseline_RecognizesOnlyItsOwnPositionFormat(IncrementalStrategy kind, string? storedPosition, bool expected)
    {
        Assert.Equal(expected, IncrementalStrategies.For(kind).Baseline!.IsOwnPosition(storedPosition));
    }
}
