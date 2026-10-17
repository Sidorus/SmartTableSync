namespace SmartTableSync.Orchestration;

/// <summary>What one sync step did to the child table. Merged counts inserted or updated rows,
/// Deleted counts removed rows, Errored counts rows that failed. HasRetryableErrors means at
/// least one failure is expected to succeed on a later run (for example an FK timing race), so
/// the caller must not move its position past it.</summary>
internal readonly record struct SyncOutcome(int Merged, int Deleted, int Errored, bool HasRetryableErrors)
{
    public static SyncOutcome None => default;

    public static SyncOutcome operator +(SyncOutcome left, SyncOutcome right) => new(
        left.Merged + right.Merged,
        left.Deleted + right.Deleted,
        left.Errored + right.Errored,
        left.HasRetryableErrors || right.HasRetryableErrors);
}
