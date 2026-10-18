using SmartTableSync.Introspection;
using SmartTableSync.Scaffolding;
using Xunit;

namespace SmartTableSync.Tests.Scaffolding;

public class ChildSchemaComparerTests
{
    private static readonly ColumnProfile _id = new("Id", "int", 4, false);
    private static readonly ColumnProfile _name = new("Name", "nvarchar", 100, true);

    private static TableProfile Table(IReadOnlyList<ColumnProfile> columns, IReadOnlyList<string>? primaryKey = null) => new(
        TableName: "Product",
        Columns: columns,
        PrimaryKeyColumns: primaryKey ?? ["Id"],
        IdentityColumn: null,
        IndexedLobColumns: [],
        ReferencesOtherTables: false,
        RowCount: 0,
        AvgRowSizeBytes: 0,
        MaxLobColumnSizeBytes: new Dictionary<string, long>());

    [Fact]
    public void Compare_SameSchema_NothingToDo()
    {
        var diff = ChildSchemaComparer.Compare(Table([_id, _name]), Table([_id, _name]));

        Assert.Empty(diff.ColumnsToAdd);
        Assert.Empty(diff.ExtraNullableColumns);
        Assert.Empty(diff.Problems);
    }

    [Fact]
    public void Compare_ColumnAddedInParent_IsAddedWithoutProblems()
    {
        var price = new ColumnProfile("Price", "decimal", 9, false);

        var diff = ChildSchemaComparer.Compare(Table([_id, _name, price]), Table([_id, _name]));

        Assert.Equal([price], diff.ColumnsToAdd);
        Assert.Empty(diff.Problems);
    }

    [Fact]
    public void Compare_ColumnAddedEarlierAsNullable_IsNotAProblem()
    {
        // Added columns are created nullable; a NOT NULL parent column fits a nullable child one.
        var parentPrice = new ColumnProfile("Price", "decimal", 9, false);
        var childPrice = parentPrice with { IsNullable = true };

        var diff = ChildSchemaComparer.Compare(Table([_id, parentPrice]), Table([_id, childPrice]));

        Assert.Empty(diff.Problems);
        Assert.Empty(diff.ColumnsToAdd);
    }

    [Fact]
    public void Compare_ColumnLengthChanged_IsAProblem()
    {
        var diff = ChildSchemaComparer.Compare(Table([_id, _name with { MaxLength = 200 }]), Table([_id, _name]));

        Assert.Contains(diff.Problems, p => p.Contains("column Name changed", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_ColumnTypeChanged_IsAProblem()
    {
        var diff = ChildSchemaComparer.Compare(Table([_id, _name with { SqlTypeName = "varchar" }]), Table([_id, _name]));

        Assert.Single(diff.Problems);
    }

    [Fact]
    public void Compare_ParentColumnNowAllowsNull_IsAProblem()
    {
        var notNullName = _name with { IsNullable = false };

        var diff = ChildSchemaComparer.Compare(Table([_id, _name]), Table([_id, notNullName]));

        Assert.Contains(diff.Problems, p => p.Contains("now allows NULL", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_NullableColumnDroppedInParent_IsLeftInPlace()
    {
        var diff = ChildSchemaComparer.Compare(Table([_id]), Table([_id, _name]));

        Assert.Equal(["Name"], diff.ExtraNullableColumns);
        Assert.Empty(diff.Problems);
    }

    [Fact]
    public void Compare_NotNullColumnDroppedInParent_IsAProblem()
    {
        // Inserts would fail: the procedure has no value for the child's NOT NULL column.
        var diff = ChildSchemaComparer.Compare(Table([_id]), Table([_id, _name with { IsNullable = false }]));

        Assert.Contains(diff.Problems, p => p.Contains("no longer exists in the parent", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_PrimaryKeyChanged_IsAProblem()
    {
        var diff = ChildSchemaComparer.Compare(Table([_id, _name], ["Id", "Name"]), Table([_id, _name], ["Id"]));

        Assert.Contains(diff.Problems, p => p.Contains("primary key changed", StringComparison.Ordinal));
    }

    [Fact]
    public void TableTypeMatches_SameColumnsInOrder_True()
    {
        Assert.True(ChildSchemaComparer.TableTypeMatches(Table([_id, _name]), [_id, _name]));
    }

    [Fact]
    public void TableTypeMatches_MissingColumn_False()
    {
        var price = new ColumnProfile("Price", "decimal", 9, false);

        Assert.False(ChildSchemaComparer.TableTypeMatches(Table([_id, _name, price]), [_id, _name]));
    }

    [Fact]
    public void TableTypeMatches_DifferentOrder_False()
    {
        // SqlClient maps table-valued parameter columns by position, not by name.
        Assert.False(ChildSchemaComparer.TableTypeMatches(Table([_id, _name]), [_name, _id]));
    }

    [Fact]
    public void TableTypeMatches_IgnoresTheRowVersionColumn()
    {
        // The rowversion column is never sent, so it isn't part of the table type.
        var rowVersion = new ColumnProfile("RowVer", "timestamp", 8, false);

        Assert.True(ChildSchemaComparer.TableTypeMatches(Table([_id, _name, rowVersion]), [_id, _name]));
    }
}
