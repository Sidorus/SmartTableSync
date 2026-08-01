namespace SmartTableSync.Infrastructure;

public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private readonly string _parentConnStr;
    private readonly string _childConnStr;
    private readonly string? _childProvisioningConnStr;

    public DbConnectionFactory(IConfiguration config)
    {
        _parentConnStr = config["ParentDbConnectionString"]
            ?? throw new InvalidOperationException("ParentDbConnectionString is not configured.");
        _childConnStr = config["ChildDbConnectionString"]
            ?? throw new InvalidOperationException("ChildDbConnectionString is not configured.");

        // Optional - only required when SyncSettings.Mode is Auto. Not validated here so
        // Manual-mode deployments never need to configure it.
        _childProvisioningConnStr = config["ChildProvisioningConnectionString"];
    }

    public SqlConnection CreateParentConnection() => new(_parentConnStr);
    public SqlConnection CreateChildConnection() => new(_childConnStr);

    public SqlConnection CreateChildProvisioningConnection() => new(
        _childProvisioningConnStr
            ?? throw new InvalidOperationException(
                "ChildProvisioningConnectionString is not configured - required when SmartTableSync:Mode is Auto."));
}
