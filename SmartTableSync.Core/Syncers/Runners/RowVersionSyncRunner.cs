namespace SmartTableSync.Syncers.Runners;

/// <summary>RowVersion strategy: rows whose rowversion column is at or after the stored
/// position. SQL Server maintains that column on every write, so no change can be missed by
/// application code forgetting to update it. The position lives in
/// CustomSyncState.IncrementalPosition as "0x" + hex.</summary>
public sealed class RowVersionSyncRunner : ChangeWindowSyncRunner<byte[]>
{
    private const string HexPrefix = "0x";

    private readonly ISyncStateRepository _syncState;

    public RowVersionSyncRunner(
        TableProfile profile, SyncPlan plan, TableSyncCommandFactory commands, IBatchMergeStrategy mergeStrategy,
        ISyncStateRepository syncState, ILogger logger)
        : base(profile, plan, commands, mergeStrategy, logger)
    {
        _syncState = syncState;
    }

    /// <summary>MIN_ACTIVE_ROWVERSION(), not @@DBTS: a transaction still in flight commits with
    /// a rowversion at or above this value, so a window ending here never skips it. @@DBTS (the
    /// last value handed out) can be above such a row and would skip it forever.</summary>
    public static async Task<byte[]> CaptureMinActiveRowVersionAsync(SqlConnection parent, CancellationToken ct)
    {
        await using var capture = new SqlCommand("SELECT MIN_ACTIVE_ROWVERSION()", parent);
        return (byte[])(await capture.ExecuteScalarAsync(ct))!;
    }

    public static string FormatPosition(byte[] rowVersion) => HexPrefix + Convert.ToHexString(rowVersion);

    public static bool IsRowVersionPosition(string? storedPosition) =>
        storedPosition is not null && storedPosition.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase);

    // The new position is captured BEFORE reading, so rows committed during the read fall
    // inside the next window.
    protected override async Task<ChangeWindow<byte[]>> OpenWindowAsync(
        SqlConnection parent, SqlConnection child, DateTime lastSyncDate, CancellationToken ct)
    {
        var storedPosition = await _syncState.GetIncrementalPositionAsync(child, Profile.TableName, ct);
        var to = await CaptureMinActiveRowVersionAsync(parent, ct);

        return new ChangeWindow<byte[]>(From: ParsePosition(storedPosition), To: to);
    }

    protected override Task<DataTable> ReadPageAsync(
        SqlConnection parent, ChangeWindow<byte[]> window, string? afterKey, int pageSize, CancellationToken ct) =>
        LoadPageAsync(Commands.BuildRowVersionPageCommand(parent, window.From, afterKey, pageSize), ct);

    protected override Task CommitWindowAsync(SqlConnection child, ChangeWindow<byte[]> window, CancellationToken ct) =>
        _syncState.UpdateIncrementalPositionAsync(child, Profile.TableName, FormatPosition(window.To), ct);

    // No position yet, or one written by another strategy before this table switched to
    // RowVersion (e.g. Change Tracking's "CT:<version>"): start from the lowest rowversion.
    private static byte[] ParsePosition(string? storedPosition) =>
        IsRowVersionPosition(storedPosition)
            ? Convert.FromHexString(storedPosition![HexPrefix.Length..])
            : new byte[8];
}
