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
/// Proves Auto mode's execution wiring — ChildDdlProvisioner actually creating the child
/// table, TVP type, and stored procedure that SmartTableSync.Scaffold otherwise only writes
/// to a file for a human to apply by hand — against a real, freshly "never manually
/// provisioned" child database. SqlArtifactGenerator's generated text is already unit-tested
/// in isolation; this is about proving the generated scripts actually execute and produce
/// working objects, not re-proving the text is correct.
///
/// Uses its own dedicated container (IClassFixture, not the shared [Collection("SqlServer")])
/// because these tests DROP real business-table DDL from Child to simulate "never manually
/// provisioned," which would wreck every other test sharing the standard collection's database.
/// </summary>
public class AutoModeProvisioningTests : IClassFixture<MsSqlTestFixture>
{
    private readonly MsSqlTestFixture _fixture;

    public AutoModeProvisioningTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ProductCatalogItemTable_AutoCreatesTableTvpTypeAndProcedureThenSyncsSuccessfully()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        await DropChildObjectsAsync(child,
            table: "ProductCatalogItem",
            procedure: "sp_SyncProductCatalogItemBatch",
            type: "ProductCatalogItemTableType");

        var provisioner = BuildProvisioner();
        var (profile, plan, wasFreshlyProvisioned) =
            await provisioner.EnsureProvisionedAsync(parent, child, "ProductCatalogItem", CancellationToken.None);

        Assert.True(wasFreshlyProvisioned);
        Assert.Equal(MergeStrategy.Tvp, plan.MergeStrategy);

        var childDdlProvisioner = BuildChildDdlProvisioner();

        // Act
        await childDdlProvisioner.EnsureChildObjectsExistAsync(child, profile, plan, CancellationToken.None);

        // Assert - the objects Scaffold would otherwise only write to a file now exist for real
        Assert.True(await TableExistsAsync(child, "ProductCatalogItem"));
        Assert.True(await TypeExistsAsync(child, "ProductCatalogItemTableType"));
        Assert.True(await ProcedureExistsAsync(child, "sp_SyncProductCatalogItemBatch"));

        // Act - prove the freshly-created objects actually work, not just exist
        var rows = await SeedDataLoader.LoadAsync(
            Path.Combine(AppContext.BaseDirectory, "SeedData", "ProductCatalogItem.json"));
        await SeedDataLoader.InsertRowsAsync(parent, "ProductCatalogItem", rows);

        var stateRepository = new SyncStateRepository();
        var lastSyncDate = await stateRepository.GetLastSyncDateAsync(child, "ProductCatalogItem", CancellationToken.None);
        var syncer = new TableSyncerFactory(new BatchLogger(), NullLoggerFactory.Instance).Create(profile, plan);

        var result = await syncer.SyncAsync(parent, child, lastSyncDate, CancellationToken.None);

        // Assert
        Assert.True(result.IsInitialSync);
        Assert.Equal(rows.Count, result.RowsSynced);
        Assert.Equal(0, result.RowsErrored);
        await using var countCmd = new SqlCommand("SELECT COUNT(*) FROM [dbo].[ProductCatalogItem]", child);
        Assert.Equal(rows.Count, (int)(await countCmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ProductAttributeValueTable_AutoCreatesTableAndProcedureThenSyncsSuccessfully()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        await SeedDataLoader.EnsureSeededAsync(parent, "Attribute", "Attribute.json", hasIdentityColumn: true);
        await SeedDataLoader.EnsureSeededAsync(child, "Attribute", "Attribute.json", hasIdentityColumn: true);

        await DropChildObjectsAsync(child,
            table: "ProductAttributeValue",
            procedure: "sp_SyncProductAttributeValue",
            type: null);

        var provisioner = BuildProvisioner();
        var (profile, plan, wasFreshlyProvisioned) =
            await provisioner.EnsureProvisionedAsync(parent, child, "ProductAttributeValue", CancellationToken.None);

        Assert.True(wasFreshlyProvisioned);
        Assert.Equal(MergeStrategy.RowByRow, plan.MergeStrategy);

        var childDdlProvisioner = BuildChildDdlProvisioner();

        // Act
        await childDdlProvisioner.EnsureChildObjectsExistAsync(child, profile, plan, CancellationToken.None);

        // Assert - proves GenerateMergeProcedureScript's other branch (scalar-parameter
        // procedure, not TVP) also executes correctly for real
        Assert.True(await TableExistsAsync(child, "ProductAttributeValue"));
        Assert.True(await ProcedureExistsAsync(child, "sp_SyncProductAttributeValue"));

        // Act - prove the freshly-created objects actually work, not just exist
        var rows = await SeedDataLoader.LoadAsync(
            Path.Combine(AppContext.BaseDirectory, "SeedData", "ProductAttributeValue.json"));
        await SeedDataLoader.InsertRowsAsync(parent, "ProductAttributeValue", rows, hasIdentityColumn: true);

        var stateRepository = new SyncStateRepository();
        var lastSyncDate = await stateRepository.GetLastSyncDateAsync(child, "ProductAttributeValue", CancellationToken.None);
        var syncer = new TableSyncerFactory(new BatchLogger(), NullLoggerFactory.Instance).Create(profile, plan);

        var result = await syncer.SyncAsync(parent, child, lastSyncDate, CancellationToken.None);

        // Assert
        Assert.True(result.IsInitialSync);
        Assert.Equal(rows.Count, result.RowsSynced);
        Assert.Equal(0, result.RowsErrored);
        await using var countCmd = new SqlCommand("SELECT COUNT(*) FROM [dbo].[ProductAttributeValue]", child);
        Assert.Equal(rows.Count, (int)(await countCmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task IdentityTvpTestTable_AutoCreatesTvpTypeAndProcedure_IncrementalSyncInsertsIdentityRowSuccessfully()
    {
        // Item 8a regression test: identity PK + no incoming FKs + small rows resolves to
        // Tvp (a real, reachable heuristic outcome none of the 3 example tables happen to
        // hit), and the generated TVP MERGE procedure must wrap identity-column inserts in
        // SET IDENTITY_INSERT ON/OFF - without that fix, this fails with SQL error 544 the
        // moment the incremental MERGE's INSERT branch fires for a row carrying an explicit
        // identity value.
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        var provisioner = BuildProvisioner();
        var (profile, plan, wasFreshlyProvisioned) =
            await provisioner.EnsureProvisionedAsync(parent, child, "IdentityTvpTestTable", CancellationToken.None);

        Assert.True(wasFreshlyProvisioned);
        Assert.Equal(MergeStrategy.Tvp, plan.MergeStrategy);

        var childDdlProvisioner = BuildChildDdlProvisioner();
        await childDdlProvisioner.EnsureChildObjectsExistAsync(child, profile, plan, CancellationToken.None);

        Assert.True(await TypeExistsAsync(child, "IdentityTvpTestTableTableType"));
        Assert.True(await ProcedureExistsAsync(child, "sp_SyncIdentityTvpTestTableBatch"));

        // Child starts empty; a non-initial lastSyncDate forces the incremental (Tvp MERGE)
        // path rather than the bulk-copy initial path, so this row's explicit identity value
        // actually goes through the generated stored procedure's INSERT branch.
        const int explicitId = 500;
        await SyncerTestHelpers.InsertIdentityTvpTestTableRowWithExplicitIdAsync(
            parent, explicitId, "Original", DateTime.UtcNow.AddSeconds(-30));

        var syncer = new TableSyncerFactory(new BatchLogger(), NullLoggerFactory.Instance).Create(profile, plan);
        var syncPoint = DateTime.UtcNow.AddMinutes(-5);

        var result = await syncer.SyncAsync(parent, child, syncPoint, CancellationToken.None);

        Assert.False(result.IsInitialSync);
        Assert.Equal(1, result.RowsSynced);
        Assert.Equal(0, result.RowsErrored);
        Assert.Equal("Original", await SyncerTestHelpers.ReadScalarStringAsync(child,
            "SELECT [Value] FROM [dbo].[IdentityTvpTestTable] WHERE [Id] = @Id",
            ("@Id", SqlDbType.Int, explicitId, 0)));
    }

    private static SyncTableProvisioner BuildProvisioner() => new(
        new SchemaReader(), new SyncPlanHeuristic(), new SyncTableConfigRepository(),
        new NoOpSyncMetricsReporter(), Microsoft.Extensions.Options.Options.Create(new SyncSettings()),
        NullLogger<SyncTableProvisioner>.Instance);

    private static ChildDdlProvisioner BuildChildDdlProvisioner() => new(
        new SchemaReader(), new SqlArtifactGenerator(), NullLogger<ChildDdlProvisioner>.Instance);

    // Simulates a child database that was never manually provisioned via SmartTableSync.Scaffold -
    // drops the procedure first (it references the table) and, when the table has a TVP merge
    // strategy, its UDT type too, so ChildDdlProvisioner's creation path (not just its
    // already-exists guard) is what actually runs.
    private static async Task DropChildObjectsAsync(SqlConnection child, string table, string procedure, string? type)
    {
        await using var dropProcedure = new SqlCommand($"DROP PROCEDURE IF EXISTS [dbo].[{procedure}]", child);
        await dropProcedure.ExecuteNonQueryAsync();

        await using var dropTable = new SqlCommand($"DROP TABLE IF EXISTS [dbo].[{table}]", child);
        await dropTable.ExecuteNonQueryAsync();

        if (type is not null)
        {
            await using var dropType = new SqlCommand($"DROP TYPE IF EXISTS [dbo].[{type}]", child);
            await dropType.ExecuteNonQueryAsync();
        }
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string tableName) =>
        await new SchemaReader().TableExistsAsync(connection, tableName, CancellationToken.None);

    private static async Task<bool> ProcedureExistsAsync(SqlConnection connection, string procedureName)
    {
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM sys.procedures WHERE name = @Name AND schema_id = SCHEMA_ID('dbo')", connection);
        cmd.Parameters.AddWithValue("@Name", procedureName);
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }

    private static async Task<bool> TypeExistsAsync(SqlConnection connection, string typeName)
    {
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM sys.types WHERE name = @Name AND schema_id = SCHEMA_ID('dbo')", connection);
        cmd.Parameters.AddWithValue("@Name", typeName);
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }
}
