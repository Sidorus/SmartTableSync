namespace SmartTableSync.Scaffolding;

/// <summary>
/// Pure SQL-text generation from an introspected TableProfile — no I/O, no database
/// connection, testable without a live SQL Server. The scaffold tool (SmartTableSync.
/// Scaffold) is what actually writes the returned text to a .sql file for human review;
/// nothing here ever executes DDL.
/// </summary>
public interface ISqlArtifactGenerator
{
    string GenerateCreateTableScript(TableProfile profile);
    string GenerateTvpUdtScript(TableProfile profile, string typeName);
    string GenerateMergeProcedureScript(TableProfile profile, SyncPlan plan);

    /// <summary>Table-agnostic — no TableProfile needed. Produces the fixed, idempotent DDL
    /// for the three tables every managed table depends on regardless of which business
    /// tables are synced: CustomSyncState/SyncBatchLog (sync-cursor and initial-load-resume
    /// tracking) and SyncTableConfig (persisted plan). Neither Auto mode's
    /// IChildDdlProvisioner nor Manual mode's Scaffold CLI create these as a side effect of
    /// provisioning a business table — this is the one place that does.</summary>
    string GenerateInfrastructureScript();
}
