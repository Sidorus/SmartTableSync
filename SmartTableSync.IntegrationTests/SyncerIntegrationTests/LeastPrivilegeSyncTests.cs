using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SmartTableSync.Hosting;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Introspection;
using SmartTableSync.Planning;
using SmartTableSync.Scaffolding;
using static SmartTableSync.IntegrationTests.Support.PermissionsScript;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// Every other integration test connects as sa, so a grant missing from the documentation never
/// shows up there. Here each identity gets exactly what sql/07_permissions.sql grants it, run as
/// written, and nothing more; then each does its real job. A failure means the documented
/// least-privilege setup doesn't work.
///
/// Covers the example tables 07_permissions.sql names: ProductAttributeValue (IDENTITY column,
/// row-by-row merge), ProductCatalogItem (TVP merge, Change Tracking) and DocumentAsset
/// (row-by-row merge for large rows). Uses its own container (IClassFixture) because it creates
/// logins and changes grants, sync state and data.
/// </summary>
public class LeastPrivilegeSyncTests : IClassFixture<LeastPrivilegeSyncTests.ManualModeGrants>
{
    private static readonly string[] _tables = ["ProductAttributeValue", "ProductCatalogItem", "DocumentAsset"];

    private readonly MsSqlTestFixture _sql;
    private readonly LeastPrivilegeIdentities _identities;

    public LeastPrivilegeSyncTests(ManualModeGrants fixture)
    {
        _sql = fixture.Sql;
        _identities = fixture.Identities;
    }

    /// <summary>Every part of 07_permissions.sql except the Auto mode alternative.</summary>
    public sealed class ManualModeGrants : LeastPrivilegeFixture
    {
        internal override async Task ApplyGrantsAsync(PermissionsScript script)
        {
            await RunOnChildAsync(script.For(ChildProvisioning, ChildDatabase), "07_permissions.sql: child-provisioning");
            await RunOnParentAsync(script.For(ParentIntrospection, ParentDatabase), "07_permissions.sql: parent-introspection");
            await RunOnParentAsync(script.For(Runtime, ParentDatabase), "07_permissions.sql: runtime, parent");
            await RunOnChildAsync(script.For(Runtime, ChildDatabase), "07_permissions.sql: runtime, child");
        }
    }

    [Fact]
    public async Task ParentIntrospectionIdentity_CanProfileEveryDocumentedTable()
    {
        // The scaffold tool's per-table generation: full profile, including the row-size sampling
        await using var parent = new SqlConnection(_identities.ParentConnectionString(ParentIntrospection));
        await parent.OpenAsync();
        var reader = new SchemaReader();

        foreach (var table in _tables)
        {
            var profile = await reader.ProfileTableAsync(parent, table, CancellationToken.None);

            Assert.NotEmpty(profile.PrimaryKeyColumns);
        }
    }

    [Fact]
    public async Task ChildProvisioningIdentity_CanCreateTrackingTablesTableTypesTablesAndProcedures()
    {
        // Arrange - profiles of two real tables under new names, so CREATE TABLE, CREATE TYPE and
        // CREATE PROCEDURE all actually run instead of finding existing objects
        await using var admin = new SqlConnection(_sql.ParentConnectionString);
        await admin.OpenAsync();
        await using var child = new SqlConnection(_identities.ChildConnectionString(ChildProvisioning));
        await child.OpenAsync();

        var reader = new SchemaReader();
        var childDdl = new ChildDdlProvisioner(reader, new SqlArtifactGenerator(), NullLogger<ChildDdlProvisioner>.Instance);
        var probes = new[]
        {
            (Source: "ProductCatalogItem", Name: "ProvisioningProbeTvp", Merge: MergeStrategy.Tvp),
            (Source: "ProductAttributeValue", Name: "ProvisioningProbeRowByRow", Merge: MergeStrategy.RowByRow),
        };

        // Act
        await childDdl.EnsureInfrastructureExistsAsync(child, CancellationToken.None);

        foreach (var probe in probes)
        {
            var profile = (await reader.ProfileTableAsync(admin, probe.Source, CancellationToken.None)) with { TableName = probe.Name };
            var plan = new SyncPlanHeuristic().DerivePlan(profile, new PlanHeuristicOptions(), "LastUpdate");
            Assert.Equal(probe.Merge, plan.MergeStrategy);

            await childDdl.EnsureChildObjectsExistAsync(child, profile, plan, CancellationToken.None);

            // Assert
            Assert.True(await reader.TableExistsAsync(child, probe.Name, CancellationToken.None));
        }
    }

    [Fact]
    public async Task RuntimeIdentity_SyncsEveryDocumentedTable_FirstCopyThenInsertsUpdatesAndDeletes()
    {
        // Arrange - as the DBA: seed the parent, and enable Change Tracking on ProductCatalogItem
        // with the scaffold tool's script, which also grants the runtime VIEW CHANGE TRACKING
        await using var parent = new SqlConnection(_sql.ParentConnectionString);
        await using var child = new SqlConnection(_sql.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();
        await SeedExampleTablesAsync(parent, child);

        var productCatalogItem = await new SchemaReader().GetFingerprintInputsAsync(parent, "ProductCatalogItem", CancellationToken.None);
        await MsSqlTestFixture.RunGeneratedScriptAsync(_sql.ParentConnectionString,
            new SqlArtifactGenerator().GenerateChangeTrackingSetupScript(productCatalogItem), "ProductCatalogItem_ChangeTrackingSetup.sql");

        // Act - first run: initial copy of every table
        var errors = await RunAsRuntimeAsync();

        // Assert
        Assert.Empty(errors);
        await AssertChildMatchesParentAsync(parent, child);
        Assert.Equal("SqlChangeTracking", await SyncerTestHelpers.ReadScalarStringAsync(child,
            "SELECT [IncrementalStrategy] FROM [dbo].[SyncTableConfig] WHERE [TableName] = @Table",
            ("@Table", System.Data.SqlDbType.NVarChar, "ProductCatalogItem", 100)));

        // Arrange - changes in every table: an insert into the IDENTITY table, an update and a
        // delete through Change Tracking, and a delete found by reconciliation
        await SyncerTestHelpers.InsertNewProductAttributeValueRowAsync(parent, attributeId: 1, slug: "least-privilege", lastUpdate: DateTime.UtcNow);
        await SyncerTestHelpers.InsertNewProductCatalogItemRowAsync(parent, "LP-NEW-1", "Added after the first sync", DateTime.UtcNow);
        await ExecuteAsync(parent, "UPDATE TOP (1) [dbo].[ProductCatalogItem] SET [Description] = N'Changed after the first sync' WHERE [Sku] <> N'LP-NEW-1'");
        await ExecuteAsync(parent, "DELETE TOP (1) FROM [dbo].[ProductCatalogItem] WHERE [Sku] <> N'LP-NEW-1'");
        await ExecuteAsync(parent, "DELETE TOP (1) FROM [dbo].[DocumentAsset]");

        // Act - second run
        errors = await RunAsRuntimeAsync();

        // Assert
        Assert.Empty(errors);
        await AssertChildMatchesParentAsync(parent, child);
    }

    private Task<IReadOnlyList<string>> RunAsRuntimeAsync() =>
        SyncerTestHelpers.RunOrchestratorAsync(
            new Dictionary<string, string?>
            {
                ["ParentDbConnectionString"] = _identities.ParentConnectionString(Runtime),
                ["ChildDbConnectionString"] = _identities.ChildConnectionString(Runtime),
            },
            new SyncSettings { Tables = _tables });

    private static async Task SeedExampleTablesAsync(SqlConnection parent, SqlConnection child)
    {
        await SeedDataLoader.EnsureSeededAsync(parent, "Attribute", "Attribute.json", hasIdentityColumn: true);
        await SeedDataLoader.EnsureSeededAsync(child, "Attribute", "Attribute.json", hasIdentityColumn: true);

        var seedData = Path.Combine(AppContext.BaseDirectory, "SeedData");
        await SeedDataLoader.InsertRowsAsync(parent, "ProductAttributeValue",
            await SeedDataLoader.LoadAsync(Path.Combine(seedData, "ProductAttributeValue.json")), hasIdentityColumn: true);
        await SeedDataLoader.InsertRowsAsync(parent, "ProductCatalogItem",
            await SeedDataLoader.LoadAsync(Path.Combine(seedData, "ProductCatalogItem.json")));
        await SeedDataLoader.InsertRowsAsync(parent, "DocumentAsset",
            await SeedDataLoader.LoadAsync(Path.Combine(seedData, "DocumentAsset.json")));

        // Only the row-by-row procedure is deployed for DocumentAsset; a large row makes the
        // planner pick that merge strategy, as in production
        await SyncerTestHelpers.EnsureLargeDocumentAssetRowSeededAsync(parent);
    }

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
