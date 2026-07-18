namespace SmartTableSync.Configuration;

/// <summary>Ties introspection (Introspection/), plan derivation (Planning/), and
/// persisted config together: given a table name, returns a ready-to-use SyncPlan,
/// (re)provisioning automatically if there's no config yet or the parent schema changed.</summary>
public interface ISyncTableProvisioner
{
    Task<SyncPlan> EnsureProvisionedAsync(SqlConnection parent, SqlConnection child, string tableName, CancellationToken ct);
}
