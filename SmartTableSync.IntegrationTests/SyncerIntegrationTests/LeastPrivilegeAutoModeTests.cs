using Microsoft.Data.SqlClient;
using SmartTableSync.Hosting;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using static SmartTableSync.IntegrationTests.Support.PermissionsScript;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// Auto mode with exactly the grants sql/07_permissions.sql documents for it: db_ddladmin for
/// the provisioning identity, the runtime's parent grants, and on the child only the
/// schema-level runtime grant given before any synced object exists. The child starts without
/// tracking tables, synced tables, table types or procedures; the Function has to create them
/// with the provisioning identity and sync with the runtime identity. ProductAttributeValue has
/// an IDENTITY column in the parent; its child column is created without one, so the runtime
/// needs no ALTER. Uses its own container because it drops those child objects.
/// </summary>
public class LeastPrivilegeAutoModeTests : IClassFixture<LeastPrivilegeAutoModeTests.AutoModeGrants>
{
    private static readonly string[] _tables = ["ProductAttributeValue", "ProductCatalogItem", "DocumentAsset"];

    private readonly MsSqlTestFixture _sql;
    private readonly LeastPrivilegeIdentities _identities;

    public LeastPrivilegeAutoModeTests(AutoModeGrants fixture)
    {
        _sql = fixture.Sql;
        _identities = fixture.Identities;
    }

    /// <summary>The Auto mode parts of 07_permissions.sql, on a child with no SmartTableSync objects.</summary>
    public sealed class AutoModeGrants : LeastPrivilegeFixture
    {
        protected override Task PrepareDatabasesAsync() => RunOnChildAsync("""
            DROP PROCEDURE IF EXISTS [dbo].[sp_SyncProductAttributeValue];
            DROP PROCEDURE IF EXISTS [dbo].[sp_SyncProductCatalogItemBatch];
            DROP PROCEDURE IF EXISTS [dbo].[sp_SyncDocumentAsset];
            DROP TABLE IF EXISTS [dbo].[ProductAttributeValue];
            DROP TABLE IF EXISTS [dbo].[ProductCatalogItem];
            DROP TABLE IF EXISTS [dbo].[DocumentAsset];
            DROP TYPE IF EXISTS [dbo].[ProductCatalogItemTableType];
            DROP TABLE IF EXISTS [dbo].[CustomSyncState];
            DROP TABLE IF EXISTS [dbo].[SyncBatchLog];
            DROP TABLE IF EXISTS [dbo].[SyncTableConfig];
            """, "child without SmartTableSync objects");

        internal override async Task ApplyGrantsAsync(PermissionsScript script)
        {
            await RunOnChildAsync(script.For(ChildProvisioning, ChildDatabase), "07_permissions.sql: child-provisioning");
            await RunOnParentAsync(script.For(Runtime, ParentDatabase), "07_permissions.sql: runtime, parent");
            await RunOnChildAsync(script.AutoModeChildRuntimeGrants, "07_permissions.sql: Auto mode runtime, child");
        }
    }

    [Fact]
    public async Task AutoMode_CreatesEveryChildObjectThenSyncsWithTheRuntimeIdentity()
    {
        // Arrange - as an administrator: seed the parent. One large DocumentAsset row makes it a
        // row-by-row table, so both kinds of merge procedure get created.
        await using var parent = new SqlConnection(_sql.ParentConnectionString);
        await using var child = new SqlConnection(_sql.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        var seedData = Path.Combine(AppContext.BaseDirectory, "SeedData");
        await SeedDataLoader.EnsureSeededAsync(parent, "Attribute", "Attribute.json", hasIdentityColumn: true);
        await SeedDataLoader.InsertRowsAsync(parent, "ProductAttributeValue",
            await SeedDataLoader.LoadAsync(Path.Combine(seedData, "ProductAttributeValue.json")), hasIdentityColumn: true);
        await SeedDataLoader.InsertRowsAsync(parent, "ProductCatalogItem",
            await SeedDataLoader.LoadAsync(Path.Combine(seedData, "ProductCatalogItem.json")));
        await SeedDataLoader.InsertRowsAsync(parent, "DocumentAsset",
            await SeedDataLoader.LoadAsync(Path.Combine(seedData, "DocumentAsset.json")));
        await SyncerTestHelpers.EnsureLargeDocumentAssetRowSeededAsync(parent);

        // Act - first run creates the tracking tables, the child tables, the table type and the
        // procedures, then copies the rows
        var errors = await RunAutoModeAsync();

        // Assert
        Assert.Empty(errors);
        await AssertChildMatchesParentAsync(parent, child);

        Assert.Null(await new SmartTableSync.Introspection.SchemaReader().GetIdentityColumnAsync(
            child, "ProductAttributeValue", CancellationToken.None));

        // Arrange - inserts and an update for the next run; the new ProductAttributeValue row
        // gets its key from the parent's IDENTITY and is copied as a plain value
        await SyncerTestHelpers.InsertNewProductAttributeValueRowAsync(parent, attributeId: 1, slug: "auto-mode", lastUpdate: DateTime.UtcNow);
        await SyncerTestHelpers.InsertNewProductCatalogItemRowAsync(parent, "AUTO-NEW-1", "Added after the first sync", DateTime.UtcNow);
        await ExecuteAsync(parent, "UPDATE TOP (1) [dbo].[DocumentAsset] SET [Notes] = N'Changed after the first sync', [LastUpdate] = GETUTCDATE()");

        // Act
        errors = await RunAutoModeAsync();

        // Assert
        Assert.Empty(errors);
        await AssertChildMatchesParentAsync(parent, child);
    }

    [Fact]
    public async Task AutoMode_RuntimeIdentityStillHasNoDdlRights()
    {
        // The schema-level grant covers objects created later, but must not allow creating,
        // altering or dropping anything
        await using var runtime = new SqlConnection(_identities.ChildConnectionString(Runtime));
        await runtime.OpenAsync();

        await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(runtime, "CREATE TABLE [dbo].[RuntimeMustNotCreate] ([Id] INT)"));
        await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(runtime, "CREATE PROCEDURE [dbo].[RuntimeMustNotCreate] AS SELECT 1"));
    }

    [Fact]
    public async Task AutoMode_ColumnsAddedInParent_AddsThemRecreatesTheTableTypeAndRescans()
    {
        // Arrange - a table added the documented way, synced once
        const string table = "EvolvingProduct";
        await using var parent = new SqlConnection(_sql.ParentConnectionString);
        await using var child = new SqlConnection(_sql.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();
        await CreateParentTableAsync(parent, table);

        Assert.Empty(await RunAutoModeAsync(table));
        await AssertChildMatchesParentAsync(parent, child, table);

        // Act - two columns added in the parent. The nullable one gets values without touching
        // LastUpdate, so only the re-scan can bring them; the NOT NULL one gets its default.
        await ExecuteAsync(parent, $"ALTER TABLE [dbo].[{table}] ADD [Category] NVARCHAR(30) NULL, [Price] DECIMAL(10, 2) NOT NULL CONSTRAINT [DF_{table}_Price] DEFAULT (0)");
        await ExecuteAsync(parent, $"UPDATE [dbo].[{table}] SET [Category] = CONCAT(N'C', [Id] % 3)");

        var errors = await RunAutoModeAsync(table);

        // Assert
        Assert.Empty(errors);
        await AssertChildMatchesParentAsync(parent, child, table);
        var typeColumns = await new SmartTableSync.Introspection.SchemaReader().GetTableTypeColumnsAsync(child, $"{table}TableType", CancellationToken.None);
        Assert.Equal(["Id", "Name", "LastUpdate", "Category", "Price"], typeColumns.Select(c => c.Name));
    }

    [Fact]
    public async Task AutoMode_RiskyParentSchemaChange_LeavesTheChildUnchangedAndKeepsReportingIt()
    {
        // Arrange
        const string table = "RiskyProduct";
        await using var parent = new SqlConnection(_sql.ParentConnectionString);
        await using var child = new SqlConnection(_sql.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();
        await CreateParentTableAsync(parent, table);
        Assert.Empty(await RunAutoModeAsync(table));

        // Act - a widened column: applying it automatically isn't safe in general
        await ExecuteAsync(parent, $"ALTER TABLE [dbo].[{table}] ALTER COLUMN [Name] NVARCHAR(100) NOT NULL");
        var firstErrors = await RunAutoModeAsync(table);
        var secondErrors = await RunAutoModeAsync(table);

        // Assert - reported on every run (the new plan wasn't saved), and the child is untouched
        Assert.Contains(firstErrors, e => e.Contains("column Name changed", StringComparison.Ordinal));
        Assert.Contains(secondErrors, e => e.Contains("column Name changed", StringComparison.Ordinal));
        var childName = (await new SmartTableSync.Introspection.SchemaReader().GetFingerprintInputsAsync(child, table, CancellationToken.None))
            .Columns.Single(c => c.Name == "Name");
        Assert.Equal(100, childName.MaxLength);
    }

    // A new parent table with 50 rows, and the documented step for adding a table: read access
    // for the runtime identity on it in the parent. The rows were last changed an hour ago, outside
    // the Timestamp strategy's one-minute look-back, so no incremental pass reads them again.
    private static async Task CreateParentTableAsync(SqlConnection parent, string table)
    {
        await ExecuteAsync(parent, $"""
            CREATE TABLE [dbo].[{table}] (
                [Id] INT NOT NULL PRIMARY KEY,
                [Name] NVARCHAR(50) NOT NULL,
                [LastUpdate] DATETIME NOT NULL CONSTRAINT [DF_{table}_LastUpdate] DEFAULT (GETUTCDATE()));
            INSERT INTO [dbo].[{table}] ([Id], [Name], [LastUpdate])
            SELECT TOP (50) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), N'Item', DATEADD(hour, -1, GETUTCDATE()) FROM sys.all_columns;
            GRANT SELECT, VIEW DEFINITION ON [dbo].[{table}] TO [smarttablesync-runtime];
            """);
    }

    private Task<IReadOnlyList<string>> RunAutoModeAsync(params string[] tables) =>
        SyncerTestHelpers.RunOrchestratorAsync(
            new Dictionary<string, string?>
            {
                ["ParentDbConnectionString"] = _identities.ParentConnectionString(Runtime),
                ["ChildDbConnectionString"] = _identities.ChildConnectionString(Runtime),
                ["ChildProvisioningConnectionString"] = _identities.ChildConnectionString(ChildProvisioning),
            },
            new SyncSettings { Tables = tables.Length > 0 ? tables : _tables, Mode = SyncMode.Auto });

    private static async Task AssertChildMatchesParentAsync(SqlConnection parent, SqlConnection child, params string[] tables)
    {
        foreach (var table in tables.Length > 0 ? tables : _tables)
        {
            var expected = await SyncerTestHelpers.TableFingerprintAsync(parent, table);
            var actual = await SyncerTestHelpers.TableFingerprintAsync(child, table);

            Assert.True(expected == actual, $"{table}: parent {expected} but child {actual}");
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
