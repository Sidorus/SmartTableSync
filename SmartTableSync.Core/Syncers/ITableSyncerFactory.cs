namespace SmartTableSync.Syncers;

/// <summary>Creates the syncer for one table from its profile and plan. Called per run rather
/// than registered in DI, because the profile and plan are only known once the table has been
/// provisioned against the live parent schema.</summary>
internal interface ITableSyncerFactory
{
    ITableSyncer Create(TableProfile profile, SyncPlan plan);
}
