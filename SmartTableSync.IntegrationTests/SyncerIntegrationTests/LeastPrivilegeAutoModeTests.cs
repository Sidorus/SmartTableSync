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
        var typeColumns = await new SmartTableSync.Introspection.SchemaReader().GetTableTypeColumnsAsync(child, table, $"{table}TableType", CancellationToken.None);
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

    [Fact]
    public async Task AutoMode_SameTableNameInTwoSchemas_SyncsEachIntoItsOwnSchema()
    {
        // Arrange - [dbo].[Order] and [sales].[Order] with different rows. The documented step for
        // a schema other than dbo in Auto mode: an administrator creates it in the child and gives
        // the runtime identity the same schema-level grant it has on dbo.
        await using var parent = new SqlConnection(_sql.ParentConnectionString);
        await using var child = new SqlConnection(_sql.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        await ExecuteAsync(parent, "CREATE SCHEMA [sales]");
        await CreateParentTableAsync(parent, "Order");
        await CreateParentTableAsync(parent, "sales.Order");
        await ExecuteAsync(parent, "UPDATE [sales].[Order] SET [Name] = N'Sales item' WHERE [Id] <= 10; DELETE FROM [sales].[Order] WHERE [Id] > 40");

        await ExecuteAsync(child, "CREATE SCHEMA [sales]");
        await ExecuteAsync(child, "GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[sales] TO [smarttablesync-runtime]");

        // Act
        var errors = await RunAutoModeAsync("Order", "sales.Order");

        // Assert - each child table matches its own parent table, and they differ from each other
        Assert.Empty(errors);
        await AssertChildMatchesParentAsync(parent, child, "Order", "sales.Order");
        Assert.NotEqual(
            await SyncerTestHelpers.TableFingerprintAsync(child, "Order"),
            await SyncerTestHelpers.TableFingerprintAsync(child, "sales.Order"));
        Assert.Equal(1, await ScalarAsync(child, "SELECT COUNT(*) FROM sys.procedures WHERE name = 'sp_SyncOrderBatch' AND schema_id = SCHEMA_ID('sales')"));
        Assert.Equal(1, await ScalarAsync(child, "SELECT COUNT(*) FROM sys.table_types WHERE name = 'OrderTableType' AND schema_id = SCHEMA_ID('sales')"));
        Assert.Equal(1, await ScalarAsync(child, "SELECT COUNT(*) FROM [dbo].[CustomSyncState] WHERE [TableName] = N'sales.Order'"));

        // Arrange - a change in the sales table only
        await ExecuteAsync(parent, "UPDATE [sales].[Order] SET [Name] = N'Changed', [LastUpdate] = GETUTCDATE() WHERE [Id] = 1");
        await ExecuteAsync(parent, "INSERT INTO [sales].[Order] ([Id], [Name], [LastUpdate]) VALUES (1000, N'New', GETUTCDATE())");

        // Act
        errors = await RunAutoModeAsync("Order", "sales.Order");

        // Assert
        Assert.Empty(errors);
        await AssertChildMatchesParentAsync(parent, child, "Order", "sales.Order");
    }

    [Fact]
    public async Task AutoMode_TableWithoutPrimaryKey_SyncsByItsBestUniqueIndex()
    {
        // Arrange - a heap whose unique indexes compete: one on a nullable column, a filtered one,
        // a disabled one, and two usable ones; the one with fewer key columns must win. Its key
        // order (Region, Code) differs from the column order.
        const string table = "RegionalCode";
        await using var parent = new SqlConnection(_sql.ParentConnectionString);
        await using var child = new SqlConnection(_sql.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();
        await CreateParentHeapAsync(parent, table);
        await ExecuteAsync(parent, $"""
            CREATE UNIQUE INDEX [UX_{table}_Nullable] ON [dbo].[{table}] ([Note]);
            CREATE UNIQUE INDEX [UX_{table}_Filtered] ON [dbo].[{table}] ([Seq]) WHERE [Seq] > 0;
            CREATE UNIQUE INDEX [UX_{table}_Disabled] ON [dbo].[{table}] ([Seq]);
            ALTER INDEX [UX_{table}_Disabled] ON [dbo].[{table}] DISABLE;
            CREATE UNIQUE INDEX [UX_{table}_Wide] ON [dbo].[{table}] ([Region], [Code], [Seq]);
            CREATE UNIQUE INDEX [UX_{table}_Best] ON [dbo].[{table}] ([Region], [Code]) INCLUDE ([Name]);
            """);

        // Act
        var errors = await RunAutoModeAsync(table);

        // Assert - the child got that index's columns as its PRIMARY KEY, and the rows
        Assert.Empty(errors);
        await AssertChildMatchesParentAsync(parent, child, table);
        Assert.Equal(["Region", "Code"], await ChildPrimaryKeyAsync(child, table));

        // Act - an update, an insert and a delete, matched and reconciled by that key
        await ChangeParentRowsAsync(parent, table);
        errors = await RunAutoModeAsync(table);

        // Assert
        Assert.Empty(errors);
        await AssertChildMatchesParentAsync(parent, child, table);
    }

    [Fact]
    public async Task AutoMode_TableWithConfiguredKeyColumns_SyncsByThem()
    {
        // Arrange - a heap with no unique index at all; the key is named in the settings
        const string table = "ConfiguredKeyCode";
        await using var parent = new SqlConnection(_sql.ParentConnectionString);
        await using var child = new SqlConnection(_sql.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();
        await CreateParentHeapAsync(parent, table);
        var keyColumns = new Dictionary<string, IReadOnlyList<string>> { [table] = ["Region", "Code"] };

        // Act
        var errors = await RunAutoModeAsync(keyColumns, table);

        // Assert
        Assert.Empty(errors);
        await AssertChildMatchesParentAsync(parent, child, table);
        Assert.Equal(["Region", "Code"], await ChildPrimaryKeyAsync(child, table));

        // Act
        await ChangeParentRowsAsync(parent, table);
        errors = await RunAutoModeAsync(keyColumns, table);

        // Assert
        Assert.Empty(errors);
        await AssertChildMatchesParentAsync(parent, child, table);
    }

    [Fact]
    public async Task AutoMode_ConfiguredKeyColumnsWithDuplicateValues_FailsTheTableBeforeCreatingIt()
    {
        // Arrange - Region alone repeats in the data
        const string table = "DuplicateKeyCode";
        await using var parent = new SqlConnection(_sql.ParentConnectionString);
        await using var child = new SqlConnection(_sql.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();
        await CreateParentHeapAsync(parent, table);

        // Act
        var errors = await RunAutoModeAsync(new Dictionary<string, IReadOnlyList<string>> { [table] = ["Region"] }, table);

        // Assert - a clear message, and nothing created in the child
        Assert.Contains(errors, e => e.Contains("don't identify each row", StringComparison.Ordinal));
        Assert.Equal(0, await ScalarAsync(child, $"SELECT COUNT(*) FROM sys.tables WHERE name = '{table}'"));
    }

    [Fact]
    public async Task AutoMode_TableWithoutAnyKey_FailsNamingTheKeyColumnsSetting()
    {
        // Arrange
        const string table = "KeylessCode";
        await using var parent = new SqlConnection(_sql.ParentConnectionString);
        await using var child = new SqlConnection(_sql.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();
        await CreateParentHeapAsync(parent, table);

        // Act
        var errors = await RunAutoModeAsync(table);

        // Assert
        Assert.Contains(errors, e => e.Contains($"SmartTableSync:KeyColumns:{table}", StringComparison.Ordinal));
        Assert.Equal(0, await ScalarAsync(child, $"SELECT COUNT(*) FROM sys.tables WHERE name = '{table}'"));
    }

    [Fact]
    public async Task ProvisioningIdentity_CanCreateAMissingChildSchema()
    {
        // The generated CREATE TABLE script creates the table's schema when the child lacks it;
        // db_ddladmin, the provisioning identity's only right, must be enough for that.
        await using var child = new SqlConnection(_sql.ChildConnectionString);
        await child.OpenAsync();

        var profile = new SmartTableSync.Introspection.TableProfile(
            TableName: "archive.Note",
            Columns: [new("Id", "int", 4, false), new("Text", "nvarchar", 200, true)],
            PrimaryKeyColumns: ["Id"],
            IdentityColumn: null,
            IndexedLobColumns: [],
            ReferencesOtherTables: false,
            RowCount: 0,
            AvgRowSizeBytes: 0,
            MaxLobColumnSizeBytes: new Dictionary<string, long>());

        await MsSqlTestFixture.RunGeneratedScriptAsync(
            _identities.ChildConnectionString(ChildProvisioning),
            new SmartTableSync.Scaffolding.SqlArtifactGenerator().GenerateCreateTableScript(profile),
            "archive.Note as the provisioning identity");

        Assert.Equal(1, await ScalarAsync(child, "SELECT COUNT(*) FROM sys.tables WHERE name = 'Note' AND schema_id = SCHEMA_ID('archive')"));
    }

    [Fact]
    public async Task AutoMode_DecimalColumn_ArrivesWithExactValuesAndTheParentsPrecision()
    {
        // Arrange - decimal(10,2) values with cents: a child created as decimal(18,0) would round them
        const string table = "PricedProduct";
        await using var parent = new SqlConnection(_sql.ParentConnectionString);
        await using var child = new SqlConnection(_sql.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();
        await CreatePricedParentTableAsync(parent, table);

        // Act
        var errors = await RunAutoModeAsync(table);

        // Assert
        Assert.Empty(errors);
        await AssertChildMatchesParentAsync(parent, child, table);
        Assert.Equal(99.95m, await DecimalAsync(child, $"SELECT [Price] FROM [dbo].[{table}] WHERE [Id] = 1"));
        Assert.Equal(2, await ScalarAsync(child, $"SELECT CAST(scale AS INT) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.{table}') AND name = N'Price'"));
    }

    [Fact]
    public async Task AutoMode_ChildDecimalWithWrongScale_IsReportedThenCopiedExactlyAfterTheAlter()
    {
        // Arrange - a child table as earlier versions created it: decimal(18,0) for a decimal(10,2) parent
        const string table = "LegacyPricedProduct";
        await using var parent = new SqlConnection(_sql.ParentConnectionString);
        await using var child = new SqlConnection(_sql.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();
        await CreatePricedParentTableAsync(parent, table);
        await ExecuteAsync(child, $"""
            CREATE TABLE [dbo].[{table}] (
                [Id] INT NOT NULL PRIMARY KEY,
                [Name] NVARCHAR(50) NOT NULL,
                [Price] DECIMAL(18, 0) NOT NULL,
                [LastUpdate] DATETIME NOT NULL);
            """);

        // Act - the wrong column is reported and nothing is copied into it
        var firstErrors = await RunAutoModeAsync(table);

        // Assert
        Assert.Contains(firstErrors, e => e.Contains("decimal(18,0) in the child but decimal(10,2) in the parent", StringComparison.Ordinal));
        Assert.Equal(0, await ScalarAsync(child, $"SELECT COUNT(*) FROM [dbo].[{table}]"));

        // Act - the repair the scaffold tool writes (<Table>_AlterColumns.sql), applied by an administrator
        await ExecuteAsync(child, $"ALTER TABLE [dbo].[{table}] ALTER COLUMN [Price] [decimal](10,2) NOT NULL");
        var secondErrors = await RunAutoModeAsync(table);

        // Assert
        Assert.Empty(secondErrors);
        await AssertChildMatchesParentAsync(parent, child, table);
        Assert.Equal(99.95m, await DecimalAsync(child, $"SELECT [Price] FROM [dbo].[{table}] WHERE [Id] = 1"));
    }

    // Prices with cents, and read access for the runtime identity on the parent
    private static async Task CreatePricedParentTableAsync(SqlConnection parent, string table)
    {
        await ExecuteAsync(parent, $"""
            CREATE TABLE [dbo].[{table}] (
                [Id] INT NOT NULL PRIMARY KEY,
                [Name] NVARCHAR(50) NOT NULL,
                [Price] DECIMAL(10, 2) NOT NULL,
                [LastUpdate] DATETIME NOT NULL CONSTRAINT [DF_{table}_LastUpdate] DEFAULT (GETUTCDATE()));
            INSERT INTO [dbo].[{table}] ([Id], [Name], [Price], [LastUpdate])
            VALUES (1, N'A', 99.95, DATEADD(hour, -1, GETUTCDATE())),
                   (2, N'B', 19.99, DATEADD(hour, -1, GETUTCDATE())),
                   (3, N'C', 0.01, DATEADD(hour, -1, GETUTCDATE()));
            GRANT SELECT, VIEW DEFINITION ON [dbo].[{table}] TO [smarttablesync-runtime];
            """);
    }

    private static async Task<decimal> DecimalAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return (decimal)(await command.ExecuteScalarAsync())!;
    }

    // A new parent table with 50 rows, and the documented step for adding a table: read access
    // for the runtime identity on it in the parent. The rows were last changed an hour ago, outside
    // the Timestamp strategy's one-minute look-back, so no incremental pass reads them again.
    private static async Task CreateParentTableAsync(SqlConnection parent, string table)
    {
        var quoted = SmartTableSync.Sql.SqlIdentifier.Table(table);
        var constraint = $"DF_{SmartTableSync.Sql.SyncTableName.Schema(table)}_{SmartTableSync.Sql.SyncTableName.Object(table)}_LastUpdate";

        await ExecuteAsync(parent, $"""
            CREATE TABLE {quoted} (
                [Id] INT NOT NULL PRIMARY KEY,
                [Name] NVARCHAR(50) NOT NULL,
                [LastUpdate] DATETIME NOT NULL CONSTRAINT [{constraint}] DEFAULT (GETUTCDATE()));
            INSERT INTO {quoted} ([Id], [Name], [LastUpdate])
            SELECT TOP (50) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), N'Item', DATEADD(hour, -1, GETUTCDATE()) FROM sys.all_columns;
            GRANT SELECT, VIEW DEFINITION ON {quoted} TO [smarttablesync-runtime];
            """);
    }

    // A parent heap (no PRIMARY KEY, no index) with 50 rows, granted like CreateParentTableAsync.
    // (Region, Code) is unique, Region alone isn't; Seq and the nullable Note are unique too.
    private static async Task CreateParentHeapAsync(SqlConnection parent, string table)
    {
        await ExecuteAsync(parent, $"""
            CREATE TABLE [dbo].[{table}] (
                [Seq] INT NOT NULL,
                [Code] NVARCHAR(20) NOT NULL,
                [Region] CHAR(2) NOT NULL,
                [Name] NVARCHAR(50) NOT NULL,
                [Note] NVARCHAR(50) NULL,
                [LastUpdate] DATETIME NOT NULL);
            INSERT INTO [dbo].[{table}] ([Seq], [Code], [Region], [Name], [Note], [LastUpdate])
            SELECT n, CONCAT(N'C', n), CHOOSE(n % 3 + 1, 'NO', 'SE', 'FI'), N'Item', CONCAT(N'Note ', n), DATEADD(hour, -1, GETUTCDATE())
            FROM (SELECT TOP (50) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_columns) AS numbers;
            GRANT SELECT, VIEW DEFINITION ON [dbo].[{table}] TO [smarttablesync-runtime];
            """);
    }

    private static Task ChangeParentRowsAsync(SqlConnection parent, string table) => ExecuteAsync(parent, $"""
        UPDATE [dbo].[{table}] SET [Name] = N'Changed', [LastUpdate] = GETUTCDATE() WHERE [Seq] = 1;
        INSERT INTO [dbo].[{table}] ([Seq], [Code], [Region], [Name], [Note], [LastUpdate])
        VALUES (1000, N'C1000', 'SE', N'New', NULL, GETUTCDATE());
        DELETE FROM [dbo].[{table}] WHERE [Seq] = 2;
        """);

    private static async Task<IReadOnlyList<string>> ChildPrimaryKeyAsync(SqlConnection child, string table)
    {
        await using var command = new SqlCommand($"""
            SELECT c.name
            FROM   sys.key_constraints kc
            JOIN   sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
            JOIN   sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE  kc.parent_object_id = OBJECT_ID(N'[dbo].[{table}]') AND kc.type = 'PK'
            ORDER  BY ic.key_ordinal
            """, child);
        await using var reader = await command.ExecuteReaderAsync();

        var columns = new List<string>();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));

        return columns;
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private Task<IReadOnlyList<string>> RunAutoModeAsync(params string[] tables) =>
        RunAutoModeAsync(new Dictionary<string, IReadOnlyList<string>>(), tables);

    private Task<IReadOnlyList<string>> RunAutoModeAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> keyColumns, params string[] tables) =>
        SyncerTestHelpers.RunOrchestratorAsync(
            new Dictionary<string, string?>
            {
                ["ParentDbConnectionString"] = _identities.ParentConnectionString(Runtime),
                ["ChildDbConnectionString"] = _identities.ChildConnectionString(Runtime),
                ["ChildProvisioningConnectionString"] = _identities.ChildConnectionString(ChildProvisioning),
            },
            new SyncSettings { Tables = tables.Length > 0 ? tables : _tables, Mode = SyncMode.Auto, KeyColumns = keyColumns });

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
