using System.Runtime.InteropServices;
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
///
/// Slots come in buckets of four, one 64-byte cache line, and a position may
/// sit in any slot of its bucket.  Each entry carries the number of the
/// search that stored or last used it (its generation, moved on by every
/// search, "go ponder" included).  A position already in the bucket is
/// updated in place; otherwise the new entry takes the slot worth least: an
/// empty one, else the one with the lowest depth less <see cref="AgePenalty"/>
/// for each search since its own.  So a flood of shallow entries (an 8-thread
/// ponder on the reply that wasn't played) pushes out other shallow entries
/// and stale ones, not the deep ones the search before it left (PONDER.md, P6).
/// </summary>
public sealed class TranspositionTable
{
    /// <summary>Slots a bucket: four 16-byte slots are one 64-byte cache line.</summary>
    public const int BucketSlots = 4;

    /// <summary>
    /// Plies of depth an entry is worth less, when choosing which to replace,
    /// for each search since it was stored or last used.  A ponder's flood is
    /// mostly entries of depth 1-2, so with 2 the search before it keeps its
    /// entries of depth 4 and more through it; an entry nothing uses again is
    /// worth less than any new one within a few moves (pondering moves the
    /// generation on twice a move).
    /// </summary>
    public const int AgePenalty = 2;

    private const int Generations = 64;   // six bits, the top of the bound's byte
    private const int WordsPerBucket = 2 * BucketSlots;
    private const int DepthShift = 48, BoundShift = 56, GenerationShift = 58;
    private const ulong GenerationMask = (ulong)(Generations - 1) << GenerationShift;

    // Two words a slot, Check (the key XOR Data) then Data: the move (32 bits), score (16), depth (8), bound (2)
    // and generation (6).  Pinned, so that bucket 0 can start on a cache line and stays there.
    private ulong[] _words = Array.Empty<ulong>();
    private int _first;        // the word bucket 0 starts at
    private ulong _buckets;
    private int _generation;   // the search running now (or the last one), 0-63

    public TranspositionTable(int megabytes = 64) => Allocate(megabytes);

    /// <summary>Between searches only (the UCI engine stops any search first).</summary>
    public void Resize(int megabytes) => Allocate(megabytes);

    public void Clear()
    {
        Array.Clear(_words);
        _generation = 0;
    }

    /// <summary>Slots in the table, <see cref="BucketSlots"/> a bucket.</summary>
    public long Slots => (long)_buckets * BucketSlots;

    /// <summary>A new search begins ("go", "go ponder"): what it stores is newer than anything there.</summary>
    public void NewSearch() => _generation = (_generation + 1) % Generations;

    /// <summary>The current search's number, 0-63 (for tests).</summary>
    internal int Generation => _generation;

    /// <summary>Where bucket 0 is in memory (for tests: on a cache line).</summary>
    internal long FirstBucketAddress => (long)Marshal.UnsafeAddrOfPinnedArrayElement(_words, BucketStart(0));

    public bool TryGet(ulong key, out TableEntry entry)
    {
        int first = BucketStart(key);
        for (int at = first; at < first + WordsPerBucket; at += 2)
        {
            ulong data = _words[at + 1], check = _words[at];   // (read once each: another thread may be writing)
            if ((check ^ data) != key || BoundOf(data) == Bound.None)
                continue;
            entry = Unpack(key, data);
            // In use again, so as good as new: a store into this bucket won't take it for stale.
            if (GenerationOf(data) != _generation)
                Write(at, key, data & ~GenerationMask | (ulong)_generation << GenerationShift);
            return true;
        }
        entry = default;
        return false;
    }

    /// <summary>
    /// <see cref="TryGet"/> without its side effect: an old entry looked at here stays old, so what a later store
    /// replaces and what <see cref="Usage"/> counts are as if nothing had looked.  For reading the table outside the
    /// search: the line "info" shows past where the search's own stops (PONDER.md, option (a)).  (A loop of its own,
    /// so TryGet's, run at every node, stays as it was.)
    /// </summary>
    public bool TryPeek(ulong key, out TableEntry entry)
    {
        int first = BucketStart(key);
        for (int at = first; at < first + WordsPerBucket; at += 2)
        {
            ulong data = _words[at + 1], check = _words[at];
            if ((check ^ data) != key || BoundOf(data) == Bound.None)
                continue;
            entry = Unpack(key, data);
            return true;
        }
        entry = default;
        return false;
    }

    public void Store(ulong key, Move move, int score, int depth, Bound bound)
    {
        int first = BucketStart(key), replace = first, lowest = int.MaxValue;
        for (int at = first; at < first + WordsPerBucket; at += 2)
        {
            ulong data = _words[at + 1], check = _words[at];
            if ((check ^ data) == key && BoundOf(data) != Bound.None)
            {
                // The same position: update it in place, but keep a deeper result unless this one is exact.
                var old = Unpack(key, data);
                if (old.Depth > depth && bound != Bound.Exact)
                    return;
                if (move == default)
                    move = old.Move;
                replace = at;
                break;
            }
            int worth = Worth(data);
            if (worth < lowest)
                (lowest, replace) = (worth, at);
        }
        Write(replace, key, Pack(move, (short)score, (sbyte)Math.Clamp(depth, -1, 127), bound, _generation));
    }

    /// <summary>Per-mille of slots holding an entry the current search stored or used, for UCI "hashfull".</summary>
    public int Usage()
    {
        int sample = (int)Math.Min(1000, Slots), used = 0;
        for (int i = 0; i < sample; i++)
        {
            ulong data = _words[_first + 2 * i + 1];
            if (BoundOf(data) != Bound.None && GenerationOf(data) == _generation)
                used++;
        }
        return used * 1000 / sample;
    }

    /// <summary>What a slot's entry is worth keeping: its depth, less <see cref="AgePenalty"/> a search since its own; an empty slot least.</summary>
    private int Worth(ulong data) =>
        BoundOf(data) == Bound.None ? int.MinValue
        : (sbyte)(byte)(data >> DepthShift) - AgePenalty * ((_generation - GenerationOf(data)) & (Generations - 1));

    // The key's high bits pick the bucket (a multiply, where a remainder would be a division); all 64 are checked.
    private int BucketStart(ulong key) => _first + (int)Math.BigMul(key, _buckets, out _) * WordsPerBucket;

    private void Write(int at, ulong key, ulong data)
    {
        _words[at + 1] = data;
        _words[at] = key ^ data;
    }

    private static ulong Pack(Move move, short score, sbyte depth, Bound bound, int generation) =>
        move.From | (ulong)move.To << 8 | (ulong)move.Promotion << 16 | (ulong)move.Flags << 24
        | (ulong)(ushort)score << 32 | (ulong)(byte)depth << DepthShift | (ulong)bound << BoundShift
        | (ulong)generation << GenerationShift;

    private static Bound BoundOf(ulong data) => (Bound)(data >> BoundShift & 3);

    private static int GenerationOf(ulong data) => (int)(data >> GenerationShift);

    private static TableEntry Unpack(ulong key, ulong data) => new()
    {
        Key = key,
        Move = new Move((byte)data, (byte)(data >> 8), (PieceType)(byte)(data >> 16), (MoveFlags)(byte)(data >> 24)),
        Score = (short)(data >> 32),
        Depth = (sbyte)(byte)(data >> DepthShift),
        Bound = BoundOf(data),
    };

    private void Allocate(int megabytes)
    {
        // Hash is megabytes, a bucket 64 bytes.  (Until P6 the table was sized at 24 bytes a 16-byte slot, so that
        // a given Hash kept the slots, and so the one-thread search, it had before the table went lockless.
        // Buckets and ages change that search anyway, so Hash now means what it says: Hash 256 is 1.5x the slots.)
        long bytes = Math.Max(1, megabytes) * 1024L * 1024L;
        long buckets = bytes / 64;
        var words = GC.AllocateArray<ulong>((int)(buckets * WordsPerBucket + 7), pinned: true);
        long address = (long)Marshal.UnsafeAddrOfPinnedArrayElement(words, 0);
        _first = (int)(-address & 63) / 8;
        _buckets = (ulong)buckets;
        _words = words;
    }
}
