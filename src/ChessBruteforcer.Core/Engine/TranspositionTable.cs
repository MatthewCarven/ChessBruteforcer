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
///
/// Every search thread shares one table, with no locks.  A slot is two
/// 64-bit words, the entry packed into one and the key XORed with it in the
/// other (Hyatt and Mann's lockless hashing).  Each word is written whole,
/// but two threads can interleave their pairs; a slot mixed that way no
/// longer XORs back to either key, so it reads as a miss rather than as
/// another position's result.
/// </summary>
public sealed class TranspositionTable
{
    private struct Slot
    {
        public ulong Check;   // the key XOR Data
        public ulong Data;    // move (32 bits), score (16), depth (8), bound (8)
    }

    private Slot[] _slots;

    public TranspositionTable(int megabytes = 64) => _slots = Allocate(megabytes);

    public void Resize(int megabytes) => _slots = Allocate(megabytes);

    public void Clear() => Array.Clear(_slots);

    public bool TryGet(ulong key, out TableEntry entry)
    {
        ref var slot = ref _slots[key % (ulong)_slots.Length];
        ulong data = slot.Data, check = slot.Check;   // (read once each: another thread may be writing)
        entry = Unpack(check ^ data, data);
        return entry.Bound != Bound.None && entry.Key == key;
    }

    public void Store(ulong key, Move move, int score, int depth, Bound bound)
    {
        ref var slot = ref _slots[key % (ulong)_slots.Length];
        ulong oldData = slot.Data;
        var old = Unpack(slot.Check ^ oldData, oldData);
        // Keep a deeper result for the same position unless this one is exact.
        if (old.Key == key && old.Depth > depth && bound != Bound.Exact)
            return;
        if (old.Key == key && move == default)
            move = old.Move;
        ulong data = Pack(move, (short)score, (sbyte)Math.Clamp(depth, -1, 127), bound);
        slot.Data = data;
        slot.Check = key ^ data;
    }

    /// <summary>Per-mille of slots in use, for UCI "hashfull".</summary>
    public int Usage()
    {
        int sample = Math.Min(1000, _slots.Length), used = 0;
        for (int i = 0; i < sample; i++)
            if (Unpack(0, _slots[i].Data).Bound != Bound.None) used++;
        return used * 1000 / sample;
    }

    private static ulong Pack(Move move, short score, sbyte depth, Bound bound) =>
        move.From | (ulong)move.To << 8 | (ulong)move.Promotion << 16 | (ulong)move.Flags << 24
        | (ulong)(ushort)score << 32 | (ulong)(byte)depth << 48 | (ulong)bound << 56;

    private static TableEntry Unpack(ulong key, ulong data) => new()
    {
        Key = key,
        Move = new Move((byte)data, (byte)(data >> 8), (PieceType)(byte)(data >> 16), (MoveFlags)(byte)(data >> 24)),
        Score = (short)(data >> 32),
        Depth = (sbyte)(byte)(data >> 48),
        Bound = (Bound)(byte)(data >> 56),
    };

    private static Slot[] Allocate(int megabytes)
    {
        long bytes = Math.Max(1, megabytes) * 1024L * 1024L;
        // A slot is 16 bytes; the table has always been sized at 24 a slot, kept so that a
        // given Hash makes the same number of slots and so the same search as before.
        int count = (int)Math.Max(1024, bytes / 24);
        return new Slot[count];
    }
}
