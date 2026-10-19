namespace SmartTableSync.Sql.Keys;

/// <summary>
/// Builds a keyset (seek) pagination predicate for any number of ordered primary key
/// columns. Pure - no I/O, no SqlConnection.
///
/// For columns [c1, c2, ..., cn] with parameters [@p1, @p2, ..., @pn]:
///   c1 > @p1
///   OR (c1 = @p1 AND c2 > @p2)
///   OR (c1 = @p1 AND c2 = @p2 AND c3 > @p3)
///   ...
/// This is the standard lexicographic "seek" predicate: a row qualifies if it comes after
/// the last-seen key in column order, matching how "ORDER BY c1, c2, ..." would sort rows.
/// </summary>
internal static class KeysetPredicateBuilder
{
    /// <param name="tableAlias">Optional alias (e.g. "CT") to qualify every key column with,
    /// for queries that join more than one table.</param>
    public static string Build(IReadOnlyList<string> primaryKeyColumns, IReadOnlyList<string> parameterNames, string? tableAlias = null)
    {
        if (primaryKeyColumns.Count == 0)
        {
            throw new ArgumentException("At least one primary key column is required.", nameof(primaryKeyColumns));
        }

        if (primaryKeyColumns.Count != parameterNames.Count)
        {
            throw new ArgumentException("primaryKeyColumns and parameterNames must have the same length.");
        }

        var clauses = new List<string>(primaryKeyColumns.Count);

        for (var i = 0; i < primaryKeyColumns.Count; i++)
        {
            var parts = new List<string>(i + 1);
            for (var j = 0; j < i; j++)
            {
                parts.Add($"{Column(primaryKeyColumns[j], tableAlias)} = {parameterNames[j]}");
            }

            parts.Add($"{Column(primaryKeyColumns[i], tableAlias)} > {parameterNames[i]}");

            clauses.Add(i == 0 ? parts[0] : $"({string.Join(" AND ", parts)})");
        }

        return string.Join("\n    OR ", clauses);
    }

    /// <summary>Builds the matching "ORDER BY c1, c2, ..." clause - keyset pagination
    /// requires the query's sort order to match the predicate's column order exactly.</summary>
    public static string BuildOrderBy(IReadOnlyList<string> primaryKeyColumns, string? tableAlias = null) =>
        string.Join(", ", primaryKeyColumns.Select(c => Column(c, tableAlias)));

    /// <summary>Builds the "key as a single string" SELECT expression used to compare
    /// parent/child key sets for delete reconciliation, joined with '|' for composite keys -
    /// the same format as CompositeKey. Every conversion is lossless and culture-independent,
    /// so two different keys never produce the same string and
    /// SqlTypeMapping.ParseKeyComponent parses each component back to its exact value:
    /// string columns as they are, uniqueidentifier in its 36-character form, date/time
    /// types as ISO 8601 (style 126, full fractional precision), numbers with '.' as the
    /// decimal separator.</summary>
    /// <param name="columnType">Returns a key column's sys.types name (e.g. "datetime2").</param>
    public static string BuildKeySelectExpression(IReadOnlyList<string> primaryKeyColumns, Func<string, string> columnType)
    {
        var parts = primaryKeyColumns.Select(c => KeyComponentExpression(SqlIdentifier.Quote(c), columnType(c)));
        return string.Join(" + '|' + ", parts);
    }

    private static string KeyComponentExpression(string quotedColumn, string sqlTypeName) => sqlTypeName.ToLowerInvariant() switch
    {
        "nvarchar" or "varchar" or "nchar" or "char" or "text" or "ntext" => quotedColumn,
        "int" or "bigint" or "smallint" or "tinyint" => $"CAST({quotedColumn} AS NVARCHAR(20))",
        "decimal" or "numeric" => $"CONVERT(NVARCHAR(40), {quotedColumn})",
        "uniqueidentifier" => $"CONVERT(NVARCHAR(36), {quotedColumn})",
        "datetime" or "datetime2" or "smalldatetime" or "date" or "datetimeoffset" or "time" =>
            $"CONVERT(NVARCHAR(40), {quotedColumn}, 126)",
        _ => throw new NotSupportedException($"Unsupported key column type for delete reconciliation: {sqlTypeName}"),
    };

    private static string Column(string name, string? tableAlias) =>
        tableAlias is null ? SqlIdentifier.Quote(name) : $"{tableAlias}.{SqlIdentifier.Quote(name)}";
}
