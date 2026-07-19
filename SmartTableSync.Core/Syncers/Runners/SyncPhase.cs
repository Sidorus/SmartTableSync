namespace SmartTableSync.Syncers.Runners;

/// <summary>Which sync phase applies to a given call — the key ConfigurableTableSyncer
/// looks its ISyncRunner up by, so the selection reads by name instead of a positional
/// index.</summary>
public enum SyncPhase
{
    Initial,
    Incremental,
}
