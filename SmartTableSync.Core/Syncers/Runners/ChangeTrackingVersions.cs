using System.Globalization;

namespace SmartTableSync.Syncers.Runners;

/// <summary>Shared SQL Server Change Tracking helpers for ChangeTrackingSyncRunner and
/// ChangeTrackingBaselineSyncRunner: the persisted position format and the version-bound
/// queries both runners need.</summary>
internal static class ChangeTrackingVersions
{
    // Prefixed so a position left behind by a different strategy (RowVersionSyncRunner stores
    // "0x..." hex in the same CustomSyncState.IncrementalPosition column) is never misread as a
    // CT version after a table switches strategy — it parses as "no position" instead, which
    // triggers a safe full re-scan + re-baseline.
    private const string PositionPrefix = "CT:";

    public static string FormatPosition(long version) =>
        PositionPrefix + version.ToString(CultureInfo.InvariantCulture);

    public static long? ParsePosition(string? position) =>
        position is not null
        && position.StartsWith(PositionPrefix, StringComparison.Ordinal)
        && long.TryParse(position.AsSpan(PositionPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : null;

    /// <summary>CHANGE_TRACKING_MIN_VALID_VERSION for the table plus the database-wide
    /// CHANGE_TRACKING_CURRENT_VERSION, in one round trip (on the caller's transaction, if any,
    /// so both see the same snapshot as the read that follows). SQL Server returns NULL for the
    /// first when CT isn't enabled on the table or the caller lacks VIEW CHANGE TRACKING, and
    /// for the second when CT isn't enabled on the database — both are configuration errors
    /// worth a clear message, not a silent fallback.</summary>
    public static async Task<(long MinValidVersion, long CurrentVersion)> GetBoundsAsync(
        SqlConnection parent, SqlTransaction? transaction, string tableName, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@TableName)), CHANGE_TRACKING_CURRENT_VERSION()",
            parent, transaction) { CommandTimeout = 60 };
        cmd.AddParam("@TableName", SqlDbType.NVarChar, tableName, 256);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        if (await reader.IsDBNullAsync(0, ct) || await reader.IsDBNullAsync(1, ct))
            throw new InvalidOperationException(
                $"{tableName}: SQL Server Change Tracking is not enabled on this table (or its database), or the " +
                $"runtime identity lacks VIEW CHANGE TRACKING on it — see sql/07_permissions.sql.");

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    public static async Task<bool> IsSnapshotIsolationAllowedAsync(SqlConnection parent, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID()", parent) { CommandTimeout = 30 };
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null and not DBNull && Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
    }
}
