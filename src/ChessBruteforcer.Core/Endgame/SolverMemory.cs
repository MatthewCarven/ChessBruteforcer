namespace ChessBruteforcer.Core.Endgame;

/// <summary>
/// What the retrograde solver held for one table: its per-slot arrays (the
/// table's own values included), and its per-ply queues at their largest.
/// </summary>
public readonly record struct SolverMemory(long ArrayBytes, long QueueBytes, long QueueEntries)
{
    public long TotalBytes => ArrayBytes + QueueBytes;
}
