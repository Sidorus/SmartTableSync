using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SmartTableSync.DependencyInjection;

public static class SmartTableSyncServiceCollectionExtensions
{
    /// <summary>Registers the entire sync engine — settings binding, schema introspection,
    /// plan derivation, table provisioning, scaffolding, and the orchestrator itself — behind
    /// their interfaces, so any host (Azure Function, console app, whatever) gets a working
    /// engine from this one call instead of needing to know its internal wiring.
    ///
    /// Uses TryAdd throughout, so a host can override any single piece (e.g. a different
    /// ISyncMetricsReporter) by registering its own implementation either before or after
    /// this call. The default, SyncMetricsReporter, emits via System.Diagnostics.Metrics —
    /// no APM vendor package required; point any OpenTelemetry exporter at the
    /// "SmartTableSync" meter name to collect it.</summary>
    public static IServiceCollection AddSmartTableSync(this IServiceCollection services)
    {
        services.AddSmartTableSyncSettings();

        // Fail at host startup, not on the first timer run minutes later, when the engine has
        // nothing to sync. Lives here rather than in AddSmartTableSyncSettings so tools that
        // only bind settings (tests, Scaffold) don't need a table list.
        services.AddOptions<SyncSettings>()
            .Validate(settings => settings.Tables.Count > 0, "SmartTableSync:TableList must list at least one table name.")
            .Validate(
                settings => settings.Tables.Distinct(StringComparer.OrdinalIgnoreCase).Count() == settings.Tables.Count,
                "SmartTableSync:TableList lists the same table more than once.")
            .ValidateOnStart();

        services.TryAddSingleton<ISyncMetricsReporter, SyncMetricsReporter>();
        services.TryAddSingleton<IDbConnectionFactory, DbConnectionFactory>();
        services.TryAddSingleton<ISyncRunLock, ChildDatabaseRunLock>();
        services.TryAddSingleton<ISyncStateRepository, SyncStateRepository>();
        services.TryAddSingleton<IBatchLogger, BatchLogger>();
        services.TryAddSingleton<ISchemaReader, SchemaReader>();
        services.TryAddSingleton<ISyncPlanHeuristic, SyncPlanHeuristic>();
        services.TryAddSingleton<ITableSyncerFactory, TableSyncerFactory>();
        services.TryAddSingleton<ISyncTableConfigRepository, SyncTableConfigRepository>();
        services.TryAddSingleton<ISyncTableProvisioner, SyncTableProvisioner>();
        services.TryAddSingleton<ISqlArtifactGenerator, SqlArtifactGenerator>();
        services.TryAddSingleton<IChildDdlProvisioner, ChildDdlProvisioner>();

        services.TryAddSingleton<ISyncOrchestrator, SyncOrchestrator>();

        return services;
    }
}
