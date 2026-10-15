using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ChessBruteforcer.Core.Endgame;

/// <summary>
/// Decompressed blocks of compressed tables (see <see cref="CompressedTable"/>),
/// one budget for all of them: every table opened with this cache shares
/// its places, so memory never passes the budget however many tables there
/// are, and a table probed often can hold more than one that isn't.  (Each
/// table had a direct-mapped cache of its own before, 64 MB at most and
/// never let go: up to 14 GB for the 220 5-piece tables.  WORKLOG,
/// 2026-10-16.)  A place's memory is taken the first time it is used, so a
/// budget nobody reaches costs nothing.
///
/// The places come in sets of 16; a block (its table and number) belongs to
/// one set, chosen by a hash, and can be in any place of it.  When a set is
/// full, a clock hand picks what goes: it passes over blocks read since it
/// last came by (clearing their mark) and takes the first that wasn't.
///
/// Read without a lock, since several search threads probe at once.  A place
/// has a version, odd while one thread fills it: a value read while the
/// version stayed the same even number is good (a seqlock).  Filling is the
/// job of the one thread that moves the version from even to odd; it names
/// the block first, so a read that wants that very block waits for it
/// (rather than decompressing it twice) and every other read passes by.
/// </summary>
public sealed class BlockCache
{
    public const int BlockBytes = 1 << 16;
    public const int Ways = 16;

    /// <summary>The budget a <see cref="Tablebase"/> has unless told otherwise, in MB.</summary>
    public const int DefaultMegabytes = 512;

    private const long Empty = -1;

    private sealed class Places
    {
        public readonly int Ways, Sets;
        public readonly long[] Keys;       // each place's block: (table << 32) | block number, or Empty
        public readonly int[] Version;     // each place's, odd while it is being filled
        public readonly byte[]?[] Data;
        public readonly byte[] Used;       // read since the hand last passed
        public readonly int[] Hand;        // each set's

        public Places(int blocks, int ways)
        {
            Ways = Math.Min(ways, blocks);
            Sets = blocks / Ways;
            int count = Sets * Ways;
            Keys = new long[count];
            Array.Fill(Keys, Empty);
            Version = new int[count];
            Data = new byte[count][];
            Used = new byte[count];
            Hand = new int[Sets];
        }

        // A block's set: the high bits of a multiplicative hash, scaled to the number of sets.
        public int SetOf(long key) => (int)Math.BigMul((ulong)key * 0x9E3779B97F4A7C15UL, (ulong)Sets, out _);
    }

    private volatile Places _places;

    public BlockCache(int megabytes = DefaultMegabytes) : this(BlocksIn(megabytes), Ways)
    {
    }

    /// <summary>A cache of exactly <paramref name="blocks"/> places, in sets of at most <paramref name="ways"/>.</summary>
    public BlockCache(int blocks, int ways)
    {
        if (blocks < 1 || ways < 1)
            throw new ArgumentOutOfRangeException(nameof(blocks), "A cache holds one block or more.");
        _places = new Places(blocks, ways);
    }

    private static int BlocksIn(int megabytes) =>
        (int)Math.Clamp(megabytes * (1L << 20) / BlockBytes, 1, int.MaxValue);

    /// <summary>How many blocks it can hold.</summary>
    public int Blocks => _places.Keys.Length;

    /// <summary>Its budget, in MB.</summary>
    public long Megabytes => (long)Blocks * BlockBytes >> 20;

    /// <summary>Change the budget, emptying the cache (its memory goes once no read holds it).</summary>
    public void Resize(int megabytes) => _places = new Places(BlocksIn(megabytes), Ways);

    /// <summary>The value at <paramref name="within"/> (values from the block's start) in a table's block.</summary>
    internal short Read(CompressedTable table, int block, int within)
    {
        var places = _places;
        long key = (long)table.CacheId << 32 | (uint)block;
        int first = places.SetOf(key) * places.Ways;
        int end = first + places.Ways;
        var keys = places.Keys;
        var versions = places.Version;
        long waiting = 0;   // since when, if this read had to wait for another thread
        bool same = false;  // and whether for this very block
        var spinner = new SpinWait();
        while (true)
        {
            int place = -1;
            for (int p = first; p < end; p++)
            {
                if (Volatile.Read(ref keys[p]) == key)
                {
                    place = p;
                    break;
                }
            }
            if (place >= 0)
            {
                // Every read here is an acquire, so each happens before the next: the version, the
                // place's block, the value, then the version again.
                int version = Volatile.Read(ref versions[place]);
                if ((version & 1) == 0)
                {
                    var bytes = Volatile.Read(ref keys[place]) == key ? Volatile.Read(ref places.Data[place]) : null;
                    if (bytes is not null)
                    {
                        short value = table.IsDtz
                            ? EndgameTable.FromByte((sbyte)Volatile.Read(ref bytes[within]))
                            : Volatile.Read(ref Unsafe.As<byte, short>(ref bytes[within * sizeof(short)]));
                        if (Volatile.Read(ref versions[place]) == version)
                        {
                            if (places.Used[place] == 0)
                                places.Used[place] = 1;
                            if (waiting != 0)
                                table.CountWait(waiting, same);
                            return value;
                        }
                    }
                    continue;   // refilled or emptied as it was read: look again
                }
                // Another thread is decompressing this very block: wait for it.
                if (waiting == 0)
                {
                    waiting = Stopwatch.GetTimestamp();
                    same = true;
                }
                spinner.SpinOnce(sleep1Threshold: -1);   // (a fill takes ~100 us; a Sleep(1) can take 15 ms)
                continue;
            }

            // Not there: take a place in the set and fill it.
            int claimed = Claim(places, first, key, out int odd);
            if (claimed >= 0)
            {
                Fill(places, table, block, claimed, odd);
                continue;   // and read it like any other
            }
            if (claimed == Taken)
                continue;   // another thread is filling it: wait for that one
            // Every place in the set is being filled by other threads (it takes 16 at once): wait.
            if (waiting == 0)
                waiting = Stopwatch.GetTimestamp();
            spinner.SpinOnce(sleep1Threshold: -1);
        }
    }

    private const int Busy = -1, Taken = -2;

    /// <summary>
    /// Take a place in the set starting at <paramref name="first"/> for a
    /// block: the clock hand's choice, its version moved to odd (returned in
    /// <paramref name="claimed"/>) and the block named in it.  Busy if every
    /// place is being filled; Taken if another thread turns out to be taking
    /// the same block.
    /// </summary>
    private static int Claim(Places places, int first, long key, out int claimed)
    {
        claimed = 0;
        int ways = places.Ways;
        int set = first / ways;
        int hand = places.Hand[set];
        for (int step = 0; step < 2 * ways; step++, hand = hand + 1 == ways ? 0 : hand + 1)
        {
            int place = first + hand;
            int version = Volatile.Read(ref places.Version[place]);
            if ((version & 1) != 0)
                continue;   // being filled
            if (places.Used[place] != 0)
            {
                places.Used[place] = 0;   // a second chance
                continue;
            }
            if (Interlocked.CompareExchange(ref places.Version[place], version + 1, version) != version)
                continue;
            places.Hand[set] = hand + 1 == ways ? 0 : hand + 1;
            // Name the block (a full fence, so the look below sees a claim another thread made
            // before it), then make sure no earlier place in the set has it or is taking it: of
            // two threads taking places for one block at once, the one with the later place lets
            // go.  (If they miss each other both keep one, which costs a decompression, no more.)
            Interlocked.Exchange(ref places.Keys[place], key);
            for (int p = first; p < place; p++)
            {
                if (Volatile.Read(ref places.Keys[p]) == key)
                {
                    places.Keys[place] = Empty;
                    Volatile.Write(ref places.Version[place], version + 2);
                    return Taken;
                }
            }
            claimed = version + 1;
            return place;
        }
        return Busy;
    }

    /// <summary>Decompress a block into a place this thread has claimed, and let the place go however it ends (empty if this failed).</summary>
    private static void Fill(Places places, CompressedTable table, int block, int place, int claimed)
    {
        bool filled = false;
        try
        {
            var bytes = places.Data[place] ??= new byte[BlockBytes];
            table.Decompress(block, bytes);
            places.Used[place] = 1;   // (it is about to be read)
            filled = true;
        }
        finally
        {
            if (!filled)
                places.Keys[place] = Empty;
            Volatile.Write(ref places.Version[place], claimed + 1);   // even again: everything above is seen before it
        }
    }

    /// <summary>Empty the cache and let go of its memory, so the next reads decompress their blocks again (for a bench).</summary>
    public void Clear()
    {
        var places = _places;
        for (int place = 0; place < places.Keys.Length; place++)
        {
            var spinner = new SpinWait();
            int version;
            while (((version = Volatile.Read(ref places.Version[place])) & 1) != 0
                   || Interlocked.CompareExchange(ref places.Version[place], version + 1, version) != version)
                spinner.SpinOnce(sleep1Threshold: -1);
            places.Keys[place] = Empty;
            places.Data[place] = null;
            places.Used[place] = 0;
            Volatile.Write(ref places.Version[place], version + 2);
        }
    }

    /// <summary>The memory its blocks hold now, in bytes.</summary>
    public long BytesHeld => _places.Data.Sum(bytes => (long)(bytes?.Length ?? 0));
}
