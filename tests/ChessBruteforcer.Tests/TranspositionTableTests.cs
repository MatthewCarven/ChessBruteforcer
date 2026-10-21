using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Tests;

/// <summary>
/// The table's buckets and ages (P6 in PONDER.md).  A position goes in any
/// of its bucket's four slots; a new one takes the slot worth least, an
/// empty one or else the lowest depth less <see cref="TranspositionTable.AgePenalty"/>
/// (2) for each search since the entry was stored or last used.  (A probe
/// that finds an entry marks it as the current search's, so these tests
/// look only once they've stored all they mean to.)
/// </summary>
public class TranspositionTableTests
{
    /// <summary>Hash 1: 16,384 buckets, so a key's top 14 bits are its bucket.</summary>
    private static TranspositionTable Small()
    {
        var table = new TranspositionTable(1);
        Assert.Equal(16_384 * TranspositionTable.BucketSlots, table.Slots);
        return table;
    }

    private static ulong Key(int bucket, int which) => (ulong)bucket << 50 | (ulong)(which + 1) * 7919;

    private static void Put(TranspositionTable table, ulong key, int depth) =>
        table.Store(key, new Move(12, 28), 10 * depth, depth, Bound.Exact);

    private static void NewSearches(TranspositionTable table, int count)
    {
        for (int i = 0; i < count; i++)
            table.NewSearch();
    }

    private static int? DepthOf(TranspositionTable table, ulong key) =>
        table.TryGet(key, out var entry) ? entry.Depth : null;

    [Fact]
    public void EveryEntryInABucketComesBackAsStored()
    {
        var table = Small();
        NewSearches(table, 45);   // (the generation sits beside the bound and depth, and mustn't disturb them)
        var cases = new (ulong Key, Move Move, int Score, int Depth, Bound Bound)[]
        {
            (Key(7, 0), new Move(12, 28, PieceType.None, MoveFlags.DoublePawnPush), 35, 7, Bound.Exact),
            (Key(7, 1), new Move(52, 61, PieceType.Knight, MoveFlags.Capture), -29_990, 1, Bound.Upper),
            (Key(7, 2), new Move(4, 6, PieceType.None, MoveFlags.Castle), 29_995, 127, Bound.Lower),
            (Key(7, 3), default, -1, -1, Bound.Exact),
        };
        foreach (var c in cases)
            table.Store(c.Key, c.Move, c.Score, c.Depth, c.Bound);
        foreach (var c in cases)
        {
            Assert.True(table.TryGet(c.Key, out var entry));
            Assert.Equal((c.Key, c.Move, (short)c.Score, (sbyte)c.Depth, c.Bound),
                         (entry.Key, entry.Move, entry.Score, entry.Depth, entry.Bound));
        }
        Assert.False(table.TryGet(Key(7, 4), out _));
    }

    [Fact]
    public void AnEmptySlotIsNoEntryEvenForKeyZero()
    {
        var table = Small();
        Assert.False(table.TryGet(0, out _));   // (an empty slot's words are 0, and 0 XOR 0 is key 0)
        table.Store(0, default, -5, -1, Bound.Upper);
        Assert.True(table.TryGet(0, out var entry));
        Assert.Equal(((short)-5, (sbyte)-1, Bound.Upper), (entry.Score, entry.Depth, entry.Bound));
    }

    [Fact]
    public void EverySlotHoldsAnEntry()
    {
        var table = Small();
        var keys = Enumerable.Range(0, 250).Concat(Enumerable.Range(16_134, 250))
                             .SelectMany(bucket => Enumerable.Range(0, 4).Select(which => Key(bucket, which))).ToList();
        foreach (var key in keys)
            Put(table, key, 3);
        Assert.All(keys, key => Assert.Equal(3, DepthOf(table, key)));
    }

    [Theory]
    [InlineData(0, 65_536)]
    [InlineData(1, 65_536)]
    [InlineData(3, 196_608)]
    [InlineData(16, 1_048_576)]
    public void HashIsMegabytesAtSixteenBytesASlot(int megabytes, long slots)
    {
        Assert.Equal(slots, new TranspositionTable(megabytes).Slots);
        var table = new TranspositionTable(1);
        table.Resize(megabytes);
        Assert.Equal(slots, table.Slots);
    }

    [Fact]
    public void BucketsStartOnACacheLine()
    {
        // (Eight tables, so that one landing there by chance can't pass for the rule.)
        var tables = Enumerable.Range(1, 8).Select(megabytes => new TranspositionTable(megabytes)).ToList();
        Assert.All(tables, table => Assert.Equal(0, table.FirstBucketAddress % 64));
    }

    [Fact]
    public void DeepEntriesOutlastAFloodOfShallowOnes()
    {
        // Our own search leaves entries of depth 12, 4 and 2 in a bucket; then a ponder on the wrong reply (the
        // next search) stores hundreds of depth-1 entries there.  The 12 and the 4 (worth 10 and 2 now) outlast
        // them; the 2 (worth 0) goes, and the flood's entries push out each other.
        var table = Small();
        ulong deep = Key(3, 0), middle = Key(3, 1), shallow = Key(3, 2);
        Put(table, deep, 12);
        Put(table, middle, 4);
        Put(table, shallow, 2);
        table.NewSearch();
        for (int i = 3; i < 500; i++)
            Put(table, Key(3, i), 1);
        Assert.Equal(12, DepthOf(table, deep));
        Assert.Equal(4, DepthOf(table, middle));
        Assert.Null(DepthOf(table, shallow));
        Assert.Equal(1, DepthOf(table, Key(3, 499)));
    }

    [Fact]
    public void TheLastSearchsEntriesGoBeforeTheCurrentOnesOfTheSameDepth()
    {
        var table = Small();
        var old = Enumerable.Range(0, 4).Select(which => Key(9, which)).ToList();
        var current = Enumerable.Range(4, 4).Select(which => Key(9, which)).ToList();
        old.ForEach(key => Put(table, key, 5));
        table.NewSearch();
        current.ForEach(key => Put(table, key, 5));
        Assert.All(current, key => Assert.Equal(5, DepthOf(table, key)));
        Assert.All(old, key => Assert.Null(DepthOf(table, key)));
    }

    [Theory]
    [InlineData(7, false)]   // three searches old: worth 7 - 6 = 1, less than a current depth 2
    [InlineData(9, true)]    // worth 3: a current depth-2 entry goes instead
    public void AnOldEntryIsWorthItsDepthLessTwoForEachSearchSince(int depth, bool kept)
    {
        var table = Small();
        ulong old = Key(11, 0);
        Put(table, old, depth);
        NewSearches(table, 3);
        for (int which = 1; which <= 4; which++)
            Put(table, Key(11, which), 2);
        Assert.Equal(kept ? depth : (int?)null, DepthOf(table, old));
        Assert.Equal(2, DepthOf(table, Key(11, 4)));
    }

    [Fact]
    public void AgesCountOnRoundTheWrap()
    {
        // Stored in search 63, and now it's search 0: one search old, worth 5 - 2 = 3, less than a current 4.
        var table = Small();
        NewSearches(table, 63);
        ulong old = Key(13, 0);
        Put(table, old, 5);
        table.NewSearch();
        Assert.Equal(0, table.Generation);
        for (int which = 1; which <= 4; which++)
            Put(table, Key(13, which), 4);
        Assert.Null(DepthOf(table, old));
    }

    [Fact]
    public void AnEmptySlotIsTakenBeforeAnyEntry()
    {
        // Even an old shallow one: stored in search 62 and two searches old at search 0, so worth 1 - 4 = -3,
        // below what an empty slot (all zeros, "search 0") would be worth if it were scored as an entry.
        var table = Small();
        NewSearches(table, 62);
        ulong old = Key(15, 0);
        Put(table, old, 1);
        NewSearches(table, 2);
        Put(table, Key(15, 1), 1);
        Put(table, Key(15, 2), 1);
        Assert.Equal(1, DepthOf(table, old));
    }

    [Fact]
    public void AnEntryInUseIsAsGoodAsNew()
    {
        // Stored in search 1, found again in search 4: it counts as search 4's (worth 3, not 3 - 6), so the
        // bucket's depth-2 entries go first.
        var table = Small();
        table.NewSearch();
        ulong used = Key(17, 0);
        Put(table, used, 3);
        NewSearches(table, 3);
        Assert.True(table.TryGet(used, out _));
        for (int which = 1; which <= 4; which++)
            Put(table, Key(17, which), 2);
        Assert.Equal(3, DepthOf(table, used));
    }

    [Theory]
    [InlineData(0)]   // the bucket's first slot
    [InlineData(2)]   // a slot past one it would otherwise replace
    public void ThePositionAlreadyThereIsUpdatedInPlace(int slot)
    {
        var table = Small();
        var keys = Enumerable.Range(0, 4).Select(which => Key(19, which)).ToList();
        keys.ForEach(key => Put(table, key, 5));
        table.Store(keys[slot], default, 70, 7, Bound.Lower);
        Assert.True(table.TryGet(keys[slot], out var entry));
        Assert.Equal((7, Bound.Lower, new Move(12, 28)), (entry.Depth, entry.Bound, entry.Move));
        Assert.All(keys.Where((_, i) => i != slot), key => Assert.Equal(5, DepthOf(table, key)));
    }

    [Fact]
    public void HashfullCountsWhatTheCurrentSearchStoredOrUsed()
    {
        var table = Small();
        Assert.Equal(0, table.Usage());
        for (int bucket = 0; bucket < 125; bucket++)   // (hashfull samples the first 1,000 slots: 250 buckets)
            for (int which = 0; which < 4; which++)
                Put(table, Key(bucket, which), 3);
        Assert.Equal(500, table.Usage());
        table.NewSearch();
        Assert.Equal(0, table.Usage());
        Assert.True(table.TryGet(Key(0, 0), out _));
        Assert.Equal(1, table.Usage());
    }

    [Fact]
    public void TheSearchCountRunsRoundAtSixtyFourAndClearStartsItAgain()
    {
        var table = Small();
        NewSearches(table, 64);
        Assert.Equal(0, table.Generation);
        NewSearches(table, 5);
        Put(table, Key(21, 0), 3);
        Assert.Equal(5, table.Generation);
        table.Clear();
        Assert.Equal(0, table.Generation);
        Assert.False(table.TryGet(Key(21, 0), out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void EachSearchIsOneGenerationHelpersIncluded(int threads)
    {
        var table = new TranspositionTable(1);
        var search = new Search(table) { Threads = threads };
        search.Run(Position.Start(), new SearchLimits { Depth = 5 });
        Assert.Equal(1, table.Generation);
        Assert.InRange(table.Usage(), 1, 1000);
        search.Run(Position.Start(), new SearchLimits { Depth = 5 });   // (cut short by the first one's entries)
        Assert.Equal(2, table.Generation);
    }
}
