namespace SmartTableSync.Syncers.Keys;

/// <summary>
/// Generalizes DocumentAsset's hand-written "{DocumentId}|{Language}" resume-key format to
/// any number of ordered primary key columns, joined with '|'. Pure — no I/O.
///
/// Deliberately does not handle the "no resume key yet" (null) case — what the correct
/// starting value is per column depends on that column's type (0 for numeric columns,
/// matching IDENTITY columns starting at 1; empty string for string columns), which is the
/// caller's concern since it already has each column's type from TableProfile.
/// </summary>
public static class CompositeKey
{
    private const char Delimiter = '|';

    /// <summary>Builds the resume-cursor string for a row, e.g. "206|en" for a 2-column key.</summary>
    public static string Extract(DataRow row, IReadOnlyList<string> primaryKeyColumns) =>
        string.Join(Delimiter, primaryKeyColumns.Select(c => row[c]!.ToString()));

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
