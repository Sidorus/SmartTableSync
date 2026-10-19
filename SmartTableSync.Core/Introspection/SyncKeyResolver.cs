namespace SmartTableSync.Introspection;

/// <summary>
/// Picks a table's sync key. Pure, so every rule is unit-tested without a database:
/// <list type="number">
/// <item>The PRIMARY KEY constraint, when the table has one. Configured key columns for such a
/// table are a mistake and fail, rather than being silently ignored.</item>
/// <item>The configured key columns (SmartTableSync:KeyColumns:&lt;Table&gt;): an explicit choice
/// wins over an inferred one. They must name existing, NOT NULL, non-LOB, non-rowversion columns,
/// each once - the child declares them as its PRIMARY KEY and the engine copies their values.</item>
/// <item>The best unique index (SchemaReader picks it).</item>
/// </list>
/// A table with none of these gets an empty key; SyncPlanHeuristic refuses it with a message that
/// says how to fix it.
/// </summary>
internal static class SyncKeyResolver
{
    public static (IReadOnlyList<string> Columns, SyncKeySource Source) Resolve(
        string tableName,
        IReadOnlyList<ColumnProfile> columns,
        IReadOnlyList<string> primaryKeyColumns,
        IReadOnlyList<string>? configuredKeyColumns,
        IReadOnlyList<string> uniqueIndexColumns)
    {
        var hasConfiguredKey = configuredKeyColumns is { Count: > 0 };

        if (primaryKeyColumns.Count > 0)
        {
            return hasConfiguredKey
                ? throw new InvalidOperationException(
                    $"{tableName}: SmartTableSync:KeyColumns is set, but the table has a PRIMARY KEY " +
                    $"({string.Join(", ", primaryKeyColumns)}), which is always the sync key. Remove the setting for this table.")
                : (primaryKeyColumns, SyncKeySource.PrimaryKey);
        }

        if (hasConfiguredKey)
        {
            return (ValidateConfigured(tableName, columns, configuredKeyColumns!), SyncKeySource.Configured);
        }

        // No key at all keeps the PrimaryKey source with an empty list: the profile such a table
        // always had, so its fingerprint doesn't change.
        return uniqueIndexColumns.Count > 0
            ? (uniqueIndexColumns, SyncKeySource.UniqueIndex)
            : (uniqueIndexColumns, SyncKeySource.PrimaryKey);
    }

    /// <summary>Returns the configured columns spelled as the table spells them, or throws with
    /// every problem found.</summary>
    private static string[] ValidateConfigured(
        string tableName, IReadOnlyList<ColumnProfile> columns, IReadOnlyList<string> configuredKeyColumns)
    {
        var byName = columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        var resolved = new List<string>();

        foreach (var name in configuredKeyColumns)
        {
            if (!byName.TryGetValue(name, out var column))
            {
                problems.Add($"column {name} doesn't exist");
            }
            else if (resolved.Contains(column.Name, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"column {column.Name} is listed more than once");
            }
            else
            {
                if (column.IsNullable)
                    problems.Add($"column {column.Name} allows NULL");
                if (column.IsLob)
                    problems.Add($"column {column.Name} is a (max) type, which can't be a key column");
                if (string.Equals(column.SqlTypeName, "timestamp", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"column {column.Name} is a rowversion column, whose values can't be copied");

                resolved.Add(column.Name);
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"{tableName}: SmartTableSync:KeyColumns ({string.Join(", ", configuredKeyColumns)}) can't be used: " +
                $"{string.Join("; ", problems)}. Key columns must exist and be NOT NULL.");
        }

        return [.. resolved];
    }
}
