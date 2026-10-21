using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartTableSync.Abstractions;
using SmartTableSync.Hosting;
using Xunit;

namespace SmartTableSync.Tests.DependencyInjection;

public class ConnectionApplicationNameTests
{
    private const string Parent = "Server=tcp:parent.database.windows.net,1433;Database=ParentDb;Authentication=Active Directory Default;Encrypt=True";
    private const string Child = "Server=tcp:child.database.windows.net,1433;Database=ChildDb;User Id=sync;Password=\"p;w=d\";Encrypt=True";

    private static IDbConnectionFactory BuildFactory(Dictionary<string, string?> configValues)
    {
        configValues["SmartTableSync:TableList"] = "Product";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSmartTableSync();

        return services.BuildServiceProvider().GetRequiredService<IDbConnectionFactory>();
    }

    private static SqlConnectionStringBuilder Parse(SqlConnection connection) => new(connection.ConnectionString);

    [Fact]
    public void Connections_WithoutApplicationName_ReportSmartTableSync()
    {
        var factory = BuildFactory(new()
        {
            ["ParentDbConnectionString"] = Parent,
            ["ChildDbConnectionString"] = Child,
            ["ChildProvisioningConnectionString"] = Child,
        });

        using var parent = factory.CreateParentConnection();
        using var child = factory.CreateChildConnection();
        using var provisioning = factory.CreateChildProvisioningConnection();

        Assert.Equal("SmartTableSync", Parse(parent).ApplicationName);
        Assert.Equal("SmartTableSync", Parse(child).ApplicationName);
        Assert.Equal("SmartTableSync", Parse(provisioning).ApplicationName);
    }

    [Fact]
    public void Connections_WithoutApplicationName_KeepEverythingElse()
    {
        var factory = BuildFactory(new()
        {
            ["ParentDbConnectionString"] = Parent,
            ["ChildDbConnectionString"] = Child,
        });

        using var parent = factory.CreateParentConnection();
        using var child = factory.CreateChildConnection();
        var parentSettings = Parse(parent);
        var childSettings = Parse(child);

        Assert.Equal("tcp:parent.database.windows.net,1433", parentSettings.DataSource);
        Assert.Equal("ParentDb", parentSettings.InitialCatalog);
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryDefault, parentSettings.Authentication);
        Assert.Equal("sync", childSettings.UserID);
        Assert.Equal("p;w=d", childSettings.Password);
    }

    [Theory]
    [InlineData("Application Name=OrdersSync")]
    [InlineData("App=OrdersSync")]
    public void Connections_WithTheirOwnApplicationName_KeepIt(string applicationName)
    {
        var factory = BuildFactory(new()
        {
            ["ParentDbConnectionString"] = $"{Parent};{applicationName}",
            ["ChildDbConnectionString"] = $"{Child};{applicationName}",
        });

        using var parent = factory.CreateParentConnection();
        using var child = factory.CreateChildConnection();

        Assert.Equal("OrdersSync", Parse(parent).ApplicationName);
        Assert.Equal("OrdersSync", Parse(child).ApplicationName);
    }

    [Fact]
    public void InvalidConnectionString_FailsNamingTheSetting()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildFactory(new()
        {
            ["ParentDbConnectionString"] = Parent,
            ["ChildDbConnectionString"] = "Server=x;NotAKeyword=1",
        }));

        Assert.Contains("ChildDbConnectionString", ex.Message);
    }
}
