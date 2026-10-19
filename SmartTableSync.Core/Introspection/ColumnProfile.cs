namespace SmartTableSync.Introspection;

/// <summary>One column's shape, as read from sys.columns/sys.types. MaxLength follows
/// SQL Server's own convention: -1 means MAX (nvarchar(max), varbinary(max)). Precision and
/// Scale are sys.columns' values: what decimal(10,2) or datetime2(3) needs to be recreated
/// exactly in the child; 0 for types that have neither.</summary>
internal sealed record ColumnProfile(
    string Name, string SqlTypeName, int MaxLength, bool IsNullable, byte Precision = 0, byte Scale = 0)
{
    public bool IsLob => MaxLength == -1;

    /// <summary>decimal and numeric: the types whose values are rounded when the child's
    /// precision or scale differs, so the only ones whose precision and scale are compared.</summary>
    public bool IsExactNumeric => SqlTypeName.ToLowerInvariant() is "decimal" or "numeric";
}
