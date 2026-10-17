using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SmartTableSync.Planning;
using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.Introspection;
using SmartTableSync.Diagnostics;
using SmartTableSync.Hosting;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// Regression test for the confirmed /code-review-style bug: SyncTableConfig.ToSyncPlan() used
/// to pass the stored TvpTypeName through unconditionally, even though it's only ever non-null
/// when the heuristic itself picked Tvp. A hand-set MergeStrategyOverride=Tvp on a table the
/// heuristic actually derives RowByRow for (this table has incoming FKs) left TvpTypeName null
/// forever on SyncTableProvisioner's fast path (unchanged schema fingerprint - no re-derivation)
/// - TvpBatchMergeStrategy would then target a TVP type literally named "dbo." (SqlException,
/// every batch), and in Auto mode ChildDdlProvisioner would auto-execute an invalid
/// "CREATE TYPE [dbo].[] AS TABLE (...)".
///
/// Uses its own dedicated container (IClassFixture, not the shared [Collection("SqlServer")])
/// because this test directly writes a hand-crafted dbo.SyncTableConfig row for
/// ProductAttributeValue (simulating an operator's manual DB edit / a pre-fix persisted plan) -
/// other tests in the shared collection also use ProductAttributeValue and shouldn't see this
/// row.
/// </summary>
public class MergeStrategyOverrideTests : IClassFixture<MsSqlTestFixture>
{
    private const string TableName = "ProductAttributeValue";

    private readonly MsSqlTestFixture _fixture;

    public MergeStrategyOverrideTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task MergeStrategyOverrideToTvp_WithStaleNullTvpTypeName_DerivesConventionalNameOnFastPath()
    {
        await using var parent = new SqlConnection(_fixture.ParentConnectionString);
        await using var child = new SqlConnection(_fixture.ChildConnectionString);
        await parent.OpenAsync();
        await child.OpenAsync();

        var schemaReader = new SchemaReader();
        var configRepository = new SyncTableConfigRepository();

        var cheapProfile = await schemaReader.GetFingerprintInputsAsync(parent, TableName, CancellationToken.None);
        var fingerprint = SchemaFingerprint.Compute(cheapProfile);

        // Simulates a hand-edited SyncTableConfig row (or a pre-fix persisted plan): the
        // heuristic's own choice for this table is RowByRow (it has incoming FKs), so
        // TvpTypeName is null - exactly as SyncPlanHeuristic.DerivePlan would have persisted it
        // before any override was applied. MergeStrategyOverride then forces Tvp.
        var staleConfig = new SyncTableConfig(
            TableName: TableName,
            PrimaryKeyColumns: cheapProfile.PrimaryKeyColumns,
            IdentityColumn: cheapProfile.IdentityColumn,
            MergeStrategy: MergeStrategy.RowByRow,
            BatchSize: 500,
            BulkCopyTimeoutSeconds: 60,
            RetryableSqlErrorNumbers: [547],
            TvpTypeName: null,
            SchemaFingerprint: fingerprint,
            ConfiguredAtUtc: DateTime.UtcNow,
            MergeStrategyOverride: MergeStrategy.Tvp,
            BatchSizeOverride: null,
            BulkCopyTimeoutSecondsOverride: null,
            RetryableSqlErrorsOverride: null);

        await configRepository.UpsertAsync(child, staleConfig, CancellationToken.None);

        var provisioner = new SyncTableProvisioner(
            schemaReader, new SyncPlanHeuristic(), configRepository,
            new NoOpSyncMetricsReporter(), Microsoft.Extensions.Options.Options.Create(new SyncSettings()),
            NullLogger<SyncTableProvisioner>.Instance);

        var (_, plan, wasFreshlyProvisioned) = await provisioner.EnsureProvisionedAsync(parent, child, TableName, CancellationToken.None);

        // Fingerprint matched -> the fast path (existing.ToSyncPlan(), no re-derivation) is
        // exactly what triggered the bug - confirming this proves the fix works on the actual
        // code path that broke, not just on SyncTableConfig.ToSyncPlan() in isolation.
        Assert.False(wasFreshlyProvisioned);
        Assert.Equal(MergeStrategy.Tvp, plan.MergeStrategy);
        Assert.False(string.IsNullOrEmpty(plan.TvpTypeName));
        Assert.Equal($"{TableName}TableType", plan.TvpTypeName);
    }
}
