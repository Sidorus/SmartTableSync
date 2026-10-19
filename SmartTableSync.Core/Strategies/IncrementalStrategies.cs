namespace SmartTableSync.Strategies;

/// <summary>The registry of incremental strategies.</summary>
internal static class IncrementalStrategies
{
    /// <summary>In auto-selection priority order - the first applicable strategy wins:
    /// <list type="number">
    /// <item>SqlChangeTracking - a DBA enabled Change Tracking: changed keys and deletes come from SQL Server itself.</item>
    /// <item>RowVersion - the table has a rowversion column, which SQL Server updates on every write.</item>
    /// <item>Timestamp - the table has the configured (default "LastUpdate") datetime column; trusts the application to update it.</item>
    /// <item>FullRescan - none of the above: small tables, or tables listed in AllowFullRescanFor.</item>
    /// </list>
    /// An IncrementalStrategyOverrides entry skips this order but must still be applicable.</summary>
    public static IReadOnlyList<IIncrementalStrategy> All { get; } =
    [
        new SqlChangeTrackingStrategy(),
        new RowVersionStrategy(),
        new TimestampStrategy(),
        new FullRescanStrategy(),
    ];

    public static IIncrementalStrategy For(IncrementalStrategy kind) =>
        All.FirstOrDefault(s => s.Kind == kind)
        ?? throw new NotSupportedException($"No incremental strategy is registered for {kind}.");
}
