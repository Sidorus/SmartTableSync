namespace SmartTableSync.Worker;

/// <summary>How the container host runs the sync (section <c>SmartTableSync:Worker</c>).</summary>
public sealed class WorkerSettings
{
    public const string SectionName = "SmartTableSync:Worker";

    /// <summary>Minutes between runs when the host keeps running. Values below 1 mean 1.</summary>
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>Run once and exit, for a scheduler that starts the container on a cron. Also set
    /// with the <c>--once</c> command-line argument.</summary>
    public bool RunOnce { get; set; }
}
