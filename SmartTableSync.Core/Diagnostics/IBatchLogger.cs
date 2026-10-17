namespace SmartTableSync.Diagnostics;

internal interface IBatchLogger
{
    Task<string?> GetResumeKeyAsync(
        SqlConnection conn, string tableName, CancellationToken ct = default);

    Task<int> GetNextBatchNumberAsync(
        SqlConnection conn, string tableName, CancellationToken ct = default);

    Task<int> GetTotalSyncedAsync(
        SqlConnection conn, string tableName, CancellationToken ct = default);

    Task<int> LogBatchStartAsync(
        SqlConnection conn, string tableName, int batchNumber, string? startKey, CancellationToken ct = default);

    Task LogBatchCompleteAsync(
        SqlConnection conn, int logId, int synced, int errored, string lastKey, CancellationToken ct = default);
}
