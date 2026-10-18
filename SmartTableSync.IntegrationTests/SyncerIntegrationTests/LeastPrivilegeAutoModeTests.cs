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
/// with the provisioning identity and sync with the runtime identity. Uses its own container
/// because it drops those child objects.
/// </summary>
public class LeastPrivilegeAutoModeTests : IClassFixture<LeastPrivilegeAutoModeTests.AutoModeGrants>
{
    private static readonly string[] _tables = ["ProductCatalogItem", "DocumentAsset"];

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
            DROP PROCEDURE IF EXISTS [dbo].[sp_SyncProductCatalogItemBatch];
            DROP PROCEDURE IF EXISTS [dbo].[sp_SyncDocumentAsset];
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

        // Arrange - an insert and an update for the next run
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

    private Task<IReadOnlyList<string>> RunAutoModeAsync() =>
        SyncerTestHelpers.RunOrchestratorAsync(
            new Dictionary<string, string?>
            {
                ["ParentDbConnectionString"] = _identities.ParentConnectionString(Runtime),
                ["ChildDbConnectionString"] = _identities.ChildConnectionString(Runtime),
                ["ChildProvisioningConnectionString"] = _identities.ChildConnectionString(ChildProvisioning),
            },
            new SyncSettings { Tables = _tables, Mode = SyncMode.Auto });

    private static async Task AssertChildMatchesParentAsync(SqlConnection parent, SqlConnection child)
    {
        foreach (var table in _tables)
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
