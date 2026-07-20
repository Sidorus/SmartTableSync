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

                // Missing or unparseable -> Manual, today's only behavior - so existing
                // deployments that predate this setting keep working unchanged.
                options.Mode = Enum.TryParse<SyncMode>(configuration["SmartTableSync:Mode"], ignoreCase: true, out var mode)
                    ? mode
                    : SyncMode.Manual;

                options.ChangeTrackingColumns = configuration.GetSection("SmartTableSync:ChangeTrackingColumns")
                    .GetChildren()
                    .Where(c => !string.IsNullOrWhiteSpace(c.Value))
                    .ToDictionary(c => c.Key, c => c.Value!, StringComparer.OrdinalIgnoreCase);

                var rawAllowFullRescanFor = configuration["SmartTableSync:AllowFullRescanFor"] ?? string.Empty;
                options.AllowFullRescanFor = rawAllowFullRescanFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            });

        return services;
    }
}
