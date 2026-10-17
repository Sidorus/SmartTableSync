namespace SmartTableSync.Syncers.Sql;

/// <summary>Maps sys.types column type names (as read by SchemaReader) to SqlDbType and
/// parses key-component strings back into the right CLR type. Pure, no I/O — this is what
/// lets keyset pagination and parameter binding work for any table's primary key shape.</summary>
internal static class SqlTypeMapping
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
        "smalldatetime" => SqlDbType.SmallDateTime,
        "datetimeoffset" => SqlDbType.DateTimeOffset,
        "time" => SqlDbType.Time,
        "date" => SqlDbType.Date,
        "text" => SqlDbType.Text,
        "ntext" => SqlDbType.NText,
        "timestamp" or "rowversion" => SqlDbType.Timestamp,
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

    public static bool IsStringType(string sqlTypeName) =>
        sqlTypeName.ToLowerInvariant() is "nvarchar" or "varchar" or "nchar" or "char" or "text" or "ntext";

    /// <summary>The types the Timestamp strategy's "[Col] &gt;= DATEADD(minute, -1, @LastSync)"
    /// filter works against (@LastSync is bound as SqlDbType.DateTime). Excludes
    /// datetimeoffset/time, which don't compare correctly against a plain DateTime parameter.</summary>
    public static bool IsDateTimeFamily(string sqlTypeName) =>
        sqlTypeName.ToLowerInvariant() is "datetime" or "datetime2" or "smalldatetime" or "date";

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
        "datetime" or "datetime2" or "smalldatetime" or "date" => DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
        "datetimeoffset" => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
        "time" => TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException($"Unsupported key column type for keyset pagination: {sqlTypeName}"),
    };
}
