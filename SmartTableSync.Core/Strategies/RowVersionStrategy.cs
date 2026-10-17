namespace SmartTableSync.Strategies;

/// <summary>Filters on the table's rowversion column. Detecting the column is enough to trust
/// it: SQL Server updates it on every insert and update.</summary>
internal sealed class RowVersionStrategy : IIncrementalStrategy
{
    public IncrementalStrategy Kind => IncrementalStrategy.RowVersion;

    public IncrementalBaseline? Baseline { get; } = new(
        CaptureAsync: async (parent, _, ct) => RowVersionSyncRunner.FormatPosition(await RowVersionSyncRunner.CaptureMinActiveRowVersionAsync(parent, ct)),
        IsOwnPosition: RowVersionSyncRunner.IsRowVersionPosition);

    public string? WhyNotApplicable(StrategyCandidate candidate) =>
        candidate.RowVersionColumn is not null ? null : "the table has no rowversion/timestamp-typed column.";

    public string? IncrementalColumn(StrategyCandidate candidate) => candidate.RowVersionColumn;

    public ISyncRunner CreateRunner(RunnerServices services) => new RowVersionSyncRunner(
        services.Profile, services.Plan, services.Commands, services.MergeStrategy, services.SyncState, services.Logger);
}
