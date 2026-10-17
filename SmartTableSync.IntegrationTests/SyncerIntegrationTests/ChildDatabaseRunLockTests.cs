using SmartTableSync.IntegrationTests.Fixtures;
using SmartTableSync.IntegrationTests.Support;
using SmartTableSync.Orchestration;

namespace SmartTableSync.IntegrationTests.SyncerIntegrationTests;

/// <summary>
/// The database run lock against a real SQL Server: a second instance can't take it while the
/// first holds it, and it's free again as soon as the holder releases it — even though the
/// holder's connection goes back to the connection pool rather than being closed.
/// Two ChildDatabaseRunLock instances stand in for two separate host processes.
/// </summary>
[Collection("SqlServer")]
public class ChildDatabaseRunLockTests
{
    private readonly MsSqlTestFixture _fixture;

    public ChildDatabaseRunLockTests(MsSqlTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SecondInstance_CannotAcquireWhileFirstHoldsTheLock_AndCanAfterRelease()
    {
        var firstHost = new ChildDatabaseRunLock(SyncerTestHelpers.CreateConnectionFactory(_fixture.Configuration));
        var secondHost = new ChildDatabaseRunLock(SyncerTestHelpers.CreateConnectionFactory(_fixture.Configuration));

        var firstLock = await firstHost.TryAcquireAsync(CancellationToken.None);
        Assert.NotNull(firstLock);

        await using (var blocked = await secondHost.TryAcquireAsync(CancellationToken.None))
        {
            Assert.Null(blocked);
        }

        await firstLock.DisposeAsync();

        await using var afterRelease = await secondHost.TryAcquireAsync(CancellationToken.None);
        Assert.NotNull(afterRelease);
    }
}
