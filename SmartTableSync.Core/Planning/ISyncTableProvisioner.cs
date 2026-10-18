namespace SmartTableSync.Planning;

/// <summary>Ties introspection (Introspection/), plan derivation (Planning/), and
/// persisted config together: given a table name, returns a ready-to-use SyncPlan,
/// (re)provisioning automatically if there's no config yet or the parent schema changed.
/// Also returns the TableProfile it already had in hand while doing so (the cheap
/// fingerprint-check one on the no-drift path, the full ProfileTableAsync result when it
/// just re-provisioned) so callers building a syncer from the result never need a second,
/// redundant introspection call of their own.</summary>
internal interface ISyncTableProvisioner
{
    /// <summary>PlanAsync followed by SaveAsync. WasFreshlyProvisioned is true only on the
    /// new-table-or-schema-drift path (false on the common fast path where an existing config's
    /// fingerprint still matches).</summary>
    Task<(TableProfile Profile, SyncPlan Plan, bool WasFreshlyProvisioned)> EnsureProvisionedAsync(SqlConnection parent, SqlConnection child, string tableName, CancellationToken ct);

    /// <summary>Returns the saved plan when the parent schema still matches it, or derives a new
    /// one (Change NewTable or SchemaChanged) without saving it yet.</summary>
    Task<TableProvisioning> PlanAsync(SqlConnection parent, SqlConnection child, string tableName, CancellationToken ct);

    /// <summary>Saves a plan PlanAsync derived. Until it's saved, every run derives it again, so
    /// work that has to happen first (Auto mode's child DDL) is retried if it failed.</summary>
    Task SaveAsync(SqlConnection child, TableProvisioning provisioning, CancellationToken ct);
}
