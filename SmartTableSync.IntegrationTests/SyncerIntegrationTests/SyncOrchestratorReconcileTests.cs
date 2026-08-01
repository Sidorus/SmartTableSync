using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SmartTableSync.Configuration;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.Infrastructure;
using SmartTableSync.Introspection;
using SmartTableSync.Logging;
using SmartTableSync.Models;
using SmartTableSync.Observability;
using SmartTableSync.Options;
using SmartTableSync.Orchestration;
using SmartTableSync.Planning;
using SmartTableSync.Scaffolding;
using SmartTableSync.State;
using SmartTableSync.Syncers;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// Regression test for a confirmed logic gap in SyncOrchestrator.RunAsync: it used to advance
/// _lastReconcileUtc after every table's SyncTableAsync call regardless of whether that call
/// actually succeeded, so a table whose reconcile step threw would silently wait a full extra
/// hour (_reconcileInterval) before its next reconcile attempt, even though nothing had been
/// resolved. Fixed by only advancing the timestamp when every table's pass this cycle
/// succeeded.
///
/// Uses its own dedicated container (IClassFixture, not the shared [Collection("SqlServer")]
/// or OrchestratorEndToEndTests' fixture) because it runs a real SyncOrchestrator twice back to
/// back and needs to control exactly what CustomSyncState/reconcile state looks like between
/// the two calls, without any other test's use of ProductCatalogItem interfering.
/// </summary>
public class SyncOrchestratorReconcileTests : IClassFixture<MsSqlTestFixture>
{
    private const string TableName = "ProductCatalogItem";

    private readonly MsSqlTestFixture _fixture;

    public SyncOrchestratorReconcileTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task RunAsync_ReconcileFails_DoesNotAdvanceReconcileTimestamp_SoNextRunRetriesReconcile()
    {
        var provisioner = new SyncTableProvisioner(
            new SchemaReader(), new SyncPlanHeuristic(), new SyncTableConfigRepository(),
            new NoOpSyncMetricsReporter(), Microsoft.Extensions.Options.Options.Create(new SyncSettings()),
            NullLogger<SyncTableProvisioner>.Instance);

        var childDdlProvisioner = new ChildDdlProvisioner(
            new SchemaReader(), new SqlArtifactGenerator(), NullLogger<ChildDdlProvisioner>.Instance);

        // A fake ITableSyncer, not a real one - SyncAsync always "succeeds" (no-op) while
        // ReconcileDeletesAsync always throws, isolating the orchestrator's own timestamp
        // bookkeeping from whatever ConfigurableTableSyncer itself does.
        var fakeSyncer = new ReconcileAlwaysThrowsSyncer(TableName);

        var orchestrator = new SyncOrchestrator(
            provisioner,
            new FixedSyncerFactory(fakeSyncer),
            childDdlProvisioner,
            new DbConnectionFactory(_fixture.Configuration),
            new SyncStateRepository(),
            new NoOpSyncMetricsReporter(),
            Microsoft.Extensions.Options.Options.Create(new SyncSettings { Tables = [TableName] }),
            NullLogger<SyncOrchestrator>.Instance);

        // _lastReconcileUtc starts at DateTime.MinValue, so both calls are "due" for reconcile
        // UNLESS the first call's failure incorrectly advances it anyway.
        await orchestrator.RunAsync(CancellationToken.None);
        await orchestrator.RunAsync(CancellationToken.None);

        // If the bug were still present, the first run's failure would still advance
        // _lastReconcileUtc, making the second run's "due for reconcile" check false - so
        // ReconcileDeletesAsync would only ever be attempted once instead of twice.
        Assert.Equal(2, fakeSyncer.ReconcileCallCount);
    }

    private sealed class ReconcileAlwaysThrowsSyncer : ITableSyncer
    {
        public ReconcileAlwaysThrowsSyncer(string tableName) => TableName = tableName;

        public string TableName { get; }

        public int ReconcileCallCount { get; private set; }

        public Task<SyncResult> SyncAsync(
            SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct = default) =>
            Task.FromResult(new SyncResult(TableName, RowsSynced: 0, RowsErrored: 0, HasRetryableErrors: false, IsInitialSync: false, Duration: TimeSpan.Zero));

        public Task<int> ReconcileDeletesAsync(
            SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct = default)
        {
            ReconcileCallCount++;
            throw new InvalidOperationException("Simulated reconcile failure for the regression test.");
        }
    }

    private sealed class FixedSyncerFactory : ITableSyncerFactory
    {
        private readonly ITableSyncer _syncer;

        public FixedSyncerFactory(ITableSyncer syncer) => _syncer = syncer;

        public ITableSyncer Create(TableProfile profile, SyncPlan plan) => _syncer;
    }
}
