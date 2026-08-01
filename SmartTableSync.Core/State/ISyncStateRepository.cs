namespace SmartTableSync.State;

public interface ISyncStateRepository
{
    Task<DateTime> GetLastSyncDateAsync(
        SqlConnection conn, string tableName, CancellationToken ct = default);

    Task UpdateLastSyncDateAsync(
        SqlConnection conn, string tableName, DateTime syncDate, CancellationToken ct = default);

    /// <summary>Generic, opaque position string for strategies whose "where did we leave off"
    /// value isn't a DateTime (e.g. RowVersionSyncRunner's hex-encoded rowversion) — storage-
    /// agnostic, separate from LastSyncDate, which keeps its own unrelated role regardless of
    /// which incremental strategy a table uses. Null when no position has been recorded yet.</summary>
    Task<string?> GetIncrementalPositionAsync(
        SqlConnection conn, string tableName, CancellationToken ct = default);

    Task UpdateIncrementalPositionAsync(
        SqlConnection conn, string tableName, string position, CancellationToken ct = default);
}
