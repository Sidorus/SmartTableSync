namespace SmartTableSync.Syncers;

/// <summary>Creates a ConfigurableTableSyncer for one table, given its introspected shape
/// and derived sync plan. Replaces DI-registered per-table singleton syncers with
/// per-invocation construction, since the profile/plan are only known once a table has
/// been (re)provisioned (Phase 8), not at DI composition time.</summary>
public interface ITableSyncerFactory
{
    ITableSyncer Create(TableProfile profile, SyncPlan plan);
}
