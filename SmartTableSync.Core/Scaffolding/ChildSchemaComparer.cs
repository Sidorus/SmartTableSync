namespace SmartTableSync.Scaffolding;

/// <summary>What it takes to bring an existing child table in line with the parent.</summary>
/// <param name="ColumnsToAdd">Parent columns the child doesn't have yet; adding them is safe.</param>
/// <param name="ExtraNullableColumns">Child columns no longer in the parent. Being nullable, they
/// don't block inserts, so they're left in place rather than dropped with their data.</param>
/// <param name="Problems">Changes that can't be applied without risking data (a changed type or
/// length, a changed primary key, a column the parent dropped that is NOT NULL in the child, a
/// column that now allows NULL in the parent but not in the child, a decimal or numeric column
/// with a different precision or scale). Empty when the child can be brought in line
/// automatically.</param>
/// <param name="ColumnsToRetype">decimal and numeric columns whose only difference is precision or
/// scale, with the parent's type. Also listed in Problems; the scaffold tool writes an ALTER
/// COLUMN script for them.</param>
internal sealed record ChildSchemaDiff(
    IReadOnlyList<ColumnProfile> ColumnsToAdd,
    IReadOnlyList<string> ExtraNullableColumns,
    IReadOnlyList<string> Problems,
    IReadOnlyList<ColumnProfile> ColumnsToRetype);

/// <summary>Compares a parent table with its existing child copy. Pure, so every rule is
/// unit-tested without a database. The child's own IDENTITY property is not compared: child
/// tables are created without it on purpose (ADR-0018).</summary>
internal static class ChildSchemaComparer
{
    public static ChildSchemaDiff Compare(TableProfile parent, TableProfile child)
    {
        var childColumns = child.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var parentColumnNames = parent.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columnsToAdd = new List<ColumnProfile>();
        var extraNullableColumns = new List<string>();
        var problems = new List<string>();
        var columnsToRetype = new List<ColumnProfile>();

        foreach (var parentColumn in parent.Columns)
        {
            if (!childColumns.TryGetValue(parentColumn.Name, out var childColumn))
            {
                columnsToAdd.Add(parentColumn);
            }
            else if (OnlyPrecisionOrScaleDiffers(parentColumn, childColumn))
            {
                columnsToRetype.Add(parentColumn);
                problems.Add($"column {parentColumn.Name} is {Describe(childColumn)} in the child but {Describe(parentColumn)} in the parent, " +
                    "so its values are rounded; apply the scaffold tool's <Table>_AlterColumns.sql");
            }
            else if (!SameType(parentColumn, childColumn))
            {
                problems.Add($"column {parentColumn.Name} changed from {Describe(childColumn)} to {Describe(parentColumn)}");
            }
            else if (parentColumn.IsNullable && !childColumn.IsNullable)
            {
                // The other direction is fine: a nullable child column accepts every parent value.
                problems.Add($"column {parentColumn.Name} now allows NULL in the parent but not in the child");
            }
        }

        foreach (var childColumn in child.Columns.Where(c => !parentColumnNames.Contains(c.Name)))
        {
            if (childColumn.IsNullable)
            {
                extraNullableColumns.Add(childColumn.Name);
            }
            else
            {
                problems.Add($"column {childColumn.Name} no longer exists in the parent but is NOT NULL in the child, so new rows can't be inserted");
            }
        }

        if (!parent.PrimaryKeyColumns.SequenceEqual(child.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"primary key changed from ({string.Join(", ", child.PrimaryKeyColumns)}) to ({string.Join(", ", parent.PrimaryKeyColumns)})");
        }

        return new ChildSchemaDiff(columnsToAdd, extraNullableColumns, problems, columnsToRetype);
    }

    /// <summary>True when an existing table type has exactly the columns the merge procedure
    /// sends, in the same order: SqlClient maps a table-valued parameter's columns by position.</summary>
    public static bool TableTypeMatches(TableProfile parent, IReadOnlyList<ColumnProfile> tableTypeColumns)
    {
        var expected = SqlArtifactGenerator.InsertableColumns(parent);

        return expected.Count == tableTypeColumns.Count
            && expected.Zip(tableTypeColumns).All(pair =>
                string.Equals(pair.First.Name, pair.Second.Name, StringComparison.OrdinalIgnoreCase)
                && SameType(pair.First, pair.Second)
                && pair.First.IsNullable == pair.Second.IsNullable);
    }

    // decimal(18,0) and decimal(10,2) both take 9 bytes, so for decimal and numeric the length
    // alone can't tell them apart: precision and scale are compared too. Other types with a
    // precision (datetime2, time, datetimeoffset) aren't: a wider child column loses nothing.
    private static bool SameType(ColumnProfile a, ColumnProfile b) =>
        string.Equals(a.SqlTypeName, b.SqlTypeName, StringComparison.OrdinalIgnoreCase)
        && a.MaxLength == b.MaxLength
        && (!a.IsExactNumeric || (a.Precision == b.Precision && a.Scale == b.Scale));

    private static bool OnlyPrecisionOrScaleDiffers(ColumnProfile parent, ColumnProfile child) =>
        parent.IsExactNumeric
        && string.Equals(parent.SqlTypeName, child.SqlTypeName, StringComparison.OrdinalIgnoreCase)
        && (parent.Precision != child.Precision || parent.Scale != child.Scale);

    private static string Describe(ColumnProfile column) =>
        column.IsExactNumeric ? $"{column.SqlTypeName}({column.Precision},{column.Scale})"
        : column.MaxLength == -1 ? $"{column.SqlTypeName}(max)"
        : $"{column.SqlTypeName}({column.MaxLength} bytes)";
}
