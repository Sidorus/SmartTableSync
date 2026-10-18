namespace SmartTableSync.Introspection;

internal interface ISchemaReader
{
    /// <summary>Cheap, catalog-metadata-only profile: columns/types + primary key order +
    /// identity column. Safe to run on every cold start to check for schema drift.</summary>
    Task<TableProfile> GetFingerprintInputsAsync(SqlConnection parent, string tableName, CancellationToken ct);

    /// <summary>Full profile — everything GetFingerprintInputsAsync returns, plus indexed
    /// LOB-key detection, incoming-FK detection, and row count/size sampling. Only run
    /// when (re)provisioning a table, not on every invocation.</summary>
    Task<TableProfile> ProfileTableAsync(SqlConnection parent, string tableName, CancellationToken ct);

    /// <summary>Stretch goal: scans every table in the parent database (not just the
    /// configured ones) and returns the names of tables with an indexed nvarchar(max)/
    /// varbinary(max) key column — the same catalog-only rule that excludes a table from
    /// Azure SQL Data Sync. Catalog-metadata-only, no data scan, so safe to run broadly.
    /// Does NOT check the oversized-row case (would require scanning every table's data),
    /// so a "no" from this method isn't a full guarantee — treat it as an assisted first
    /// pass, not a complete answer.</summary>
    Task<IReadOnlyList<string>> FindDataSyncIncompatibleTablesAsync(SqlConnection parent, CancellationToken ct);

    /// <summary>Cheap existence check via OBJECT_ID — used by the scaffold tool to decide
    /// whether a CREATE TABLE script is needed for the child, without the "0 columns found"
    /// exception GetFingerprintInputsAsync/ProfileTableAsync throw for a missing table.</summary>
    Task<bool> TableExistsAsync(SqlConnection connection, string tableName, CancellationToken ct);

    /// <summary>The table's IDENTITY column, or null when it has none or doesn't exist. Asked of
    /// the CHILD table to decide whether copying needs IDENTITY_INSERT.</summary>
    Task<string?> GetIdentityColumnAsync(SqlConnection connection, string tableName, CancellationToken ct);
}
