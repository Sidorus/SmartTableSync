using Microsoft.Azure.Functions.Worker;

namespace SmartTableSync.Functions;

public sealed class SmartTableSyncTimerFunction
{
    private readonly ISyncOrchestrator _orchestrator;

    public SmartTableSyncTimerFunction(ISyncOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    [Function("SmartTableSync")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        await _orchestrator.RunAsync(ct);
    }
}
