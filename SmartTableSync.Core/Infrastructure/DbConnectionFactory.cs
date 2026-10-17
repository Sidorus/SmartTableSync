namespace SmartTableSync.Infrastructure;

/// <summary>Creates parent/child connections from configuration. Every connection gets the same
/// open-retry provider (SyncSettings.ConnectionOpenAttempts), so transient Azure SQL failures
/// while connecting — failover, throttling, brief unavailability — are retried with exponential
/// backoff instead of failing the table for a whole cycle. Commands are deliberately not
/// retried here; see SyncSettings.ConnectionOpenAttempts.</summary>
public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private readonly string _parentConnStr;
    private readonly string _childConnStr;
    private readonly string? _childProvisioningConnStr;
    private readonly SqlRetryLogicBaseProvider _openRetryProvider;

    public DbConnectionFactory(IConfiguration config, IOptions<SyncSettings> settings, ILogger<DbConnectionFactory> logger)
    {
        _parentConnStr = config["ParentDbConnectionString"]
            ?? throw new InvalidOperationException("ParentDbConnectionString is not configured.");
        _childConnStr = config["ChildDbConnectionString"]
            ?? throw new InvalidOperationException("ChildDbConnectionString is not configured.");

        // Optional - only required when SyncSettings.Mode is Auto. Not validated here so
        // Manual-mode deployments never need to configure it.
        _childProvisioningConnStr = config["ChildProvisioningConnectionString"];

        _openRetryProvider = CreateOpenRetryProvider(settings.Value.ConnectionOpenAttempts, logger);
    }

    public SqlConnection CreateParentConnection() => Create(_parentConnStr);

    public SqlConnection CreateChildConnection() => Create(_childConnStr);

    public SqlConnection CreateChildProvisioningConnection() => Create(
        _childProvisioningConnStr
            ?? throw new InvalidOperationException(
                "ChildProvisioningConnectionString is not configured - required when SmartTableSync:Mode is Auto."));

    private SqlConnection Create(string connectionString) =>
        new(connectionString) { RetryLogicProvider = _openRetryProvider };

    // TransientErrors is left null on purpose: SqlClient then uses its own maintained list of
    // transient connection errors, which tracks Azure SQL's error numbers better than a
    // hand-copied list would.
    private static SqlRetryLogicBaseProvider CreateOpenRetryProvider(int attempts, ILogger logger)
    {
        if (attempts <= 1)
        {
            return SqlConfigurableRetryFactory.CreateNoneRetryProvider();
        }

        var provider = SqlConfigurableRetryFactory.CreateExponentialRetryProvider(new SqlRetryLogicOption
        {
            NumberOfTries = attempts,
            DeltaTime = TimeSpan.FromSeconds(1),
            MaxTimeInterval = TimeSpan.FromSeconds(20),
        });

        provider.Retrying += (_, e) =>
        {
            var lastError = e.Exceptions[^1];
            logger.LogWarning(
                "Opening a database connection failed with a transient error ({Error}) — retry {Attempt} after {Delay}",
                lastError is SqlException sqlException ? sqlException.Number : lastError.GetType().Name,
                e.RetryCount + 1, e.Delay);
        };

        return provider;
    }
}
