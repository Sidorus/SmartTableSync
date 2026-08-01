using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SmartTableSync.Configuration;
using SmartTableSync.Infrastructure;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Introspection;
using SmartTableSync.Logging;
using SmartTableSync.Models;
using SmartTableSync.Observability;
using SmartTableSync.Options;
using SmartTableSync.Planning;
using SmartTableSync.Scaffolding;
using SmartTableSync.State;
using SmartTableSync.Syncers;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// Item 2 — rowversion incremental strategy. Proves detection, the MIN_ACTIVE_ROWVERSION()
/// bootstrap, and position advancement across cycles against a real database, plus the
/// necessary correctness fixes this feature exposed: SQL Server rejects any explicit value
/// written into a rowversion column, so it must be excluded from the generated TVP MERGE's
/// INSERT/UPDATE column lists, the TVP type itself, and the initial-sync bulk-copy column
/// mapping — without those fixes, every sync of a rowversion-bearing table fails outright.
///
/// RowVersionTestTable has no hand-authored TVP/stored procedure, so this provisions it via
/// the Auto-mode path (SyncTableProvisioner + ChildDdlProvisioner), the same construction
/// IncrementalStrategyTests uses for its own test-only tables.
///
/// Uses its own dedicated container (IClassFixture, not the shared [Collection("SqlServer")])
/// because RowVersionSyncRunner owns CustomSyncState.IncrementalPosition entirely, calling
/// syncer.SyncAsync directly here (bypassing SyncOrchestrator, which would otherwise also
/// call UpdateLastSyncDateAsync) writes a CustomSyncState row for this table - something no
/// other test in the shared collection expects.
/// </summary>
public class RowVersionStrategyTests : IClassFixture<MsSqlTestFixture>
{
    private const string TableName = "RowVersionTestTable";

    private readonly MsSqlTestFixture _fixture;

    public RowVersionStrategyTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task DetectsRowVersionColumn_ResolvesRowVersionStrategyWithNoConfig()
    {
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        var (_, plan) = await ProvisionAsync(parent, child);

        Assert.Equal(IncrementalStrategy.RowVersion, plan.IncrementalStrategy);
        Assert.Equal("RowVer", plan.ChangeTrackingColumn);
    }

    [Fact]
    public async Task IncrementalSync_PicksUpInsertsAndUpdatesAcrossCycles_ThenStopsWhenNothingChanged()
    {
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        const int firstId = 1;
        const int secondId = 2;

        await SyncerTestHelpers.InsertRowVersionTestTableRowAsync(parent, firstId, "Original");

        var (profile, plan) = await ProvisionAsync(parent, child);
        Assert.Equal(IncrementalStrategy.RowVersion, plan.IncrementalStrategy);

        await BuildChildDdlProvisioner().EnsureChildObjectsExistAsync(child, profile, plan, CancellationToken.None);
        var syncer = new TableSyncerFactory(new BatchLogger(), new SyncStateRepository(), NullLoggerFactory.Instance).Create(profile, plan);

        // Initial sync: bulk-copies the one existing row. Succeeding at all proves
        // BulkCopyWriter's rowversion-column exclusion works - without it, SqlBulkCopy
        // throws trying to write parent's RowVer value into child's own rowversion column.
        var initialResult = await syncer.SyncAsync(parent, child, SyncConstants.InitialDate, CancellationToken.None);
        Assert.True(initialResult.IsInitialSync);
        Assert.Equal(1, initialResult.RowsSynced);
        Assert.Equal(0, initialResult.RowsErrored);

        // Child generated its own rowversion value for the row - never null, and there was
        // no error inserting it (the value itself is engine-assigned, not copied from parent).
        Assert.NotNull(await ReadRowVerAsync(child, firstId));

        // First incremental cycle: no persisted position yet, so this is the bootstrap call -
        // it starts from "beginning of time" (0x00...00) and therefore re-processes the row
        // initial sync already carried. Harmless (idempotent MERGE via TVP - also proves
        // SqlArtifactGenerator's TVP-procedure rowversion exclusion works), and establishes a
        // real position for subsequent cycles.
        var bootstrapResult = await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);
        Assert.False(bootstrapResult.IsInitialSync);
        Assert.True(bootstrapResult.RowsSynced >= 1);
        Assert.Equal(0, bootstrapResult.RowsErrored);

        // Now make real changes: a new row and an update to the existing one.
        await SyncerTestHelpers.InsertRowVersionTestTableRowAsync(parent, secondId, "New");
        await SyncerTestHelpers.UpdateRowVersionTestTableValueAsync(parent, firstId, "Updated");

        var changeResult = await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);
        Assert.False(changeResult.IsInitialSync);
        Assert.Equal(2, changeResult.RowsSynced);
        Assert.Equal(0, changeResult.RowsErrored);

        await using var countCmd = new SqlCommand("SELECT COUNT(*) FROM [dbo].[RowVersionTestTable]", child);
        Assert.Equal(2, (int)(await countCmd.ExecuteScalarAsync())!);
        Assert.Equal("Updated", await SyncerTestHelpers.ReadScalarStringAsync(child,
            "SELECT [Value] FROM [dbo].[RowVersionTestTable] WHERE [Id] = @Id",
            ("@Id", SqlDbType.Int, firstId, 0)));
        Assert.Equal("New", await SyncerTestHelpers.ReadScalarStringAsync(child,
            "SELECT [Value] FROM [dbo].[RowVersionTestTable] WHERE [Id] = @Id",
            ("@Id", SqlDbType.Int, secondId, 0)));

        // Third cycle, nothing changed since - proves the position actually advanced past
        // both prior changes rather than re-matching them forever.
        var noopResult = await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(0, noopResult.RowsSynced);
        Assert.Equal(0, noopResult.RowsErrored);
    }

    private static async Task<byte[]?> ReadRowVerAsync(SqlConnection child, int id)
    {
        await using var cmd = new SqlCommand("SELECT [RowVer] FROM [dbo].[RowVersionTestTable] WHERE [Id] = @Id", child);
        cmd.AddParam("@Id", SqlDbType.Int, id);
        return await cmd.ExecuteScalarAsync() as byte[];
    }

    private static async Task<(TableProfile Profile, SyncPlan Plan)> ProvisionAsync(SqlConnection parent, SqlConnection child)
    {
        var provisioner = new SyncTableProvisioner(
            new SchemaReader(), new SyncPlanHeuristic(), new SyncTableConfigRepository(),
            new NoOpSyncMetricsReporter(), Microsoft.Extensions.Options.Options.Create(new SyncSettings()),
            NullLogger<SyncTableProvisioner>.Instance);

        var (profile, plan, _) = await provisioner.EnsureProvisionedAsync(parent, child, TableName, CancellationToken.None);
        return (profile, plan);
    }

    private static ChildDdlProvisioner BuildChildDdlProvisioner() => new(
        new SchemaReader(), new SqlArtifactGenerator(), NullLogger<ChildDdlProvisioner>.Instance);
}
