namespace SmartTableSync.Scaffolding;

/// <summary>
/// Pure SQL-text generation from an introspected TableProfile — no I/O, no database
/// connection, testable without a live SQL Server. The scaffold tool (SmartTableSync.
/// Scaffold) is what actually writes the returned text to a .sql file for human review;
/// nothing here ever executes DDL.
/// </summary>
internal interface ISqlArtifactGenerator
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

    /// <summary>Generates the one-time DDL an operator applies BY HAND against the PARENT
    /// database to opt a table into the RowVersion incremental strategy: an ALTER TABLE
    /// adding the rowversion column (skipped if profile.RowVersionColumn already shows one),
    /// plus the nonclustered index it needs to avoid a full scan every incremental cycle.
    /// Never executed by any SmartTableSync tooling in either Manual or Auto mode — the
    /// parent database is the customer's own live application database, and neither mode has
    /// (or should have) DDL access to it. Purely generated text for review, same trust model
    /// as sql/07_permissions.sql.</summary>
    string GenerateRowVersionSetupScript(TableProfile profile, string columnName = "RowVer");

    /// <summary>Generates the one-time script a DBA applies BY HAND against the PARENT database
    /// to opt a table into the SqlChangeTracking incremental strategy: database-level
    /// CHANGE_TRACKING = ON (skipped if already on — retention/auto-cleanup are database-wide,
    /// so an existing setting is left alone), table-level ENABLE CHANGE_TRACKING with
    /// TRACK_COLUMNS_UPDATED = OFF, and the VIEW CHANGE TRACKING grant the runtime identity
    /// needs to read CHANGETABLE. Same trust model as GenerateRowVersionSetupScript — never
    /// executed by SmartTableSync in either mode.</summary>
    string GenerateChangeTrackingSetupScript(
        TableProfile profile, int retentionDays = 2, string runtimePrincipal = "smarttablesync-runtime");
}
