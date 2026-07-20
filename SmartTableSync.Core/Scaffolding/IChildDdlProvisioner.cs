namespace SmartTableSync.Scaffolding;

/// <summary>Auto mode's counterpart to the manually-run SmartTableSync.Scaffold tool: given a
/// table's already-derived profile/plan, ensures the child database actually has the table,
/// TVP type (if applicable), and stored procedure it needs - executing the exact same
/// ISqlArtifactGenerator output Scaffold would otherwise only write to a file for a human to
/// review and apply by hand. Only ever called by SyncOrchestrator when SyncSettings.Mode is
/// Auto, and only on the same infrequent occasions ISyncTableProvisioner would otherwise
/// re-provision (new table or parent schema drift) - never on the routine sync path.</summary>
public interface IChildDdlProvisioner
{
    Task EnsureChildObjectsExistAsync(
        SqlConnection childProvisioning, TableProfile profile, SyncPlan plan, CancellationToken ct);

    /// <summary>Ensures the table-agnostic tracking infrastructure (CustomSyncState,
    /// SyncBatchLog, SyncTableConfig) exists — called once per run in Auto mode, before any
    /// business table is touched. Idempotent and cheap (IF NOT EXISTS-guarded), same as
    /// EnsureChildObjectsExistAsync's own per-table guards.</summary>
    Task EnsureInfrastructureExistsAsync(SqlConnection childProvisioning, CancellationToken ct);
}
