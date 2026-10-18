using Microsoft.Data.SqlClient;
using SmartTableSync.IntegrationTests.Fixtures;

namespace SmartTableSync.IntegrationTests.Support;

/// <summary>
/// The three identities sql/07_permissions.sql grants to, as SQL logins with users in both the
/// parent and the child database and no rights of their own: whatever they can do afterwards
/// comes from the grants a test applies.
/// </summary>
internal sealed class LeastPrivilegeIdentities
{
    private static readonly string[] _names =
    [
        "smarttablesync-child-provisioning",
        "smarttablesync-parent-introspection",
        "smarttablesync-runtime",
    ];

    private readonly MsSqlTestFixture _fixture;
    private readonly string _password;

    private LeastPrivilegeIdentities(MsSqlTestFixture fixture, string password)
    {
        _fixture = fixture;
        _password = password;
    }

    public static async Task<LeastPrivilegeIdentities> CreateAsync(MsSqlTestFixture fixture)
    {
        // Only lives as long as this test's throwaway container
        var identities = new LeastPrivilegeIdentities(fixture, $"Lp-{Guid.NewGuid():N}-Aa1!");

        foreach (var name in _names)
        {
            await ExecuteAsync(fixture.ParentConnectionString, $"CREATE LOGIN [{name}] WITH PASSWORD = N'{identities._password}';");
            await ExecuteAsync(fixture.ParentConnectionString, $"CREATE USER [{name}] FOR LOGIN [{name}];");
            await ExecuteAsync(fixture.ChildConnectionString, $"CREATE USER [{name}] FOR LOGIN [{name}];");
        }

        return identities;
    }

    /// <summary>A connection string for one of the identities, by the PermissionsScript identity
    /// key (runtime, parent-introspection, child-provisioning).</summary>
    public string ParentConnectionString(string identity) => ConnectionString(_fixture.ParentConnectionString, identity);

    public string ChildConnectionString(string identity) => ConnectionString(_fixture.ChildConnectionString, identity);

    private string ConnectionString(string adminConnectionString, string identity) =>
        new SqlConnectionStringBuilder(adminConnectionString)
        {
            UserID = $"smarttablesync-{identity}",
            Password = _password,
        }.ConnectionString;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
