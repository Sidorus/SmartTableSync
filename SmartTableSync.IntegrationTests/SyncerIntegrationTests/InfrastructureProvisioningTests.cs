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
/// Item 6 — infrastructure DDL (CustomSyncState/SyncBatchLog/SyncTableConfig) is generated,
/// not distributed as static files. Proves IChildDdlProvisioner.EnsureInfrastructureExistsAsync
/// actually (re)creates the three tables when missing, and that the full Auto-mode
/// construction path succeeds against a child database that never had this tracking
/// infrastructure at all - no manual pre-run of any script required in either mode.
///
/// Uses its own dedicated container (IClassFixture, not the shared [Collection("SqlServer")])
/// because these tests DROP the shared tracking tables every other test in the suite depends on.
/// </summary>
public class InfrastructureProvisioningTests : IClassFixture<MsSqlTestFixture>
{
    private readonly MsSqlTestFixture _fixture;

    public InfrastructureProvisioningTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task EnsureInfrastructureExistsAsync_RecreatesAllThreeTables_WhenDropped()
    {
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await child.OpenAsync();
        var reader = new SchemaReader();

        await DropInfrastructureTablesAsync(child);
        Assert.False(await reader.TableExistsAsync(child, "CustomSyncState", CancellationToken.None));
        Assert.False(await reader.TableExistsAsync(child, "SyncBatchLog", CancellationToken.None));
        Assert.False(await reader.TableExistsAsync(child, "SyncTableConfig", CancellationToken.None));

        await BuildChildDdlProvisioner().EnsureInfrastructureExistsAsync(child, CancellationToken.None);

        Assert.True(await reader.TableExistsAsync(child, "CustomSyncState", CancellationToken.None));
        Assert.True(await reader.TableExistsAsync(child, "SyncBatchLog", CancellationToken.None));
        Assert.True(await reader.TableExistsAsync(child, "SyncTableConfig", CancellationToken.None));
    }

    [Fact]
    public async Task EnsureInfrastructureExistsAsync_IsIdempotent_WhenTablesAlreadyExist()
    {
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await child.OpenAsync();
        var childDdlProvisioner = BuildChildDdlProvisioner();

        // Already exist from fixture setup - calling this again must not throw, and the
        // second call's IF NOT EXISTS guards must all take the "already exists" branch.
        await childDdlProvisioner.EnsureInfrastructureExistsAsync(child, CancellationToken.None);
        await childDdlProvisioner.EnsureInfrastructureExistsAsync(child, CancellationToken.None);

        var reader = new SchemaReader();
        Assert.True(await reader.TableExistsAsync(child, "SyncTableConfig", CancellationToken.None));
    }

    [Fact]
    public async Task AutoModeConstructionPath_SyncsSuccessfully_AgainstChildDatabaseWithNoTrackingInfrastructureAtAll()
    {
        // Chains the same pieces SyncOrchestrator.RunAsync does internally in Auto mode
        // (EnsureInfrastructureExistsAsync -> EnsureProvisionedAsync -> EnsureChildObjectsExistAsync
        // -> syncer.SyncAsync), rather than constructing a second real SyncOrchestrator -
        // its concurrency guard is a static, process-wide semaphore (a legitimate anti-
        // re-entrancy guard for a single Azure Function instance), so a second instance
        // running concurrently with OrchestratorEndToEndTests' own orchestrator in the same
        // test process would contend for it and silently no-op.
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        await DropInfrastructureTablesAsync(child);

        var rows = await SeedDataLoader.LoadAsync(Path.Combine(AppContext.BaseDirectory, "SeedData", "ProductCatalogItem.json"));
        await SeedDataLoader.InsertRowsAsync(parent, "ProductCatalogItem", rows);

        var childDdlProvisioner = BuildChildDdlProvisioner();
        await childDdlProvisioner.EnsureInfrastructureExistsAsync(child, CancellationToken.None);

        var provisioner = new SyncTableProvisioner(
            new SchemaReader(), new SyncPlanHeuristic(), new SyncTableConfigRepository(),
            new NoOpSyncMetricsReporter(), Microsoft.Extensions.Options.Options.Create(new SyncSettings()),
            NullLogger<SyncTableProvisioner>.Instance);

        var (profile, plan, wasFreshlyProvisioned) =
            await provisioner.EnsureProvisionedAsync(parent, child, "ProductCatalogItem", CancellationToken.None);
        Assert.True(wasFreshlyProvisioned);

        await childDdlProvisioner.EnsureChildObjectsExistAsync(child, profile, plan, CancellationToken.None);

        var stateRepository = new SyncStateRepository();
        var lastSyncDate = await stateRepository.GetLastSyncDateAsync(child, "ProductCatalogItem", CancellationToken.None);
        var syncer = new TableSyncerFactory(new BatchLogger(), new SyncStateRepository(), new NoOpSyncMetricsReporter(), NullLoggerFactory.Instance).Create(profile, plan);

        var result = await syncer.SyncAsync(parent, child, lastSyncDate, CancellationToken.None);

        Assert.True(result.IsInitialSync);
        Assert.Equal(rows.Count, result.RowsSynced);
        Assert.Equal(0, result.RowsErrored);

        await using var countCmd = new SqlCommand("SELECT COUNT(*) FROM [dbo].[ProductCatalogItem]", child);
        Assert.Equal(rows.Count, (int)(await countCmd.ExecuteScalarAsync())!);
    }

    private static ChildDdlProvisioner BuildChildDdlProvisioner() => new(
        new SchemaReader(), new SqlArtifactGenerator(), NullLogger<ChildDdlProvisioner>.Instance);

    private static async Task DropInfrastructureTablesAsync(SqlConnection child)
    {
        foreach (var table in new[] { "SyncBatchLog", "CustomSyncState", "SyncTableConfig" })
        {
            await using var cmd = new SqlCommand($"DROP TABLE IF EXISTS [dbo].[{table}]", child);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
