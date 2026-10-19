namespace SmartTableSync.Strategies.SqlChangeTracking;

/// <summary>Reads changes from SQL Server Change Tracking. Chosen whenever a DBA has enabled
/// Change Tracking on the table — SmartTableSync never enables it itself.</summary>
internal sealed class SqlChangeTrackingStrategy : IIncrementalStrategy
{
    public IncrementalStrategy Kind => IncrementalStrategy.SqlChangeTracking;

    public IncrementalBaseline? Baseline { get; } = new(
        CaptureAsync: async (parent, tableName, ct) =>
        {
            var (_, current) = await ChangeTrackingVersions.GetBoundsAsync(parent, null, tableName, ct);
            return ChangeTrackingVersions.FormatPosition(current);
        },
        IsOwnPosition: position => ChangeTrackingVersions.ParsePosition(position) is not null);

    // SQL Server refuses to enable Change Tracking without a PRIMARY KEY, and reports changes by
    // its columns; a key taken from a unique index or configuration can't stand in for it.
    public string? WhyNotApplicable(StrategyCandidate candidate) =>
        !candidate.Profile.IsChangeTrackingEnabled
            ? "SQL Server Change Tracking is not enabled on this table — a DBA must run ALTER TABLE ... ENABLE " +
              "CHANGE_TRACKING first (see SmartTableSync.Scaffold --change-tracking-setup)."
            : candidate.Profile.KeySource != SyncKeySource.PrimaryKey || candidate.Profile.PrimaryKeyColumns.Count == 0
                ? "SQL Server Change Tracking needs a PRIMARY KEY constraint, and this table has none."
                : null;

    public string? IncrementalColumn(StrategyCandidate candidate) => null;

    public ISyncRunner CreateRunner(RunnerServices services) => new ChangeTrackingSyncRunner(
        services.Profile, services.Plan, services.Commands, services.MergeStrategy,
        new FullRescanSyncRunner(services.Profile, services.Plan, services.Commands, services.MergeStrategy, services.Logger),
        new DeleteReconciler(services.Profile, services.Commands, services.Logger),
        services.SyncState, services.Metrics, services.Logger);
}
