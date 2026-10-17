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
/// Strategy 3 — SQL Server Change Tracking (SqlChangeTracking). Proves against a real database:
/// auto-detection (CT wins over a LastUpdate column) and the manual override opting out of it;
/// the baseline recorded before the initial copy; inserts, updates, deletes and primary-key
/// changes picked up from CHANGETABLE; an uncommitted transaction neither blocking the read nor
/// being skipped once it commits; the full re-scan + re-baseline fallback for a broken version
/// chain (position ahead of the current version, as after a restore; Change Tracking disabled
/// and re-enabled); and a resumed initial sync keeping its original baseline.
///
/// MsSqlTestFixture enables CT (and ALLOW_SNAPSHOT_ISOLATION) on the parent copies of the
/// ChangeTracking*TestTable tables only. One table per scenario, so each test owns its own
/// CustomSyncState position. Uses its own container (IClassFixture) for the same reason as
/// RowVersionStrategyTests: calling syncer.SyncAsync directly writes CustomSyncState rows no
/// test in the shared collection expects.
/// </summary>
public class ChangeTrackingStrategyTests : IClassFixture<MsSqlTestFixture>
{
    private readonly MsSqlTestFixture _fixture;
    private readonly SyncStateRepository _syncState = new();

    public ChangeTrackingStrategyTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task FullCycle_BaselineThenInsertsUpdatesDeletesAndKeyChanges()
    {
        const string table = "ChangeTrackingTestTable";
        await using var parent = await OpenAsync(_fixture.ParentConnectionString);
        await using var child = await OpenAsync(_fixture.ChildConnectionString);

        await SyncerTestHelpers.InsertChangeTrackingTestRowAsync(parent, table, 1, "One");
        await SyncerTestHelpers.InsertChangeTrackingTestRowAsync(parent, table, 2, "Two");
        await SyncerTestHelpers.InsertChangeTrackingTestRowAsync(parent, table, 3, "Three");

        // Keys at or below zero: the first page used to start from a "0" placeholder key
        // (WHERE [Id] > 0), which skipped these rows entirely.
        await SyncerTestHelpers.InsertChangeTrackingTestRowAsync(parent, table, 0, "Zero");
        await SyncerTestHelpers.InsertChangeTrackingTestRowAsync(parent, table, -5, "Negative");

        var (profile, plan, syncer) = await BuildAsync(parent, child, table);

        // Auto-detected with no config, and preferred over the table's LastUpdate column.
        Assert.True(profile.IsChangeTrackingEnabled);
        Assert.Equal(IncrementalStrategy.SqlChangeTracking, plan.IncrementalStrategy);
        Assert.Null(plan.IncrementalColumn);

        var initial = await syncer.SyncAsync(parent, child, SyncConstants.InitialDate, CancellationToken.None);
        Assert.True(initial.IsInitialSync);
        Assert.Equal(5, initial.RowsSynced);
        Assert.Equal("Zero", await ReadValueAsync(child, table, 0));
        Assert.Equal("Negative", await ReadValueAsync(child, table, -5));
        Assert.StartsWith("CT:", await _syncState.GetIncrementalPositionAsync(child, table));

        // The baseline was recorded before the copy, so the first incremental cycle reads
        // nothing - not a re-read of the rows the initial copy already carried.
        var firstIncremental = await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);
        Assert.False(firstIncremental.IsInitialSync);
        Assert.Equal(0, firstIncremental.RowsSynced);

        await SyncerTestHelpers.UpdateChangeTrackingTestValueAsync(parent, table, 2, "Two-updated");
        await ExecuteAsync(parent, $"DELETE FROM [dbo].[{table}] WHERE [Id] = 3");
        await SyncerTestHelpers.InsertChangeTrackingTestRowAsync(parent, table, 4, "Four");

        var changes = await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(2, changes.RowsSynced); // 1 update + 1 insert
        Assert.Equal(1, changes.RowsDeleted);
        Assert.Equal(0, changes.RowsErrored);
        Assert.Equal("Two-updated", await ReadValueAsync(child, table, 2));
        Assert.Equal(0, await CountAsync(child, table, 3));
        Assert.Equal("Four", await ReadValueAsync(child, table, 4));

        // A primary-key update is reported as a delete of the old key plus an insert of the new.
        await ExecuteAsync(parent, $"UPDATE [dbo].[{table}] SET [Id] = 10 WHERE [Id] = 1");

        var keyChange = await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(1, keyChange.RowsSynced);
        Assert.Equal(1, keyChange.RowsDeleted);
        Assert.Equal(0, keyChange.RowsErrored);
        Assert.Equal(0, await CountAsync(child, table, 1));
        Assert.Equal("One", await ReadValueAsync(child, table, 10));

        var noop = await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(0, noop.RowsSynced);
        Assert.Equal(0, noop.RowsErrored);
    }

    [Fact]
    public async Task UncommittedTransaction_DoesNotBlockTheRead_AndIsPickedUpAfterCommit()
    {
        const string table = "ChangeTrackingInFlightTestTable";
        await using var parent = await OpenAsync(_fixture.ParentConnectionString);
        await using var child = await OpenAsync(_fixture.ChildConnectionString);

        await SyncerTestHelpers.InsertChangeTrackingTestRowAsync(parent, table, 1, "Original");
        var (_, _, syncer) = await BuildAsync(parent, child, table);
        await syncer.SyncAsync(parent, child, SyncConstants.InitialDate, CancellationToken.None);
        await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);

        await using var writer = await OpenAsync(_fixture.ParentConnectionString);
        await using var transaction = writer.BeginTransaction();
        await SyncerTestHelpers.UpdateChangeTrackingTestValueAsync(writer, table, 1, "InFlight", transaction);

        // The writer still holds its locks: under snapshot isolation the cycle neither blocks
        // nor sees the uncommitted change.
        var during = await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(0, during.RowsSynced);
        Assert.Equal("Original", await ReadValueAsync(child, table, 1));

        // CT versions are assigned at commit, so the commit lands after the position the
        // previous cycle saved - it can't be skipped.
        await transaction.CommitAsync();

        var after = await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(1, after.RowsSynced);
        Assert.Equal("InFlight", await ReadValueAsync(child, table, 1));
    }

    [Fact]
    public async Task BrokenVersionChain_FallsBackToFullRescan_ThenResumesChangeTracking()
    {
        const string table = "ChangeTrackingRescanTestTable";
        await using var parent = await OpenAsync(_fixture.ParentConnectionString);
        await using var child = await OpenAsync(_fixture.ChildConnectionString);

        await SyncerTestHelpers.InsertChangeTrackingTestRowAsync(parent, table, 1, "One");
        await SyncerTestHelpers.InsertChangeTrackingTestRowAsync(parent, table, 2, "Two");
        var metrics = new FullRescanRecorder();
        var (_, _, syncer) = await BuildAsync(parent, child, table, metrics: metrics);
        await syncer.SyncAsync(parent, child, SyncConstants.InitialDate, CancellationToken.None);
        await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);

        // Case 1 - position ahead of CHANGE_TRACKING_CURRENT_VERSION, as after the parent is
        // restored from a backup. Without the check, every change would be silently skipped.
        await _syncState.UpdateIncrementalPositionAsync(child, table, "CT:999999999");
        await SyncerTestHelpers.UpdateChangeTrackingTestValueAsync(parent, table, 2, "Two-after-restore");

        var afterRestore = await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(2, afterRestore.RowsSynced); // full re-scan: every row
        Assert.Equal("Two-after-restore", await ReadValueAsync(child, table, 2));
        Assert.NotEqual("CT:999999999", await _syncState.GetIncrementalPositionAsync(child, table));

        var resumed = await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(0, resumed.RowsSynced);

        // Case 2 - Change Tracking disabled and re-enabled on the table: the tracking data for
        // the change made before that is gone, and the min valid version moves past our position.
        await SyncerTestHelpers.UpdateChangeTrackingTestValueAsync(parent, table, 1, "One-before-reenable");
        await ExecuteAsync(parent, $"ALTER TABLE [dbo].[{table}] DISABLE CHANGE_TRACKING");
        await ExecuteAsync(parent, $"ALTER TABLE [dbo].[{table}] ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF)");

        var afterReenable = await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(2, afterReenable.RowsSynced);
        Assert.Equal("One-before-reenable", await ReadValueAsync(child, table, 1));

        var resumedAgain = await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(0, resumedAgain.RowsSynced);

        // Each fallback is reported once, with a fixed category usable as a metric tag.
        Assert.Equal(
            [(table, "position_ahead"), (table, "position_expired")],
            metrics.FullRescans);
    }

    [Fact]
    public async Task FallbackRescan_AlsoRemovesRowsDeletedWhileTheChainWasBroken()
    {
        const string table = "ChangeTrackingReconcileTestTable";
        await using var parent = await OpenAsync(_fixture.ParentConnectionString);
        await using var child = await OpenAsync(_fixture.ChildConnectionString);

        await SyncerTestHelpers.InsertChangeTrackingTestRowAsync(parent, table, 1, "One");
        await SyncerTestHelpers.InsertChangeTrackingTestRowAsync(parent, table, 2, "Two");
        await SyncerTestHelpers.InsertChangeTrackingTestRowAsync(parent, table, 3, "Three");
        var (_, _, syncer) = await BuildAsync(parent, child, table);
        await syncer.SyncAsync(parent, child, SyncConstants.InitialDate, CancellationToken.None);
        await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);

        // The delete is recorded by CT, then CT is disabled and re-enabled, which throws that
        // tracking data away - CT alone can never report this delete any more.
        await ExecuteAsync(parent, $"DELETE FROM [dbo].[{table}] WHERE [Id] = 3");
        await ExecuteAsync(parent, $"ALTER TABLE [dbo].[{table}] DISABLE CHANGE_TRACKING");
        await ExecuteAsync(parent, $"ALTER TABLE [dbo].[{table}] ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF)");

        var afterReenable = await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(2, afterReenable.RowsSynced); // both remaining rows re-scanned
        Assert.Equal(1, afterReenable.RowsDeleted); // the orphan found by the reconciliation
        Assert.Equal(0, afterReenable.RowsErrored);
        Assert.Equal(0, await CountAsync(child, table, 3));
        Assert.Equal("One", await ReadValueAsync(child, table, 1));
    }

    [Fact]
    public async Task ResumedInitialSync_KeepsOriginalBaseline()
    {
        const string table = "ChangeTrackingBaselineTestTable";
        await using var parent = await OpenAsync(_fixture.ParentConnectionString);
        await using var child = await OpenAsync(_fixture.ChildConnectionString);

        await SyncerTestHelpers.InsertChangeTrackingTestRowAsync(parent, table, 1, "Original");
        var (_, _, syncer) = await BuildAsync(parent, child, table);

        // As if an earlier, interrupted initial sync had already recorded its baseline...
        await using (var versionCmd = new SqlCommand("SELECT CHANGE_TRACKING_CURRENT_VERSION()", parent))
        {
            var baseline = $"CT:{(long)(await versionCmd.ExecuteScalarAsync())!}";
            await _syncState.UpdateIncrementalPositionAsync(child, table, baseline);
        }
        var originalBaseline = await _syncState.GetIncrementalPositionAsync(child, table);

        // ...and the row changed before the initial sync resumed.
        await SyncerTestHelpers.UpdateChangeTrackingTestValueAsync(parent, table, 1, "Changed-before-resume");

        var initial = await syncer.SyncAsync(parent, child, SyncConstants.InitialDate, CancellationToken.None);
        Assert.Equal(1, initial.RowsSynced);
        Assert.Equal(originalBaseline, await _syncState.GetIncrementalPositionAsync(child, table));

        // The change is still inside the window after the original baseline, so it's read
        // again (harmless - idempotent MERGE). A baseline moved forward would read nothing.
        var incremental = await syncer.SyncAsync(parent, child, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(1, incremental.RowsSynced);
        Assert.Equal("Changed-before-resume", await ReadValueAsync(child, table, 1));
    }

    [Fact]
    public async Task IncrementalStrategyOverride_PinsTimestamp_EvenWithChangeTrackingEnabled()
    {
        const string table = "ChangeTrackingOverrideTestTable";
        await using var parent = await OpenAsync(_fixture.ParentConnectionString);
        await using var child = await OpenAsync(_fixture.ChildConnectionString);

        var settings = new SyncSettings
        {
            IncrementalStrategyOverrides = new Dictionary<string, IncrementalStrategy> { [table] = IncrementalStrategy.Timestamp },
        };

        var (profile, plan, _) = await BuildAsync(parent, child, table, settings);

        Assert.True(profile.IsChangeTrackingEnabled);
        Assert.Equal(IncrementalStrategy.Timestamp, plan.IncrementalStrategy);
        Assert.Equal("LastUpdate", plan.IncrementalColumn);
    }

    private static async Task<(TableProfile Profile, SyncPlan Plan, ITableSyncer Syncer)> BuildAsync(
        SqlConnection parent, SqlConnection child, string table, SyncSettings? settings = null, ISyncMetricsReporter? metrics = null)
    {
        var provisioner = new SyncTableProvisioner(
            new SchemaReader(), new SyncPlanHeuristic(), new SyncTableConfigRepository(),
            new NoOpSyncMetricsReporter(), Microsoft.Extensions.Options.Options.Create(settings ?? new SyncSettings()),
            NullLogger<SyncTableProvisioner>.Instance);

        var (profile, plan, _) = await provisioner.EnsureProvisionedAsync(parent, child, table, CancellationToken.None);

        await new ChildDdlProvisioner(new SchemaReader(), new SqlArtifactGenerator(), NullLogger<ChildDdlProvisioner>.Instance)
            .EnsureChildObjectsExistAsync(child, profile, plan, CancellationToken.None);

        var syncer = new TableSyncerFactory(
            new BatchLogger(), new SyncStateRepository(), metrics ?? new NoOpSyncMetricsReporter(), NullLoggerFactory.Instance)
            .Create(profile, plan);
        return (profile, plan, syncer);
    }

    private sealed class FullRescanRecorder : ISyncMetricsReporter
    {
        public List<(string Table, string Reason)> FullRescans { get; } = [];

        public void RowsSynced(string tableName, string syncMode, int count) { }
        public void RowsErrored(string tableName, string syncMode, int count) { }
        public void SyncDuration(string tableName, string syncMode, TimeSpan duration) { }
        public void RowsDeleted(string tableName, int count) { }
        public void SyncLag(string tableName, TimeSpan lag) { }
        public void TableProvisioned(string tableName) { }
        public void FullRescan(string tableName, string reason) => FullRescans.Add((tableName, reason));
    }

    private static async Task<SqlConnection> OpenAsync(string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private static Task<string> ReadValueAsync(SqlConnection connection, string table, int id) =>
        SyncerTestHelpers.ReadScalarStringAsync(connection,
            $"SELECT [Value] FROM [dbo].[{table}] WHERE [Id] = @Id", ("@Id", SqlDbType.Int, id, 0));

    private static async Task<int> CountAsync(SqlConnection connection, string table, int id)
    {
        await using var cmd = new SqlCommand($"SELECT COUNT(*) FROM [dbo].[{table}] WHERE [Id] = @Id", connection);
        cmd.AddParam("@Id", SqlDbType.Int, id);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
