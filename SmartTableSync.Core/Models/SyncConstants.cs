namespace SmartTableSync.Models;

internal static class SyncConstants
{
    /// <summary>Sentinel stored in CustomSyncState before the first full sync runs.</summary>
    public static readonly DateTime InitialDate = new(2000, 1, 1);

    public static bool IsInitial(DateTime lastSyncDate) => lastSyncDate == InitialDate;
}
