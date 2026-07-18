namespace SmartTableSync.Syncers;

/// <summary>Maps sys.types column type names (as read by SchemaReader) to SqlDbType and
/// parses key-component strings back into the right CLR type. Pure, no I/O — this is what
/// lets keyset pagination and parameter binding work generically across any table's
/// primary key shape, instead of one hand-written parser per table.</summary>
public static class SqlTypeMapping
{
    public static SqlDbType ToSqlDbType(string sqlTypeName) => sqlTypeName.ToLowerInvariant() switch
    {
        "int" => SqlDbType.Int,
        "bigint" => SqlDbType.BigInt,
        "smallint" => SqlDbType.SmallInt,
        "tinyint" => SqlDbType.TinyInt,
        "nvarchar" => SqlDbType.NVarChar,
        "varchar" => SqlDbType.VarChar,
        "nchar" => SqlDbType.NChar,
        "char" => SqlDbType.Char,
        "datetime" => SqlDbType.DateTime,
        "datetime2" => SqlDbType.DateTime2,
        "date" => SqlDbType.Date,
        "bit" => SqlDbType.Bit,
        "real" => SqlDbType.Real,
        "float" => SqlDbType.Float,
        "varbinary" => SqlDbType.VarBinary,
        "binary" => SqlDbType.Binary,
        "uniqueidentifier" => SqlDbType.UniqueIdentifier,
        "decimal" or "numeric" => SqlDbType.Decimal,
        "money" => SqlDbType.Money,
        "smallmoney" => SqlDbType.SmallMoney,
        _ => throw new NotSupportedException($"Unmapped SQL type: {sqlTypeName}"),
    };

    /// <summary>The "before any real value" sentinel for a keyset pagination column when
    /// there's no resume key yet — 0 for numeric columns (matches IDENTITY columns, which
    /// start at 1), empty string for string columns.</summary>
    public static string DefaultKeyComponent(string sqlTypeName) =>
        IsStringType(sqlTypeName) ? string.Empty : "0";

    public static bool IsStringType(string sqlTypeName) =>
        sqlTypeName.ToLowerInvariant() is "nvarchar" or "varchar" or "nchar" or "char" or "text" or "ntext";

    /// <summary>SqlParameter.Size for a column, or 0 (no explicit size) for fixed-size
    /// types where AddParam's size argument isn't meaningful. sys.columns.max_length is in
    /// bytes; nvarchar/nchar store 2 bytes per character, so it must be halved to get the
    /// character-count SqlParameter expects (varchar/char/binary are already 1 byte/unit).
    /// -1 (MAX types) passes through unchanged.</summary>
    public static int GetParameterSize(ColumnProfile column)
    {
        if (column.MaxLength == -1) return -1;

        return column.SqlTypeName.ToLowerInvariant() switch
        {
            "nvarchar" or "nchar" => column.MaxLength / 2,
            "varchar" or "char" or "varbinary" or "binary" => column.MaxLength,
            _ => 0,
        };
    }

    /// <summary>Parses one keyset-cursor component (always stored/transmitted as a string,
    /// e.g. in SyncBatchLog.LastKeyValue) back into the CLR type its column needs for a
    /// correctly-typed SqlParameter.</summary>
    public static object ParseKeyComponent(string value, string sqlTypeName) => sqlTypeName.ToLowerInvariant() switch
    {
        "int" => int.Parse(value),
        "bigint" => long.Parse(value),
        "smallint" => short.Parse(value),
        "tinyint" => byte.Parse(value),
        "nvarchar" or "varchar" or "nchar" or "char" or "text" or "ntext" => value,
        "uniqueidentifier" => Guid.Parse(value),
        "datetime" or "datetime2" or "date" => DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException($"Unsupported key column type for keyset pagination: {sqlTypeName}"),
    };
}
