namespace SmartTableSync.Strategies;

/// <summary>Reads changes from SQL Server Change Tracking. Chosen whenever a DBA has enabled
/// Change Tracking on the table — SmartTableSync never enables it itself.</summary>
public sealed class SqlChangeTrackingStrategy : IIncrementalStrategy
{
    public IncrementalStrategy Kind => IncrementalStrategy.SqlChangeTracking;

    public IncrementalBaseline? Baseline { get; } = new(
        CaptureAsync: async (parent, tableName, ct) =>
        {
            var (_, current) = await ChangeTrackingVersions.GetBoundsAsync(parent, null, tableName, ct);
            return ChangeTrackingVersions.FormatPosition(current);
        },
        IsOwnPosition: position => ChangeTrackingVersions.ParsePosition(position) is not null);

    // SQL Server refuses to enable Change Tracking without a PRIMARY KEY; the key check is a guard.
    public string? WhyNotApplicable(StrategyCandidate candidate) =>
        candidate.Profile.IsChangeTrackingEnabled && candidate.Profile.PrimaryKeyColumns.Count > 0
            ? null
            : "SQL Server Change Tracking is not enabled on this table — a DBA must run ALTER TABLE ... ENABLE " +
              "CHANGE_TRACKING first (see SmartTableSync.Scaffold --change-tracking-setup).";

    public string? IncrementalColumn(StrategyCandidate candidate) => null;

    public ISyncRunner CreateRunner(RunnerServices services) => new ChangeTrackingSyncRunner(
        services.Profile, services.Plan, services.Commands, services.MergeStrategy,
        new FullRescanSyncRunner(services.Profile, services.Plan, services.Commands, services.MergeStrategy, services.Logger),
        new DeleteReconciler(services.Profile, services.Commands, services.Logger),
        services.SyncState, services.Metrics, services.Logger);
}
