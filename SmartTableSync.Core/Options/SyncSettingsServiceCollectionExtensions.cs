namespace SmartTableSync.Options;

public static class SyncSettingsServiceCollectionExtensions
{
    /// <summary>Binds SyncSettings.Tables from the "SmartTableSync:TableList" configuration
    /// value (env var / Azure App Setting) — a single comma-separated string, e.g.
    /// "ProductAttributeValue,ProductCatalogItem,DocumentAsset". Requires IConfiguration
    /// to already be registered (true by default in any generic host).</summary>
    public static IServiceCollection AddSmartTableSyncSettings(this IServiceCollection services)
    {
        services.AddOptions<SyncSettings>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                var raw = configuration["SmartTableSync:TableList"] ?? string.Empty;
                options.Tables = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            });

        return services;
    }
}
