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
}
