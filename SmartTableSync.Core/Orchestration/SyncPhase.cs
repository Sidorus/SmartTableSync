namespace SmartTableSync.Orchestration;

/// <summary>Which sync phase applies to a given call — the key ConfigurableTableSyncer
/// looks its ISyncRunner up by, so the selection reads by name instead of a positional
/// index.</summary>
internal enum SyncPhase
{
    Initial,
    Incremental,
}
