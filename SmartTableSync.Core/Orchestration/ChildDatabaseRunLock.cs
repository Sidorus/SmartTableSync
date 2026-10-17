namespace SmartTableSync.Orchestration;

/// <summary>ISyncRunLock backed by a SQL Server application lock (sp_getapplock) in the child
/// database, the one resource every host instance syncing into it shares. The lock is owned by
/// the session of a dedicated connection held for the whole run, so SQL Server releases it by
/// itself if the host crashes or loses the connection — a stale lock can't block future runs.
/// Needs no extra permission: sp_getapplock is available to every database user.</summary>
public sealed class ChildDatabaseRunLock : ISyncRunLock
{
    public const string LockResource = "SmartTableSync.Run";

    private readonly IDbConnectionFactory _connFactory;

    public ChildDatabaseRunLock(IDbConnectionFactory connFactory) => _connFactory = connFactory;

    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
    {
        var connection = _connFactory.CreateChildConnection();
        try
        {
            await connection.OpenAsync(ct);

            await using var cmd = new SqlCommand("sp_getapplock", connection) { CommandType = CommandType.StoredProcedure };
            cmd.AddParam("@Resource", SqlDbType.NVarChar, LockResource, 255);
            cmd.AddParam("@LockMode", SqlDbType.NVarChar, "Exclusive", 32);
            cmd.AddParam("@LockOwner", SqlDbType.NVarChar, "Session", 32);
            cmd.AddParam("@LockTimeout", SqlDbType.Int, 0);
            var result = cmd.Parameters.Add("@Result", SqlDbType.Int);
            result.Direction = ParameterDirection.ReturnValue;

            await cmd.ExecuteNonQueryAsync(ct);

            // 0 = granted, 1 = granted after waiting; negative = timeout, deadlock or error.
            if ((int)result.Value >= 0)
            {
                return new Handle(connection);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return null;
    }

    private sealed class Handle : IAsyncDisposable
    {
        private readonly SqlConnection _connection;

        public Handle(SqlConnection connection) => _connection = connection;

        // Released explicitly: with connection pooling, disposing only returns the session to
        // the pool, and a session-owned lock would stay held until that pooled connection is
        // reused and reset. If the release itself fails (connection already broken), the
        // session is gone anyway and SQL Server has released the lock with it.
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_connection.State == ConnectionState.Open)
                {
                    await using var cmd = new SqlCommand("sp_releaseapplock", _connection) { CommandType = CommandType.StoredProcedure };
                    cmd.AddParam("@Resource", SqlDbType.NVarChar, LockResource, 255);
                    cmd.AddParam("@LockOwner", SqlDbType.NVarChar, "Session", 32);
                    await cmd.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
            catch (SqlException)
            {
                // See comment above: a failed release means the session, and its lock, is gone.
            }
            finally
            {
                await _connection.DisposeAsync();
            }
        }
    }
}
