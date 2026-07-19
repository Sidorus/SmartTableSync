using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SmartTableSync.Configuration;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Infrastructure;
using SmartTableSync.Introspection;
using SmartTableSync.Logging;
using SmartTableSync.Observability;
using SmartTableSync.Options;
using SmartTableSync.Orchestration;
using SmartTableSync.Planning;
using SmartTableSync.Scaffolding;
using SmartTableSync.State;
using SmartTableSync.Syncers;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// The first-ever test of SyncOrchestrator itself: builds a real SyncOrchestrator exactly as
/// SmartTableSyncFunction/Program.cs wires it up (ISyncTableProvisioner + ITableSyncerFactory,
/// driven by a plain list of table names), and runs a real end-to-end cycle against the live
/// container - the exact production code path, not a stand-in.
///
/// Uses its own dedicated container (IClassFixture, not the shared [Collection("SqlServer")])
/// because SyncOrchestrator.RunAsync writes dbo.CustomSyncState for real, keyed only by table
/// name - every other integration test file implicitly depends on that table staying at its
/// seeded InitialDate sentinel for these same 3 real table names, and this is the only test
/// in the whole suite that would ever change it.
/// </summary>
public class OrchestratorEndToEndTests : IClassFixture<MsSqlTestFixture>
{
    private readonly MsSqlTestFixture _fixture;

    public OrchestratorEndToEndTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task RunAsync_SyncsAllThreeTablesViaConfigurableTableSyncer()
    {
        // Arrange
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        await SeedDataLoader.EnsureSeededAsync(parent, "Attribute", "Attribute.json", hasIdentityColumn: true);
        await SeedDataLoader.EnsureSeededAsync(child, "Attribute", "Attribute.json", hasIdentityColumn: true);

        var pavRows = await SeedDataLoader.LoadAsync(Path.Combine(AppContext.BaseDirectory, "SeedData", "ProductAttributeValue.json"));
        await SeedDataLoader.InsertRowsAsync(parent, "ProductAttributeValue", pavRows, hasIdentityColumn: true);

        var pciRows = await SeedDataLoader.LoadAsync(Path.Combine(AppContext.BaseDirectory, "SeedData", "ProductCatalogItem.json"));
        await SeedDataLoader.InsertRowsAsync(parent, "ProductCatalogItem", pciRows);

        var daRows = await SeedDataLoader.LoadAsync(Path.Combine(AppContext.BaseDirectory, "SeedData", "DocumentAsset.json"));
        await SeedDataLoader.InsertRowsAsync(parent, "DocumentAsset", daRows);

        // See InitialSyncTests' equivalent comment: without a genuinely large
        // row, real introspection would (correctly, given only the JSON seed data) derive Tvp
        // for DocumentAsset - but only a row-by-row SP is actually deployed for that table.
        await SyncerTestHelpers.EnsureLargeDocumentAssetRowSeededAsync(parent);
        var expectedDocumentAssetCount = daRows.Count + 1;

        var provisioner = new SyncTableProvisioner(
            new SchemaReader(), new SyncPlanHeuristic(), new SyncTableConfigRepository(),
            new NoOpSyncMetricsReporter(), NullLogger<SyncTableProvisioner>.Instance);
        var syncerFactory = new TableSyncerFactory(new BatchLogger(), NullLoggerFactory.Instance);

        var childDdlProvisioner = new ChildDdlProvisioner(
            new SchemaReader(), new SqlArtifactGenerator(), NullLogger<ChildDdlProvisioner>.Instance);

        var orchestrator = new SyncOrchestrator(
            provisioner,
            syncerFactory,
            childDdlProvisioner,
            new DbConnectionFactory(_fixture.Configuration),
            new SyncStateRepository(),
            new NoOpSyncMetricsReporter(),
            Microsoft.Extensions.Options.Options.Create(new SyncSettings
            {
                Tables = ["ProductAttributeValue", "ProductCatalogItem", "DocumentAsset"]
            }),
            NullLogger<SyncOrchestrator>.Instance);

        // Act
        await orchestrator.RunAsync(CancellationToken.None);

        // Assert - isolated database, so exact counts are valid
        await using var pavCountCmd = new SqlCommand("SELECT COUNT(*) FROM [dbo].[ProductAttributeValue]", child);
        Assert.Equal(pavRows.Count, (int)(await pavCountCmd.ExecuteScalarAsync())!);

        await using var pciCountCmd = new SqlCommand("SELECT COUNT(*) FROM [dbo].[ProductCatalogItem]", child);
        Assert.Equal(pciRows.Count, (int)(await pciCountCmd.ExecuteScalarAsync())!);

        await using var daCountCmd = new SqlCommand("SELECT COUNT(*) FROM [dbo].[DocumentAsset]", child);
        Assert.Equal(expectedDocumentAssetCount, (int)(await daCountCmd.ExecuteScalarAsync())!);
    }
}
