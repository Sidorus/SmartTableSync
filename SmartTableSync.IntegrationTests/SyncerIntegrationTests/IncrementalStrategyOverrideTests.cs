using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SmartTableSync.Configuration;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Introspection;
using SmartTableSync.Logging;
using SmartTableSync.Observability;
using SmartTableSync.Options;
using SmartTableSync.Planning;
using SmartTableSync.Scaffolding;
using SmartTableSync.State;
using SmartTableSync.Syncers;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// SmartTableSync:IncrementalStrategyOverrides — proves the per-table override actually flows
/// end-to-end through SyncTableProvisioner.EnsureProvisionedAsync into a working sync, not
/// just through the pure SyncPlanHeuristic function (already covered by SyncPlanHeuristicTests).
///
/// Each scenario uses its own dedicated table, even though all three could conceptually share
/// one - SyncTableProvisioner caches the derived plan by schema fingerprint only, not by the
/// settings passed in, so two calls against the same table name with different settings but an
/// unchanged schema would silently return whichever one ran first (the same fingerprint-match
/// fast path documented as a known limitation for TimestampColumns/AllowFullRescanFor).
/// </summary>
[Collection("SqlServer")]
public class IncrementalStrategyOverrideTests
{
    private readonly MsSqlTestFixture _fixture;

    public IncrementalStrategyOverrideTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task WithoutOverride_RowVersionColumnWinsOverLastUpdate()
    {
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        var (_, plan) = await ProvisionAsync(parent, child, "IncrementalStrategyOverrideTestTable", new SyncSettings());

        Assert.Equal(IncrementalStrategy.RowVersion, plan.IncrementalStrategy);
        Assert.Equal("RowVer", plan.IncrementalColumn);
    }

    [Fact]
    public async Task WithTimestampOverride_ForcesTimestampDespiteRowVersionColumn_AndSyncsViaLastUpdate()
    {
        const string tableName = "IncrementalStrategyOverrideTimestampTestTable";
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        var settings = new SyncSettings
        {
            IncrementalStrategyOverrides = new Dictionary<string, IncrementalStrategy> { [tableName] = IncrementalStrategy.Timestamp },
        };

        const int rowId = 1;
        var originalLastUpdate = DateTime.UtcNow.AddDays(-1);
        await SyncerTestHelpers.InsertIncrementalStrategyOverrideTimestampRowAsync(parent, rowId, "Original", originalLastUpdate);
        await SyncerTestHelpers.InsertIncrementalStrategyOverrideTimestampRowAsync(child, rowId, "Original", originalLastUpdate);

        var (profile, plan) = await ProvisionAsync(parent, child, tableName, settings);
        Assert.Equal(IncrementalStrategy.Timestamp, plan.IncrementalStrategy);
        Assert.Equal("LastUpdate", plan.IncrementalColumn);

        await BuildChildDdlProvisioner().EnsureChildObjectsExistAsync(child, profile, plan, CancellationToken.None);
        var syncer = new TableSyncerFactory(new BatchLogger(), new SyncStateRepository(), new NoOpSyncMetricsReporter(), NullLoggerFactory.Instance).Create(profile, plan);

        var syncPoint = DateTime.UtcNow.AddHours(-9);
        await SyncerTestHelpers.UpdateIncrementalStrategyOverrideTimestampValueAsync(parent, rowId, "Updated", syncPoint.AddSeconds(5));

        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsSynced >= 1);
        Assert.Equal("Updated", await SyncerTestHelpers.ReadScalarStringAsync(child,
            $"SELECT [Value] FROM [dbo].[{tableName}] WHERE [Id] = @Id",
            ("@Id", SqlDbType.Int, rowId, 0)));
    }

    [Fact]
    public async Task WithChangeTrackingColumnsNamingWrongTypedColumn_Throws()
    {
        const string tableName = "IncrementalStrategyOverrideBadColumnTestTable";
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        // "Id" is an int column - a real operator mistake, must fail loudly at provisioning
        // time rather than silently accepting it and failing every incremental cycle forever.
        var settings = new SyncSettings
        {
            TimestampColumns = new Dictionary<string, string> { [tableName] = "Id" },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ProvisionAsync(parent, child, tableName, settings));
        Assert.Contains("Id", ex.Message);
    }

    private static async Task<(TableProfile Profile, SyncPlan Plan)> ProvisionAsync(
        SqlConnection parent, SqlConnection child, string tableName, SyncSettings settings)
    {
        var provisioner = new SyncTableProvisioner(
            new SchemaReader(), new SyncPlanHeuristic(), new SyncTableConfigRepository(),
            new NoOpSyncMetricsReporter(), Microsoft.Extensions.Options.Options.Create(settings),
            NullLogger<SyncTableProvisioner>.Instance);

        var (profile, plan, _) = await provisioner.EnsureProvisionedAsync(parent, child, tableName, CancellationToken.None);
        return (profile, plan);
    }

    private static ChildDdlProvisioner BuildChildDdlProvisioner() => new(
        new SchemaReader(), new SqlArtifactGenerator(), NullLogger<ChildDdlProvisioner>.Instance);
}
