using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Engine;

public enum Bound : byte
{
    None,
    Exact,
    Lower,   // the score is at least this (a move this good was found; search stopped early)
    Upper,   // the score is at most this (nothing beat alpha)
}

public struct TableEntry
{
    public ulong Key;
    public Move Move;
    public short Score;
    public sbyte Depth;
    public Bound Bound;
}

/// <summary>
/// The search's memory: what it already learned about a position (keyed by
/// Zobrist hash), so transpositions and later iterations don't redo work,
/// and the best move found last time can be tried first.
/// </summary>
public sealed class TranspositionTable
{
    private TableEntry[] _entries;

    public TranspositionTable(int megabytes = 64) => _entries = Allocate(megabytes);

    public void Resize(int megabytes) => _entries = Allocate(megabytes);

    public void Clear() => Array.Clear(_entries);

    public bool TryGet(ulong key, out TableEntry entry)
    {
        entry = _entries[key % (ulong)_entries.Length];
        return entry.Bound != Bound.None && entry.Key == key;
    }

    public void Store(ulong key, Move move, int score, int depth, Bound bound)
    {
        ref var slot = ref _entries[key % (ulong)_entries.Length];
        // Keep a deeper result for the same position unless this one is exact.
        if (slot.Key == key && slot.Depth > depth && bound != Bound.Exact)
            return;
        if (slot.Key == key && move == default)
            move = slot.Move;
        slot = new TableEntry
        {
            Key = key, Move = move, Score = (short)score, Depth = (sbyte)Math.Clamp(depth, -1, 127), Bound = bound,
        };
    }

    /// <summary>Per-mille of slots in use, for UCI "hashfull".</summary>
    public int Usage()
    {
        int sample = Math.Min(1000, _entries.Length), used = 0;
        for (int i = 0; i < sample; i++)
            if (_entries[i].Bound != Bound.None) used++;
        return used * 1000 / sample;
    }

    private static TableEntry[] Allocate(int megabytes)
    {
        long bytes = Math.Max(1, megabytes) * 1024L * 1024L;
        int count = (int)Math.Max(1024, bytes / 24);
        return new TableEntry[count];
    }
}
