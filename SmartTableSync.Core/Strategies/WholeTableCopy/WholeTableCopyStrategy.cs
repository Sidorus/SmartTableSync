namespace SmartTableSync.Strategies.WholeTableCopy;

/// <summary>Replaces the whole child table every run. The strategy for a table without any key:
/// SyncPlanHeuristic chooses it by itself for such a table, and only for such a table; a table
/// with a key gets it only through SmartTableSync:IncrementalStrategyOverrides. Copying every row
/// every run is expensive, so it follows FullRescan's size rule: automatic up to
/// PlanHeuristicOptions.FullRescanRowCountThreshold rows, above that only for a table listed in
/// SmartTableSync:AllowFullRescanFor.</summary>
internal sealed class WholeTableCopyStrategy : IIncrementalStrategy
{
    public IncrementalStrategy Kind => IncrementalStrategy.WholeTableCopy;

    public IncrementalBaseline? Baseline => null;

    public string? WhyNotApplicable(StrategyCandidate candidate)
    {
        var tableName = candidate.Profile.TableName;
        var threshold = candidate.Options.FullRescanRowCountThreshold;

        return candidate.Profile.RowCount <= threshold || candidate.AllowFullRescan
            ? null
            : $"{candidate.Profile.RowCount} rows exceeds the threshold ({threshold}) for copying the whole table every run - " +
              $"add '{tableName}' to SmartTableSync:AllowFullRescanFor to accept a whole-table copy every run, or give the " +
              $"table a key (a PRIMARY KEY, a unique index, or SmartTableSync:KeyColumns:{tableName}) so only changes are copied.";
    }

    public string? IncrementalColumn(StrategyCandidate candidate) => null;

    public ISyncRunner CreateRunner(RunnerServices services) =>
        new WholeTableCopySyncRunner(services.Profile, services.Plan, services.Logger);
}
