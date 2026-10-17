namespace SmartTableSync.Configuration;

internal interface ISyncTableConfigRepository
{
    Task<SyncTableConfig?> GetAsync(SqlConnection child, string tableName, CancellationToken ct);
    Task UpsertAsync(SqlConnection child, SyncTableConfig config, CancellationToken ct);
}
