namespace SmartTableSync.Sql;

/// <summary>
/// A synced table's name as it appears in SmartTableSync:TableList, the per-table settings and
/// the tracking tables: "Table" for a table in dbo, "schema.Table" for any other schema. The
/// dbo form stays unqualified so tracking rows written before schemas were supported keep
/// matching. The child keeps the parent's schema; a table's procedures and table type are
/// created in that same schema, so tables with the same name in two schemas never collide.
/// </summary>
internal static class SyncTableName
{
    public const string DefaultSchema = "dbo";

    /// <summary>The TableName columns in CustomSyncState, SyncBatchLog and SyncTableConfig are
    /// NVARCHAR(100).</summary>
    public const int MaxLength = 100;

    /// <summary>"dbo.Order" and "Order" both become "Order"; "sales.Order" stays as it is.
    /// Throws for an empty part, more than one dot, or a name too long for the tracking tables.</summary>
    public static string Normalize(string configuredName)
    {
        var name = configuredName.Trim();
        var parts = name.Split('.');

        if (parts.Length > 2 || parts.Any(p => p.Trim().Length == 0))
        {
            throw new InvalidOperationException(
                $"Table name '{SqlIdentifier.CommentText(configuredName)}' must be 'Table' or 'schema.Table' " +
                "(names that contain a dot aren't supported).");
        }

        var normalized = parts.Length == 1 || string.Equals(parts[0].Trim(), DefaultSchema, StringComparison.OrdinalIgnoreCase)
            ? parts[^1].Trim()
            : $"{parts[0].Trim()}.{parts[1].Trim()}";

        if (normalized.Length > MaxLength)
        {
            throw new InvalidOperationException(
                $"Table name '{SqlIdentifier.CommentText(normalized)}' is longer than {MaxLength} characters, " +
                "the size of the TableName columns in the tracking tables.");
        }

        return normalized;
    }

    public static string Schema(string tableName)
    {
        var dot = tableName.IndexOf('.');
        return dot < 0 ? DefaultSchema : tableName[..dot];
    }

    /// <summary>The table's own name, without the schema: the base for the names of its
    /// procedures, table type and constraints.</summary>
    public static string Object(string tableName)
    {
        var dot = tableName.IndexOf('.');
        return dot < 0 ? tableName : tableName[(dot + 1)..];
    }

    /// <summary>"OrderTableType" for both "Order" and "sales.Order": the type is created in the
    /// table's own schema, so the name itself carries no schema.</summary>
    public static string TableTypeName(string tableName) => $"{Object(tableName)}TableType";
}
