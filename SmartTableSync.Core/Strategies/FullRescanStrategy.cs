namespace SmartTableSync.Strategies;

/// <summary>Re-reads the whole table every run. Allowed automatically for small tables (up to
/// PlanHeuristicOptions.FullRescanRowCountThreshold rows); a larger table needs the operator's
/// explicit consent through SmartTableSync:AllowFullRescanFor, so an expensive re-scan every
/// few minutes never starts silently.</summary>
internal sealed class FullRescanStrategy : IIncrementalStrategy
{
    public IncrementalStrategy Kind => IncrementalStrategy.FullRescan;

    public IncrementalBaseline? Baseline => null;

    public string? WhyNotApplicable(StrategyCandidate candidate)
    {
        var tableName = candidate.Profile.TableName;
        var threshold = candidate.Options.FullRescanRowCountThreshold;

        return candidate.Profile.RowCount <= threshold || candidate.AllowFullRescan
            ? null
            : $"no usable change-detection column and {candidate.Profile.RowCount} rows exceeds the FullRescan " +
              $"threshold ({threshold}) — set SmartTableSync:TimestampColumns:{tableName} to the correct column " +
              $"name, or add '{tableName}' to SmartTableSync:AllowFullRescanFor to accept a full re-scan every run.";
    }

    public string? IncrementalColumn(StrategyCandidate candidate) => null;

    public ISyncRunner CreateRunner(RunnerServices services) => new FullRescanSyncRunner(
        services.Profile, services.Plan, services.Commands, services.MergeStrategy, services.Logger);
}
