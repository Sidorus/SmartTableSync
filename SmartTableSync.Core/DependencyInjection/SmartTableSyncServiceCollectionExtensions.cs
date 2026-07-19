using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartTableSync.Configuration;
using SmartTableSync.Infrastructure;
using SmartTableSync.Introspection;
using SmartTableSync.Logging;
using SmartTableSync.Observability;
using SmartTableSync.Options;
using SmartTableSync.Orchestration;
using SmartTableSync.Planning;
using SmartTableSync.Scaffolding;
using SmartTableSync.State;
using SmartTableSync.Syncers;

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

        services.TryAddSingleton<ISyncMetricsReporter, SyncMetricsReporter>();
        services.TryAddSingleton<IDbConnectionFactory, DbConnectionFactory>();
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
