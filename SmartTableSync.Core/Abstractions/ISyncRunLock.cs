namespace SmartTableSync.Abstractions;

/// <summary>Guarantees that only one sync run touches a child database at a time, across every
/// process and machine - not just within one process. Needed whenever more than one host
/// instance can run the orchestrator: a scaled-out Function App, several containers, or a
/// manual run started while the timer run is still going.</summary>
public interface ISyncRunLock
{
    /// <summary>Tries to take the lock without waiting. Returns a handle that releases the lock
    /// when disposed, or null when another run already holds it.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct);
}
