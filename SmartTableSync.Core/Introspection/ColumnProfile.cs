namespace SmartTableSync.Introspection;

/// <summary>One column's shape, as read from sys.columns/sys.types. MaxLength follows
/// SQL Server's own convention: -1 means MAX (nvarchar(max), varbinary(max)).</summary>
public sealed record ColumnProfile(string Name, string SqlTypeName, int MaxLength, bool IsNullable)
{
    public bool IsLob => MaxLength == -1;
}
