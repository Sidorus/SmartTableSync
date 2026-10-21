namespace SmartTableSync.Sql;

/// <summary>The application name SmartTableSync's connections report to SQL Server, so a DBA sees
/// them in sys.dm_exec_sessions.program_name, APP_NAME(), Extended Events and audit logs instead
/// of SqlClient's generic "Core Microsoft SqlClient Data Provider". An Application Name (or App)
/// already in the connection string always wins.</summary>
internal static class SqlApplicationName
{
    public const string Engine = "SmartTableSync";
    public const string Scaffold = "SmartTableSync.Scaffold";

    /// <summary>Returns the connection string with Application Name set to
    /// <paramref name="applicationName"/>, or unchanged when it already sets one. Called once per
    /// connection string, not per connection.</summary>
    /// <param name="settingName">The setting the connection string came from, for the error message.</param>
    public static string WithDefault(string connectionString, string applicationName, string settingName)
    {
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"{settingName} isn't a valid connection string: {ex.Message}", ex);
        }

        // ShouldSerialize is true only when the string itself sets the keyword (or its synonym
        // App); an empty value counts as not set
        if (builder.ShouldSerialize("Application Name"))
        {
            return connectionString;
        }

        builder.ApplicationName = applicationName;
        return builder.ConnectionString;
    }
}
