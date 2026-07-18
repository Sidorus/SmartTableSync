namespace SmartTableSync.Configuration;

public interface ISyncTableConfigRepository
{
    Task<SyncTableConfig?> GetAsync(SqlConnection child, string tableName, CancellationToken ct);
    Task UpsertAsync(SqlConnection child, SyncTableConfig config, CancellationToken ct);
}
