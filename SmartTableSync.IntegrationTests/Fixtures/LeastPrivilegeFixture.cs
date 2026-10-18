using SmartTableSync.IntegrationTests.Support;

namespace SmartTableSync.IntegrationTests.Fixtures;

/// <summary>
/// A dedicated SQL Server container (see MsSqlTestFixture) plus the three SmartTableSync
/// identities as logins with no rights of their own, then the grants from
/// sql/07_permissions.sql that the subclass applies: once per test class, before any test runs.
/// </summary>
public abstract class LeastPrivilegeFixture : IAsyncLifetime
{
    public MsSqlTestFixture Sql { get; } = new();

    internal LeastPrivilegeIdentities Identities { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Sql.InitializeAsync();
        await PrepareDatabasesAsync();
        Identities = await LeastPrivilegeIdentities.CreateAsync(Sql);
        await ApplyGrantsAsync(PermissionsScript.Load());
    }

    public Task DisposeAsync() => Sql.DisposeAsync();

    /// <summary>Changes the databases as an administrator before the identities exist.</summary>
    protected virtual Task PrepareDatabasesAsync() => Task.CompletedTask;

    /// <summary>Runs the parts of 07_permissions.sql this setup documents.</summary>
    internal abstract Task ApplyGrantsAsync(PermissionsScript script);

    protected Task RunOnParentAsync(string sql, string label) =>
        MsSqlTestFixture.RunGeneratedScriptAsync(Sql.ParentConnectionString, sql, label);

    protected Task RunOnChildAsync(string sql, string label) =>
        MsSqlTestFixture.RunGeneratedScriptAsync(Sql.ChildConnectionString, sql, label);
}
