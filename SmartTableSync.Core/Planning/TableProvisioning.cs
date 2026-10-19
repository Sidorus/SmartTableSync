namespace SmartTableSync.Planning;

/// <summary>Why a table's plan was (re)derived on this run.</summary>
internal enum ProvisioningChange
{
    /// <summary>The saved plan still matches the parent schema.</summary>
    None,

    /// <summary>No saved plan yet: the table is synced for the first time.</summary>
    NewTable,

    /// <summary>The parent schema changed since the saved plan was derived.</summary>
    SchemaChanged,
}

/// <summary>A table's profile and plan for this run. PendingConfig is the new plan to save, set
/// only when Change isn't None; it's saved separately (ISyncTableProvisioner.SaveAsync) so Auto
/// mode can save it only after the child objects match it, and a failed attempt is retried.</summary>
internal sealed record TableProvisioning(
    TableProfile Profile,
    SyncPlan Plan,
    ProvisioningChange Change,
    SyncTableConfig? PendingConfig);
