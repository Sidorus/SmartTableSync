namespace SmartTableSync.Strategies.Abstractions;

/// <summary>What a strategy decides from, resolved once when a table is provisioned.</summary>
/// <param name="TimestampColumn">The configured (default "LastUpdate") datetime column, or null
/// when the table has no usable one.</param>
/// <param name="RowVersionColumn">The table's rowversion column, or null.</param>
/// <param name="AllowFullRescan">The operator accepted a full re-scan every run for this table.</param>
internal sealed record StrategyCandidate(
    TableProfile Profile,
    PlanHeuristicOptions Options,
    string? TimestampColumn,
    string? RowVersionColumn,
    bool AllowFullRescan);

/// <summary>Everything a strategy needs to build the incremental runner for one table.</summary>
internal sealed record RunnerServices(
    TableProfile Profile,
    SyncPlan Plan,
    TableSyncCommandFactory Commands,
    IBatchMergeStrategy MergeStrategy,
    ISyncStateRepository SyncState,
    ISyncMetricsReporter Metrics,
    ILogger Logger);

/// <summary>
/// One way of finding what changed in a table since the last run, described in one place:
/// when it can be used, which column it reads, the runner that executes it, and the position it
/// records before the initial copy. IncrementalStrategies.All lists them in auto-selection order.
/// Adding a strategy means adding one implementation and one entry in that list.
/// </summary>
internal interface IIncrementalStrategy
{
    IncrementalStrategy Kind { get; }

    /// <summary>Position to record before the initial copy, or null when the strategy keeps no
    /// position of its own.</summary>
    IncrementalBaseline? Baseline { get; }

    /// <summary>Null when the strategy can be used for this table; otherwise the reason it can't,
    /// worded for an operator.</summary>
    string? WhyNotApplicable(StrategyCandidate candidate);

    /// <summary>The column the strategy filters on (SyncPlan.IncrementalColumn), or null.</summary>
    string? IncrementalColumn(StrategyCandidate candidate);

    ISyncRunner CreateRunner(RunnerServices services);
}
