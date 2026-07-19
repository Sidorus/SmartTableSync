namespace SmartTableSync.IntegrationTests.Fixtures;

/// <summary>Shares one MsSqlTestFixture (one container) across every test class tagged
/// [Collection("SqlServer")] - SQL Server startup cost is paid once per test run, not
/// once per class.</summary>
[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<MsSqlTestFixture>;
