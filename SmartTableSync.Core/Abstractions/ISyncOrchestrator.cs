namespace SmartTableSync.Abstractions;

public interface ISyncOrchestrator
{
    Task RunAsync(CancellationToken ct = default);
}
