namespace SmartTableSync.Strategies;

/// <summary>Filters on a datetime column the application updates on every write (configured
/// per table via SmartTableSync:TimestampColumns, default "LastUpdate"). Its position is the
/// orchestrator's LastSyncDate, so it records no baseline of its own.</summary>
internal sealed class TimestampStrategy : IIncrementalStrategy
{
    public IncrementalStrategy Kind => IncrementalStrategy.Timestamp;

    public IncrementalBaseline? Baseline => null;

    public string? WhyNotApplicable(StrategyCandidate candidate) =>
        candidate.TimestampColumn is not null
            ? null
            : "the configured (or default \"LastUpdate\") timestamp column doesn't exist on this table.";

    public string? IncrementalColumn(StrategyCandidate candidate) => candidate.TimestampColumn;

    public ISyncRunner CreateRunner(RunnerServices services) => new TimestampSyncRunner(
        services.Profile, services.Plan, services.Commands, services.MergeStrategy, services.Logger);
}
