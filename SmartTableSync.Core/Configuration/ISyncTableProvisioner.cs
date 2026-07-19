namespace SmartTableSync.Configuration;

/// <summary>Ties introspection (Introspection/), plan derivation (Planning/), and
/// persisted config together: given a table name, returns a ready-to-use SyncPlan,
/// (re)provisioning automatically if there's no config yet or the parent schema changed.
/// Also returns the TableProfile it already had in hand while doing so (the cheap
/// fingerprint-check one on the no-drift path, the full ProfileTableAsync result when it
/// just re-provisioned) so callers building a syncer from the result never need a second,
/// redundant introspection call of their own.</summary>
public interface ISyncTableProvisioner
{
    Task<(TableProfile Profile, SyncPlan Plan)> EnsureProvisionedAsync(SqlConnection parent, SqlConnection child, string tableName, CancellationToken ct);
}
