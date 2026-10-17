namespace SmartTableSync.Planning;

/// <summary>Runtime safety net, separate from the heuristic itself: on a batch timeout,
/// halve the batch size and retry once rather than trusting the heuristic's first answer
/// forever. Pure/stateless so it's trivially unit-testable without a real timeout.</summary>
internal static class BatchSizeBackoff
{
    public static int Halve(int current, int floor = 1) => Math.Max(floor, current / 2);
}
