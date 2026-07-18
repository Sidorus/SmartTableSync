namespace SmartTableSync.Options;

/// <summary>Which tables SyncOrchestrator should sync. Bound from a single delimited
/// setting rather than IConfiguration's native indexed array binding, since a single
/// comma-separated value is far easier to enter as one Azure App Setting / env var.</summary>
public sealed class SyncSettings
{
    public IReadOnlyList<string> Tables { get; set; } = [];
}
