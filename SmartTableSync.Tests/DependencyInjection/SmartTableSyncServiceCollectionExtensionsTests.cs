using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartTableSync.DependencyInjection;
using SmartTableSync.Options;
using SmartTableSync.Orchestration;
using Xunit;

namespace SmartTableSync.Tests.DependencyInjection;

public class SmartTableSyncServiceCollectionExtensionsTests
{
    private static IServiceProvider BuildProvider(Dictionary<string, string?> configValues)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSmartTableSync();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddSmartTableSync_WithTableList_SettingsAreValid()
    {
        var provider = BuildProvider(new Dictionary<string, string?> { ["SmartTableSync:TableList"] = "Orders,Customers" });

        var settings = provider.GetRequiredService<IOptions<SyncSettings>>().Value;

        Assert.Equal(["Orders", "Customers"], settings.Tables);
    }

    [Fact]
    public void AddSmartTableSync_WithoutTableList_FailsValidation()
    {
        var provider = BuildProvider([]);

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SyncSettings>>().Value);

        Assert.Contains("SmartTableSync:TableList", ex.Message);
    }

    [Fact]
    public void AddSmartTableSync_WithDuplicateTable_FailsValidation()
    {
        var provider = BuildProvider(new Dictionary<string, string?> { ["SmartTableSync:TableList"] = "Orders,orders" });

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SyncSettings>>().Value);

        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void AddSmartTableSync_RegistersTheDatabaseRunLock()
    {
        var services = new ServiceCollection();
        services.AddSmartTableSync();

        var registration = Assert.Single(services, s => s.ServiceType == typeof(ISyncRunLock));
        Assert.Equal(typeof(ChildDatabaseRunLock), registration.ImplementationType);
    }
}
