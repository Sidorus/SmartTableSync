namespace SmartTableSync.Orchestration;

public interface ISyncOrchestrator
{
    Task RunAsync(CancellationToken ct = default);
}
