namespace SmartTableSync.Infrastructure;

public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private readonly string _parentConnStr;
    private readonly string _childConnStr;

    public DbConnectionFactory(IConfiguration config)
    {
        _parentConnStr = config["ParentDbConnectionString"]
            ?? throw new InvalidOperationException("ParentDbConnectionString is not configured.");
        _childConnStr = config["ChildDbConnectionString"]
            ?? throw new InvalidOperationException("ChildDbConnectionString is not configured.");
    }

    public SqlConnection CreateParentConnection() => new(_parentConnStr);
    public SqlConnection CreateChildConnection()  => new(_childConnStr);
}
