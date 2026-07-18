namespace SmartTableSync.State;

public interface ISyncStateRepository
{
    Task<DateTime> GetLastSyncDateAsync(
        SqlConnection conn, string tableName, CancellationToken ct = default);

    Task UpdateLastSyncDateAsync(
        SqlConnection conn, string tableName, DateTime syncDate, CancellationToken ct = default);
}
