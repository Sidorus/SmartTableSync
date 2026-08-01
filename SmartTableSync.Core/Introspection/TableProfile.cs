namespace SmartTableSync.Introspection;

/// <summary>
/// Everything the sync-plan heuristic (Planning/) needs to know about one table,
/// derived by inspecting the parent database's schema rather than hand-declared.
///
/// Produced two ways — see ISchemaReader:
///   - GetFingerprintInputsAsync: Columns + PrimaryKeyColumns + IdentityColumn only
///     (cheap, catalog-metadata-only — safe to run on every cold start to detect drift).
///   - ProfileTableAsync: the above plus IndexedLobColumns, HasIncomingForeignKeys,
///     RowCount, AvgRowSizeBytes, MaxLobColumnSizeBytes (adds row/size sampling —
///     only run when (re)provisioning is actually needed).
/// </summary>
public sealed record TableProfile(
    string TableName,
    IReadOnlyList<ColumnProfile> Columns,
    IReadOnlyList<string> PrimaryKeyColumns,
    string? IdentityColumn,
    IReadOnlyList<string> IndexedLobColumns,
    bool HasIncomingForeignKeys,
    long RowCount,
    long AvgRowSizeBytes,
    IReadOnlyDictionary<string, long> MaxLobColumnSizeBytes)
{
    /// <summary>Largest observed row size across all LOB columns — the value the
    /// batch-size heuristic must key off instead of AvgRowSizeBytes, since an average
    /// hides the rare oversized-row case (e.g. a handful of ~50 MB rows among many
    /// small ones).</summary>
    public long MaxLobRowSizeBytes => MaxLobColumnSizeBytes.Count == 0 ? 0 : MaxLobColumnSizeBytes.Values.Max();

    /// <summary>The table's rowversion/timestamp column, if it has one. SQL Server allows at
    /// most one per table and always reports its sys.types name as "timestamp" (rowversion is
    /// just a Transact-SQL keyword synonym, not a distinct catalog type) — detecting it is a
    /// fact, not a guess, since the type itself proves the engine maintains it on every write.
    /// Single source of truth for: preferring the RowVersion incremental strategy over a
    /// datetime match, and excluding this column from generated INSERT/UPDATE/bulk-copy column
    /// lists, since SQL Server rejects any explicit value written into a rowversion column.</summary>
    public string? RowVersionColumn => Columns.FirstOrDefault(c =>
        string.Equals(c.SqlTypeName, "timestamp", StringComparison.OrdinalIgnoreCase))?.Name;
}
