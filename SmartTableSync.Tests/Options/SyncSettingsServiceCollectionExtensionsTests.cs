using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartTableSync.Options;
using SmartTableSync.Planning;
using Xunit;

namespace SmartTableSync.Tests.Options;

public class SyncSettingsServiceCollectionExtensionsTests
{
    private static SyncSettings Bind(Dictionary<string, string?> configValues)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSmartTableSyncSettings();

        return services.BuildServiceProvider().GetRequiredService<IOptions<SyncSettings>>().Value;
    }

    [Fact]
    public void ChangeTrackingColumns_BindsDictionarySection()
    {
        var settings = Bind(new Dictionary<string, string?>
        {
            ["SmartTableSync:ChangeTrackingColumns:SomeOtherTable"] = "ModifiedAt",
            ["SmartTableSync:ChangeTrackingColumns:AnotherTable"] = "UpdatedAt",
        });

        Assert.Equal("ModifiedAt", settings.ChangeTrackingColumns["SomeOtherTable"]);
        Assert.Equal("UpdatedAt", settings.ChangeTrackingColumns["AnotherTable"]);
    }

    [Fact]
    public void ChangeTrackingColumns_Missing_IsEmpty()
    {
        var settings = Bind([]);

        Assert.Empty(settings.ChangeTrackingColumns);
    }

    [Fact]
    public void ChangeTrackingColumns_LookupIsCaseInsensitive()
    {
        var settings = Bind(new Dictionary<string, string?>
        {
            ["SmartTableSync:ChangeTrackingColumns:SomeTable"] = "ModifiedAt",
        });

        Assert.True(settings.ChangeTrackingColumns.TryGetValue("sometable", out var column));
        Assert.Equal("ModifiedAt", column);
    }

    [Fact]
    public void AllowFullRescanFor_BindsCommaSeparatedList()
    {
        var settings = Bind(new Dictionary<string, string?>
        {
            ["SmartTableSync:AllowFullRescanFor"] = "SmallLookupTable, AnotherTable",
        });

        Assert.Equal(["SmallLookupTable", "AnotherTable"], settings.AllowFullRescanFor);
    }

    [Fact]
    public void AllowFullRescanFor_Missing_IsEmpty()
    {
        var settings = Bind([]);

        Assert.Empty(settings.AllowFullRescanFor);
    }

    [Fact]
    public void IncrementalStrategyOverrides_BindsDictionarySection()
    {
        var settings = Bind(new Dictionary<string, string?>
        {
            ["SmartTableSync:IncrementalStrategyOverrides:SomeTable"] = "Timestamp",
            ["SmartTableSync:IncrementalStrategyOverrides:AnotherTable"] = "FullRescan",
        });

        Assert.Equal(IncrementalStrategy.Timestamp, settings.IncrementalStrategyOverrides["SomeTable"]);
        Assert.Equal(IncrementalStrategy.FullRescan, settings.IncrementalStrategyOverrides["AnotherTable"]);
    }

    [Fact]
    public void IncrementalStrategyOverrides_IsCaseInsensitiveForBothKeyAndValue()
    {
        var settings = Bind(new Dictionary<string, string?>
        {
            ["SmartTableSync:IncrementalStrategyOverrides:SomeTable"] = "rowversion",
        });

        Assert.True(settings.IncrementalStrategyOverrides.TryGetValue("sometable", out var strategy));
        Assert.Equal(IncrementalStrategy.RowVersion, strategy);
    }

    [Fact]
    public void IncrementalStrategyOverrides_UnparseableValue_IsDropped()
    {
        var settings = Bind(new Dictionary<string, string?>
        {
            ["SmartTableSync:IncrementalStrategyOverrides:SomeTable"] = "NotARealStrategy",
        });

        Assert.Empty(settings.IncrementalStrategyOverrides);
    }

    [Fact]
    public void IncrementalStrategyOverrides_Missing_IsEmpty()
    {
        var settings = Bind([]);

        Assert.Empty(settings.IncrementalStrategyOverrides);
    }
}
