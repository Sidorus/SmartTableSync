using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SmartTableSync.Configuration;
using SmartTableSync.Infrastructure;
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
/// Item 1 — configurable change-tracking column. Proves the three resolutions
/// SyncPlanHeuristic/SyncTableProvisioner can reach for a table's incremental strategy,
/// against real introspection and a real sync run, not just the unit-level heuristic math:
///   - a table with the default "LastUpdate" column resolves Timestamp with zero config
///   - a table whose tracking column is named something else resolves Timestamp once
///     configured via SmartTableSync:ChangeTrackingColumns
///   - a table with no matching column and no config resolves FullRescan, and an
///     incremental run still picks up changes despite ignoring lastSyncDate entirely
///
/// The two test-only tables have no hand-authored TVP/stored procedure (unlike the 3 real
/// example tables also used in this suite), so these tests provision them via the Auto-mode
/// path (SyncTableProvisioner + ChildDdlProvisioner), the same construction AutoModeProvisioningTests
/// uses, rather than SyncerTestHelpers.BuildConfigurableSyncerAsync's Manual-mode assumption.
/// </summary>
[Collection("SqlServer")]
public class IncrementalStrategyTests
{
    private readonly MsSqlTestFixture _fixture;

    public IncrementalStrategyTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task DefaultLastUpdateColumn_ResolvesTimestampStrategyWithNoConfig()
    {
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        var (_, plan) = await SyncerTestHelpers.BuildConfigurableSyncerAsync(parent, child, "ProductCatalogItem");

        Assert.Equal(IncrementalStrategy.Timestamp, plan.IncrementalStrategy);
        Assert.Equal("LastUpdate", plan.ChangeTrackingColumn);
    }

    [Fact]
    public async Task DifferentlyNamedColumn_ResolvesTimestampStrategyOnceConfigured_AndSyncsUpdates()
    {
        const string tableName = "IncrementalStrategyConfiguredColumnTestTable";
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        var settings = new SyncSettings
        {
            ChangeTrackingColumns = new Dictionary<string, string> { [tableName] = "ModifiedAt" },
        };

        const int rowId = 1;
        var originalModifiedAt = DateTime.UtcNow.AddDays(-1);
        await SyncerTestHelpers.InsertIncrementalStrategyConfiguredColumnRowAsync(parent, rowId, "Original", originalModifiedAt);
        await SyncerTestHelpers.InsertIncrementalStrategyConfiguredColumnRowAsync(child, rowId, "Original", originalModifiedAt);

        var (profile, plan) = await ProvisionAsync(parent, child, tableName, settings);
        Assert.Equal(IncrementalStrategy.Timestamp, plan.IncrementalStrategy);
        Assert.Equal("ModifiedAt", plan.ChangeTrackingColumn);

        await BuildChildDdlProvisioner().EnsureChildObjectsExistAsync(child, profile, plan, CancellationToken.None);
        var syncer = new TableSyncerFactory(new BatchLogger(), new SyncStateRepository(), new NoOpSyncMetricsReporter(), NullLoggerFactory.Instance).Create(profile, plan);

        var syncPoint = DateTime.UtcNow.AddHours(-9);
        await SyncerTestHelpers.UpdateIncrementalStrategyConfiguredColumnValueAsync(parent, rowId, "Updated", syncPoint.AddSeconds(5));

        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        Assert.False(result.IsInitialSync);
        Assert.True(result.RowsSynced >= 1);
        Assert.Equal("Updated", await SyncerTestHelpers.ReadScalarStringAsync(child,
            "SELECT [Value] FROM [dbo].[IncrementalStrategyConfiguredColumnTestTable] WHERE [Id] = @Id",
            ("@Id", SqlDbType.Int, rowId, 0)));
    }

    [Fact]
    public async Task NoMatchingColumnAndNoConfig_ResolvesFullRescan_AndSyncsChangesRegardlessOfLastSyncDate()
    {
        const string tableName = "IncrementalStrategyFullRescanTestTable";
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        const int unchangedId = 1;
        const int newParentId = 2;

        // Already-synced row, present in both — proves an unchanged row surviving a
        // re-merge is harmless, not just that new/changed rows get picked up.
        await SyncerTestHelpers.InsertIncrementalStrategyFullRescanRowAsync(parent, unchangedId, "Unchanged");
        await SyncerTestHelpers.InsertIncrementalStrategyFullRescanRowAsync(child, unchangedId, "Unchanged");

        var (profile, plan) = await ProvisionAsync(parent, child, tableName);
        Assert.Equal(IncrementalStrategy.FullRescan, plan.IncrementalStrategy);
        Assert.Null(plan.ChangeTrackingColumn);

        await BuildChildDdlProvisioner().EnsureChildObjectsExistAsync(child, profile, plan, CancellationToken.None);
        var syncer = new TableSyncerFactory(new BatchLogger(), new SyncStateRepository(), new NoOpSyncMetricsReporter(), NullLoggerFactory.Instance).Create(profile, plan);

        // A row that exists only in Parent (never synced) and an update to an existing row -
        // neither has any change-tracking column to be detected by, so only a full re-scan
        // picks them up. lastSyncDate is deliberately far in the future, well past either
        // change - if this were IncrementalSyncRunner's windowed query, both would be missed.
        await SyncerTestHelpers.InsertIncrementalStrategyFullRescanRowAsync(parent, newParentId, "NewInParentOnly");
        await SyncerTestHelpers.UpdateIncrementalStrategyFullRescanValueAsync(parent, unchangedId, "Changed");

        var farFutureSyncPoint = DateTime.UtcNow.AddYears(1);
        var result = await syncer.SyncAsync(parent, child, farFutureSyncPoint, CancellationToken.None);

        Assert.False(result.IsInitialSync);
        Assert.Equal(0, result.RowsErrored);

        await using var countCmd = new SqlCommand(
            "SELECT COUNT(*) FROM [dbo].[IncrementalStrategyFullRescanTestTable] WHERE [Id] = @Id",
            child);
        countCmd.AddParam("@Id", SqlDbType.Int, newParentId);
        Assert.Equal(1, (int)(await countCmd.ExecuteScalarAsync())!);

        Assert.Equal("Changed", await SyncerTestHelpers.ReadScalarStringAsync(child,
            "SELECT [Value] FROM [dbo].[IncrementalStrategyFullRescanTestTable] WHERE [Id] = @Id",
            ("@Id", SqlDbType.Int, unchangedId, 0)));
    }

    private static async Task<(TableProfile Profile, SyncPlan Plan)> ProvisionAsync(
        SqlConnection parent, SqlConnection child, string tableName, SyncSettings? settings = null)
    {
        var provisioner = new SyncTableProvisioner(
            new SchemaReader(), new SyncPlanHeuristic(), new SyncTableConfigRepository(),
            new NoOpSyncMetricsReporter(), Microsoft.Extensions.Options.Options.Create(settings ?? new SyncSettings()),
            NullLogger<SyncTableProvisioner>.Instance);

        var (profile, plan, _) = await provisioner.EnsureProvisionedAsync(parent, child, tableName, CancellationToken.None);
        return (profile, plan);
    }

    private static ChildDdlProvisioner BuildChildDdlProvisioner() => new(
        new SchemaReader(), new SqlArtifactGenerator(), NullLogger<ChildDdlProvisioner>.Instance);
}
