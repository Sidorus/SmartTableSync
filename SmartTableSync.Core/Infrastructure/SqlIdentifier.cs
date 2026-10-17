namespace SmartTableSync.Infrastructure;

/// <summary>Bracket-quotes a SQL Server identifier (table or column name) for SQL text that
/// can't take it as a parameter, escaping any closing bracket inside the name. Identifiers here
/// come from configuration or the system catalog, never end-user input, but every SQL builder
/// quotes them the same way rather than each keeping its own copy.</summary>
public static class SqlIdentifier
{
    public static string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";
}
