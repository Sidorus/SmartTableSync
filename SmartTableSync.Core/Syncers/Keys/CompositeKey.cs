namespace SmartTableSync.Syncers.Keys;

/// <summary>
/// A primary key as one string — the key columns' values in key order, joined with '|'
/// (e.g. "206|en"). Used as the resume position between pages and as a row's identity in
/// delete reconciliation. Pure — no I/O. "No position yet" is null, handled by the callers.
/// </summary>
internal static class CompositeKey
{
    private const char Delimiter = '|';

    // "FFFFFFF" omits trailing zeros (and the '.' when there is no fraction), like SQL
    // Server's style 126 does for datetime.
    private const string IsoDateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF";

    /// <summary>Builds the resume-cursor string for a row, e.g. "206|en" for a 2-column key.
    /// Formats with InvariantCulture explicitly — SqlTypeMapping.ParseKeyComponent parses
    /// with InvariantCulture, and a bare ToString() defaults to CurrentCulture, which can
    /// mismatch (and fail to round-trip) for DateTime/DateTimeOffset/decimal-typed keys on a
    /// host whose culture isn't invariant/en-US-compatible. DateTime/DateTimeOffset use ISO
    /// 8601 with full fractional precision (the invariant default drops fractional seconds),
    /// the same format KeysetPredicateBuilder.BuildKeySelectExpression gets from SQL Server.</summary>
    public static string Extract(DataRow row, IReadOnlyList<string> primaryKeyColumns) =>
        string.Join(Delimiter, primaryKeyColumns.Select(c => row[c] switch
        {
            DateTime d => d.ToString(IsoDateTimeFormat, System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset d => d.ToString(IsoDateTimeFormat + "zzz", System.Globalization.CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            var v => v!.ToString(),
        }));

    /// <summary>Splits a non-null resume-cursor string back into its per-column string
    /// components, in the same order as primaryKeyColumns.</summary>
    public static string[] Parse(string key, IReadOnlyList<string> primaryKeyColumns)
    {
        var parts = key.Split(Delimiter, primaryKeyColumns.Count);
        if (parts.Length != primaryKeyColumns.Count)
        {
            throw new FormatException(
                $"Resume key '{key}' has {parts.Length} component(s), expected {primaryKeyColumns.Count} " +
                $"for columns [{string.Join(",", primaryKeyColumns)}].");
        }

        return parts;
    }
}
