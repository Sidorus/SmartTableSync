namespace SmartTableSync.Configuration;

/// <summary>Resolves the change-tracking column name for a table from configuration + schema,
/// shared by SyncTableProvisioner (runtime) and Scaffold's Program.cs (CLI) so both apply
/// identical validation instead of Scaffold re-deriving its own partial approximation. An
/// explicitly configured column (not just the ambient "LastUpdate" default) that exists but
/// isn't datetime-typed is a real operator mistake — it's rejected immediately rather than
/// silently accepted and left to fail on every incremental cycle forever
/// (TableSyncCommandFactory.BuildIncrementalCommand binds the comparison as SqlDbType.DateTime).</summary>
internal static class TimestampColumnResolver
{
    public static string? Resolve(TableProfile profile, IReadOnlyDictionary<string, string> changeTrackingColumns)
    {
        var wasExplicitlyConfigured = changeTrackingColumns.TryGetValue(profile.TableName, out var configuredName);
        var columnName = configuredName ?? "LastUpdate";
        var column = profile.Columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));

        if (column is null)
            return null;

        if (SqlTypeMapping.IsDateTimeFamily(column.SqlTypeName))
            return columnName;

        if (wasExplicitlyConfigured)
            throw new InvalidOperationException(
                $"{profile.TableName}: SmartTableSync:TimestampColumns:{profile.TableName} names column " +
                $"'{columnName}', but its type ('{column.SqlTypeName}') isn't a datetime type — point it at a " +
                $"datetime/datetime2/smalldatetime/date column instead.");

        // The ambient "LastUpdate" default happening to name a wrong-typed column is a naming
        // coincidence, not a deliberate operator choice - treat it the same as "doesn't exist"
        // rather than failing a table nobody configured anything for.
        return null;
    }
}
