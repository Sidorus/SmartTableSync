using System.Globalization;

namespace SmartTableSync.Hosting;

public static class SyncSettingsServiceCollectionExtensions
{
    /// <summary>Binds SyncSettings from IConfiguration (env vars / Azure App Settings) for a
    /// DI-hosted app. Requires IConfiguration to already be registered (true by default in
    /// any generic host).</summary>
    public static IServiceCollection AddSmartTableSyncSettings(this IServiceCollection services)
    {
        services.AddOptions<SyncSettings>().Configure<IConfiguration>(Bind);
        return services;
    }

    /// <summary>The actual binding logic, extracted so SmartTableSync.Scaffold's CLI — which
    /// has no DI container of its own — can resolve the identical SyncSettings (including
    /// TimestampColumns/AllowFullRescanFor/IncrementalStrategyOverrides) directly from an
    /// IConfiguration built over environment variables, instead of re-deriving its own partial
    /// approximation of this logic that could silently drift from the runtime's.</summary>
    public static void Bind(SyncSettings options, IConfiguration configuration)
    {
        var raw = configuration["SmartTableSync:TableList"] ?? string.Empty;
        options.Tables = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Missing or unparseable -> Manual, today's only behavior - so existing
        // deployments that predate this setting keep working unchanged.
        options.Mode = Enum.TryParse<SyncMode>(configuration["SmartTableSync:Mode"], ignoreCase: true, out var mode)
            ? mode
            : SyncMode.Manual;

        // "ChangeTrackingColumns" is the original name of this setting, still read so existing
        // deployments keep working; an entry under the new name wins for the same table.
        options.TimestampColumns = configuration.GetSection("SmartTableSync:ChangeTrackingColumns").GetChildren()
            .Concat(configuration.GetSection("SmartTableSync:TimestampColumns").GetChildren())
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value!, StringComparer.OrdinalIgnoreCase);

        var rawAllowFullRescanFor = configuration["SmartTableSync:AllowFullRescanFor"] ?? string.Empty;
        options.AllowFullRescanFor = rawAllowFullRescanFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Invalid enum values are dropped rather than throwing at startup - the same
        // "unparseable falls back to the safe default" philosophy as Mode above,
        // applied per-table instead of globally. SyncPlanHeuristic still validates
        // the surviving overrides are actually satisfiable for the table they target.
        options.IncrementalStrategyOverrides = configuration.GetSection("SmartTableSync:IncrementalStrategyOverrides")
            .GetChildren()
            .Where(c => Enum.TryParse<IncrementalStrategy>(c.Value, ignoreCase: true, out _))
            .ToDictionary(c => c.Key, c => Enum.Parse<IncrementalStrategy>(c.Value!, ignoreCase: true), StringComparer.OrdinalIgnoreCase);

        // Whole hours, so it's easy to enter as one App Setting. Missing, unparseable or
        // negative keeps the 24-hour default; 0 explicitly disables the periodic run.
        options.ChangeTrackingReconcileInterval =
            TryParseNonNegativeInt(configuration["SmartTableSync:ChangeTrackingReconcileIntervalHours"], out var changeTrackingReconcileHours)
                ? TimeSpan.FromHours(changeTrackingReconcileHours)
                : TimeSpan.FromHours(24);

        // No zero here: for these strategies reconciliation is the only way deletes arrive.
        options.ReconcileInterval =
            TryParseNonNegativeInt(configuration["SmartTableSync:ReconcileIntervalHours"], out var reconcileHours) && reconcileHours > 0
                ? TimeSpan.FromHours(reconcileHours)
                : TimeSpan.FromHours(1);

        // SqlClient accepts 1..60 total attempts; anything outside that keeps the default.
        options.ConnectionOpenAttempts =
            TryParseNonNegativeInt(configuration["SmartTableSync:ConnectionOpenAttempts"], out var attempts) && attempts is >= 1 and <= 60
                ? attempts
                : 5;
    }

    private static bool TryParseNonNegativeInt(string? value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
}
