namespace SmartTableSync.Infrastructure;

public interface IDbConnectionFactory
{
    SqlConnection CreateParentConnection();
    SqlConnection CreateChildConnection();
}
