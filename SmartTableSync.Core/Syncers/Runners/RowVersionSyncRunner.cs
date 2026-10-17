namespace SmartTableSync.Syncers.Runners;

/// <summary>Runs the incremental-sync path for a table with a rowversion/timestamp column —
/// an exact, engine-maintained cutoff instead of a windowed datetime query. Owns its own
/// position state entirely via ISyncStateRepository (CustomSyncState.IncrementalPosition),
/// independent of LastSyncDate, the same way InitialSyncRunner already owns its own
/// resume-key state via IBatchLogger rather than routing through SyncOrchestrator.</summary>
public sealed class RowVersionSyncRunner : ISyncRunner
{
    private readonly TableProfile _profile;
    private readonly TableSyncCommandFactory _commandFactory;
    private readonly IBatchMergeStrategy _mergeStrategy;
    private readonly ISyncStateRepository _syncState;
    private readonly ILogger _logger;

    public RowVersionSyncRunner(
        TableProfile profile, TableSyncCommandFactory commandFactory, IBatchMergeStrategy mergeStrategy,
        ISyncStateRepository syncState, ILogger logger)
    {
        _profile = profile;
        _commandFactory = commandFactory;
        _mergeStrategy = mergeStrategy;
        _syncState = syncState;
        _logger = logger;
    }

    // lastSyncDate is unused — this strategy's position is CustomSyncState.IncrementalPosition,
    // a separate, opaque value this runner owns entirely. Present only so this satisfies
    // ISyncRunner.
    public async Task<(int synced, int errored, bool retryable)> RunAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct)
    {
        var persistedPosition = await _syncState.GetIncrementalPositionAsync(child, _profile.TableName, ct);
        // A position that isn't "0x..." hex was written by another strategy (e.g.
        // ChangeTrackingSyncRunner's "CT:<version>") before this table switched to RowVersion —
        // treat it like no position at all (start from 0x00...00) rather than misparse it.
        var lastRowVersion = persistedPosition is null || !persistedPosition.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? new byte[8]
            : FromHexString(persistedPosition);

        // Captured BEFORE the read, not after — any transaction still in flight right now is
        // guaranteed to commit with a rowversion >= this bound, so it remains visible to the
        // NEXT cycle's "> position" filter. Capturing after the read (or using @@DBTS, the
        // last-assigned rather than lowest-in-flight version) risks permanently skipping a
        // row that committed between the read and the capture.
        var newPosition = await GetMinActiveRowVersionAsync(parent, ct);

        await using var cmd = _commandFactory.BuildRowVersionIncrementalCommand(parent, lastRowVersion);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        using var batch = await DataTableLoader.LoadAsync(reader, ct);

        if (batch.Rows.Count == 0)
        {
            await _syncState.UpdateIncrementalPositionAsync(child, _profile.TableName, ToHexString(newPosition), ct);
            return (0, 0, false);
        }

        var (synced, errored, retryable) = await _mergeStrategy.MergeBatchAsync(batch, child, ct);

        // Only advance once this window is fully processed — a retryable error (e.g. FK
        // timing race) must keep the old position so the failed rows remain inside the next
        // cycle's "> position" filter and get retried. Unlike the datetime strategy's 1-minute
        // overlap, rowversion filtering is an exact cutoff with no cushion of its own, so
        // not advancing here is the only thing that prevents permanently losing those rows.
        // MERGE is idempotent, so re-processing this window's already-succeeded rows next
        // cycle is harmless.
        if (!retryable)
            await _syncState.UpdateIncrementalPositionAsync(child, _profile.TableName, ToHexString(newPosition), ct);

        return (synced, errored, retryable);
    }

    private static async Task<byte[]> GetMinActiveRowVersionAsync(SqlConnection parent, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT MIN_ACTIVE_ROWVERSION()", parent);
        return (byte[])(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static string ToHexString(byte[] bytes) => "0x" + Convert.ToHexString(bytes);

    private static byte[] FromHexString(string hex) =>
        Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex);
}
