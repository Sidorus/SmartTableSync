namespace SmartTableSync.Models;

public sealed record SyncResult(
    string   TableName,
    int      RowsSynced,
    int      RowsErrored,
    bool     HasRetryableErrors,
    bool     IsInitialSync,
    TimeSpan Duration);
