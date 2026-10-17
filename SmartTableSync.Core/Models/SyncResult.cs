namespace SmartTableSync.Models;

/// <summary>One table's sync pass, as reported to the orchestrator. RowsSynced counts inserted or
/// updated rows; RowsDeleted counts rows deleted during the pass itself (SqlChangeTracking reports
/// deletes directly), not by the separate periodic delete reconciliation.</summary>
internal sealed record SyncResult(
    string TableName,
    int RowsSynced,
    int RowsErrored,
    bool HasRetryableErrors,
    bool IsInitialSync,
    TimeSpan Duration,
    int RowsDeleted = 0);
