using System.Text.RegularExpressions;

namespace SmartTableSync.Sql;

/// <summary>
/// Safe ways to put a table or column name into SQL text, for the places a name can't be a
/// parameter. Names come from configuration or from the parent database's catalog — the parent
/// schema belongs to another team, so a name is treated as untrusted text, never pasted raw.
/// </summary>
internal static partial class SqlIdentifier
{
    /// <summary>[name], with any ']' doubled.</summary>
    public static string Quote(string identifier) => $"[{EnsureSingleLine(identifier).Replace("]", "]]")}]";

    /// <summary>[schema].[name] for a synced table ("Order" is [dbo].[Order]; see SyncTableName).</summary>
    public static string Table(string tableName) =>
        $"{Quote(SyncTableName.Schema(tableName))}.{Quote(SyncTableName.Object(tableName))}";

    /// <summary>[schema].[objectName] for an object that belongs to a synced table — its
    /// procedures and table type live in the table's schema.</summary>
    public static string InTableSchema(string tableName, string objectName) =>
        $"{Quote(SyncTableName.Schema(tableName))}.{Quote(objectName)}";

    /// <summary>N'text', with any quote doubled — for names inside string literals
    /// (OBJECT_ID('...'), sys.* lookups, PRINT messages).</summary>
    public static string Literal(string text) => $"N'{EnsureSingleLine(text).Replace("'", "''")}'";

    /// <summary>The name with line breaks and comment terminators removed, for generated SQL
    /// comments, where a crafted name could otherwise end the comment and inject SQL.</summary>
    public static string CommentText(string text) =>
        text.Replace("\r", " ").Replace("\n", " ").Replace("*/", "* /");

    /// <summary>"@name" for a column's stored-procedure parameter. Parameter names can't be
    /// bracket-quoted, so a column name that isn't a plain identifier is rejected — with a clear
    /// error at provisioning time — instead of producing broken or injectable SQL.</summary>
    public static string Parameter(string columnName) =>
        PlainIdentifier().IsMatch(columnName)
            ? "@" + columnName
            : throw new InvalidOperationException(
                $"Column name '{columnName}' can't be used as a stored-procedure parameter name (letters, digits " +
                "and _ @ # $ only, not starting with a digit or @). Rename the column, or force the Tvp merge " +
                "strategy for the table via the MergeStrategyOverride column in dbo.SyncTableConfig.");

    // Generated scripts are split into batches on lines containing only "GO", as text, before
    // SQL Server parses anything — so a line break inside a name could start a new batch even
    // inside brackets or quotes. No real table or column name needs a control character.
    private static string EnsureSingleLine(string name) =>
        name.Any(char.IsControl)
            ? throw new InvalidOperationException(
                $"Name '{CommentText(name)}' contains a line break or other control character, which SmartTableSync " +
                "doesn't support in table or column names.")
            : name;

    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{Nd}_@#$]{0,126}$")]
    private static partial Regex PlainIdentifier();
}
