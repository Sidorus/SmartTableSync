using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartTableSync.Configuration;
using SmartTableSync.Functions.Observability;
using SmartTableSync.Infrastructure;
using SmartTableSync.Introspection;
using SmartTableSync.Logging;
using SmartTableSync.Observability;
using SmartTableSync.Options;
using SmartTableSync.Orchestration;
using SmartTableSync.Planning;
using SmartTableSync.State;
using SmartTableSync.Syncers;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        services.AddSmartTableSyncSettings();

        services.AddSingleton<ISyncMetricsReporter, AppInsightsMetricsReporter>();
        services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
        services.AddSingleton<ISyncStateRepository, SyncStateRepository>();
        services.AddSingleton<IBatchLogger, BatchLogger>();
        services.AddSingleton<ISchemaReader, SchemaReader>();
        services.AddSingleton<ISyncPlanHeuristic, SyncPlanHeuristic>();
        services.AddSingleton<ITableSyncerFactory, TableSyncerFactory>();
        services.AddSingleton<ISyncTableConfigRepository, SyncTableConfigRepository>();
        services.AddSingleton<ISyncTableProvisioner, SyncTableProvisioner>();

        // Registered in execution order — orchestrator iterates in registration order.
        services.AddSingleton<ITableSyncer, ProductAttributeValueSyncer>();
        services.AddSingleton<ITableSyncer, ProductCatalogItemSyncer>();
        services.AddSingleton<ITableSyncer, DocumentAssetSyncer>();

        services.AddSingleton<ISyncOrchestrator, SyncOrchestrator>();
    })
    .Build();

await host.RunAsync();
