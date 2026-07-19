namespace SmartTableSync.Infrastructure;

public interface IDbConnectionFactory
{
    SqlConnection CreateParentConnection();
    SqlConnection CreateChildConnection();

    /// <summary>DDL-capable connection, used only when SyncSettings.Mode is Auto, only for the
    /// brief "create the child table/TVP/procedure if missing" moment - never for regular
    /// sync. Optional: only throws if actually called without ChildProvisioningConnectionString
    /// configured, so Manual-mode deployments never need to set it.</summary>
    SqlConnection CreateChildProvisioningConnection();
}
