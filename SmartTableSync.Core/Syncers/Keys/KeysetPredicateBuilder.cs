namespace SmartTableSync.Syncers.Keys;

/// <summary>
/// Builds a keyset (seek) pagination predicate for any number of ordered primary key
/// columns, generalizing what was previously a hand-written 2-column OR clause specific
/// to DocumentAsset's composite key. Pure — no I/O, no SqlConnection.
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

    /// <summary>Builds the matching "ORDER BY c1, c2, ..." clause — keyset pagination
    /// requires the query's sort order to match the predicate's column order exactly.</summary>
    public static string BuildOrderBy(IReadOnlyList<string> primaryKeyColumns, string? tableAlias = null) =>
        string.Join(", ", primaryKeyColumns.Select(c => Column(c, tableAlias)));

    /// <summary>Builds the "key as a single string" SELECT expression used to compare
    /// parent/child key sets for delete reconciliation: no CAST for a lone string-typed key
    /// column (matches ProductCatalogItem's original "SELECT [Sku]"), CAST to NVARCHAR for
    /// non-string columns, concatenated with '|' for composite keys (matches DocumentAsset's
    /// original "CAST([DocumentId] AS NVARCHAR(20)) + '|' + [LanguageCode]").</summary>
    public static string BuildKeySelectExpression(IReadOnlyList<string> primaryKeyColumns, Func<string, bool> isStringColumn)
    {
        var parts = primaryKeyColumns.Select(c => isStringColumn(c) ? $"[{c}]" : $"CAST([{c}] AS NVARCHAR(20))");
        return string.Join(" + '|' + ", parts);
    }

    private static string Column(string name, string? tableAlias) =>
        tableAlias is null ? SqlIdentifier.Quote(name) : $"{tableAlias}.{SqlIdentifier.Quote(name)}";
}
