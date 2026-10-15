using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Tests;

/// <summary>
/// Small tables solved once (mate distances and the 50-move rule), and a
/// folder holding every one of them compressed.
/// </summary>
public sealed class CompressedTables : IDisposable
{
    public Tablebase Plain { get; } = new();

    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("cbc-tables").FullName;

    public CompressedTables()
    {
        // K+P v K promotes into K+Q, K+R, K+B, K+N v K; K+R+R v K has two identical pieces.
        foreach (string text in new[] { "KRvK", "KPvK", "KRRvK" })
            Plain.Get(Material.Parse(text));
        foreach (var material in Plain.Tables.Select(t => t.Material).ToList())
        {
            Plain.Get(material).SaveCompressed(Path.Combine(Directory, material + ".cbt"));
            Plain.GetDtz(material).SaveCompressed(Path.Combine(Directory, material + ".cbz"));
        }
    }

    public string PathOf(string material, bool dtz) => Path.Combine(Directory, material + (dtz ? ".cbz" : ".cbt"));

    public EndgameTable PlainTable(string material, bool dtz) =>
        dtz ? Plain.GetDtz(Material.Parse(material)) : Plain.Get(Material.Parse(material));

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
}

public class CompressedTableTests : IClassFixture<CompressedTables>
{
    private readonly CompressedTables _tables;

    public CompressedTableTests(CompressedTables tables) => _tables = tables;

    [Theory]
    [InlineData("KRvK", false)]
    [InlineData("KRvK", true)]
    [InlineData("KPvK", false)]    // pawns: the left-right mirror only, a bigger table
    [InlineData("KPvK", true)]
    [InlineData("KRRvK", false)]   // identical pieces, stored once
    [InlineData("KRRvK", true)]
    public void EveryValueComesBackFromACompressedTable(string material, bool dtz)
    {
        var plain = _tables.PlainTable(material, dtz);
        string path = _tables.PathOf(material, dtz);
        Assert.True(EndgameTable.IsCompressedFile(path));
        Assert.False(EndgameTable.IsOutdatedFile(path));   // (nothing for an upgrade to do)
        Assert.True(new FileInfo(path).Length < plain.Size * (dtz ? 1 : 2));   // (tables this small gain less)

        using var loaded = EndgameTable.Load(path);
        Assert.True(loaded.IsCompressed);
        Assert.Equal(dtz, loaded.IsDtz);
        Assert.Null(loaded.Cap);
        Assert.Equal(plain.Material, loaded.Material);
        Assert.Equal(plain.Size, loaded.Size);
        for (long i = 0; i < plain.Size; i++)
            Assert.Equal(plain[i], loaded[i]);

        using var inMemory = EndgameTable.Load(path, intoMemory: true);
        Assert.False(inMemory.IsCompressed);
        Assert.Equal(dtz, inMemory.IsDtz);
        for (long i = 0; i < plain.Size; i++)
            Assert.Equal(plain[i], inMemory[i]);
    }

    [Fact]
    public void BlocksPushedOutOfTheCacheComeBackTheSame()
    {
        // Two cache places, and values read in a scattered order, so blocks keep displacing
        // each other (nearly every read decompresses a block: ~80 us, so 20,000 of them).
        var plain = _tables.PlainTable("KRRvK", dtz: false);
        using var compressed = CompressedTable.Open(_tables.PathOf("KRRvK", dtz: false), new BlockCache(blocks: 2, ways: 2));
        Assert.True(compressed.Size > 20 * 32768);   // many blocks of 32K values
        long step = 1_000_003 % compressed.Size;
        long index = 0;
        for (int n = 0; n < 20_000; n++)
        {
            Assert.Equal(plain[index], Decode(compressed[index]));
            index = (index + step) % compressed.Size;
        }
    }

    [Fact]
    public void ManyThreadsReadingOneTableAllGetItsValues()
    {
        // 8 threads on 3 cache places: each reads a scattered sequence of its own, half the time
        // from a few hot blocks, so reads of a block already there race with other threads
        // filling the same places, and two threads often want one block at once.
        var plain = _tables.PlainTable("KRRvK", dtz: false);
        using var compressed = CompressedTable.Open(_tables.PathOf("KRRvK", dtz: false), new BlockCache(blocks: 3, ways: 3));
        const int Threads = 8, Reads = 3000;
        var wrong = new int[Threads];
        var workers = Enumerable.Range(0, Threads).Select(t => Task.Factory.StartNew(() =>
        {
            var random = new Random(t);
            for (int n = 0; n < Reads; n++)
            {
                long index = n % 2 == 0 ? random.Next(3 * 32768) : random.NextInt64(compressed.Size);
                if (Decode(compressed[index]) != plain[index])
                    wrong[t]++;
            }
        }, TaskCreationOptions.LongRunning)).ToArray();
        Assert.True(Task.WaitAll(workers, TimeSpan.FromMinutes(2)), "the readers didn't finish");
        Assert.Equal(new int[Threads], wrong);
        var counts = compressed.Counts;
        Assert.True(counts.Decompressed > Reads, $"{counts.Decompressed} blocks decompressed");   // (it was a test of the places)
        Assert.True(counts.Waits > 0, "no read ever waited for a place being filled");
    }

    /// <summary>Tables sharing a cache: the KRRvK and KPvK files, both kinds, read at random through 12 places in 3 sets of 4.</summary>
    private (EndgameTable[] Tables, EndgameTable[] Plain) Sharing(BlockCache cache)
    {
        var names = new[] { ("KRRvK", false), ("KRRvK", true), ("KPvK", false), ("KPvK", true) };
        return (names.Select(n => EndgameTable.Load(_tables.PathOf(n.Item1, n.Item2), cache: cache)).ToArray(),
                names.Select(n => _tables.PlainTable(n.Item1, n.Item2)).ToArray());
    }

    [Fact]
    public void TablesSharingACacheStayWithinItsBudget()
    {
        var cache = new BlockCache(blocks: 12, ways: 4);
        Assert.Equal(12, cache.Blocks);
        var (tables, plain) = Sharing(cache);
        try
        {
            var random = new Random(5);
            for (int n = 0; n < 20_000; n++)
            {
                int t = random.Next(tables.Length);
                long index = random.NextInt64(tables[t].Size);
                Assert.Equal(plain[t][index], tables[t][index]);
            }
            Assert.Equal(12L * BlockCache.BlockBytes, cache.BytesHeld);   // every place used, and no more
            Assert.All(tables, table => Assert.True(table.BlockCacheCounts.Decompressed > 100));
            cache.Clear();
            Assert.Equal(0L, cache.BytesHeld);
            Assert.Equal(plain[0][12345], tables[0][12345]);
        }
        finally
        {
            foreach (var table in tables)
                table.Dispose();
        }
    }

    [Fact]
    public void ManyThreadsReadingTablesThatShareACacheAllGetTheirValues()
    {
        // 8 threads, 4 tables, 6 places in 2 sets of 3: places are claimed, filled and taken
        // back from each other all the time, and two threads often want one block at once.
        var cache = new BlockCache(blocks: 6, ways: 3);
        var (tables, plain) = Sharing(cache);
        try
        {
            const int Threads = 8, Reads = 3000;
            var wrong = new int[Threads];
            var workers = Enumerable.Range(0, Threads).Select(t => Task.Factory.StartNew(() =>
            {
                var random = new Random(t);
                for (int n = 0; n < Reads; n++)
                {
                    int which = random.Next(tables.Length);
                    long index = n % 2 == 0 ? random.Next(2 * 32768) : random.NextInt64(tables[which].Size);
                    if (tables[which][index] != plain[which][index])
                        wrong[t]++;
                }
            }, TaskCreationOptions.LongRunning)).ToArray();
            Assert.True(Task.WaitAll(workers, TimeSpan.FromMinutes(2)), "the readers didn't finish");
            Assert.Equal(new int[Threads], wrong);
            var counts = tables.Aggregate(default(BlockCacheCounts), (sum, table) => sum + table.BlockCacheCounts);
            Assert.True(counts.Decompressed > Reads, $"{counts.Decompressed} blocks decompressed");
            Assert.True(counts.SameBlockWaits > 0, "no read ever waited for its block to be filled by another thread");
            Assert.True(cache.BytesHeld <= 6L * BlockCache.BlockBytes);
        }
        finally
        {
            foreach (var table in tables)
                table.Dispose();
        }
    }

    [Fact]
    public void AMateInTimeHasTheSameResultUnderTheRule()
    {
        // Why TryProbeWithClock needn't read the DTZ table for a mate that comes before the count
        // reaches 100: in every position of every table here, the DTZ table agrees (same win or
        // loss) and its distance to the next capture, pawn move or mate is no longer.
        int decisive = 0;
        foreach (var mates in _tables.Plain.Tables.ToList())
        {
            var rule = _tables.Plain.GetDtz(mates.Material);
            for (long i = 0; i < mates.Size; i++)
            {
                if (mates[i] is not { } mate || mate.Kind == OutcomeKind.Draw || mate.Plies > EndgameTable.RulePlies)
                    continue;
                var dtz = rule[i]!.Value;
                if (dtz.Kind != mate.Kind || dtz.Plies > mate.Plies)
                    Assert.Fail($"{mates.PositionAt(i).ToFen()}: mate distance {mate}, under the rule {dtz}");
                decisive++;
            }
        }
        Assert.True(decisive > 100_000, $"{decisive} positions");
    }

    [Fact]
    public void ProbesWithTheClockMatchTheirDefinition()
    {
        // Through the compressed tables, at clocks either side of where the mate and the DTZ
        // distance run out: the mate distance, made a draw when the DTZ result comes too late.
        using var tablebase = new Tablebase(_tables.Directory, blockCache: new BlockCache(blocks: 16, ways: 4)) { SolveMissing = false };
        int checkedProbes = 0;
        foreach (string text in new[] { "KRRvK", "KPvK" })
        {
            var plain = _tables.Plain.Get(Material.Parse(text));
            for (long i = 0; i < plain.Size; i += 37)
            {
                if (plain[i] is null)
                    continue;
                var position = plain.PositionAt(i);
                var mate = _tables.Plain.Probe(position);
                var dtz = _tables.Plain.ProbeDtz(position);
                int[] clocks = { 0, 50, 99, 100, 99 - mate.Plies, 100 - mate.Plies, 101 - mate.Plies, 100 - dtz.Plies, 101 - dtz.Plies };
                foreach (int clock in clocks.Where(c => c is >= 0 and <= 100).Distinct())
                {
                    var fields = position.ToFen().Split(' ');
                    fields[4] = clock.ToString();
                    var expected = mate.Kind != OutcomeKind.Draw && !Tablebase.DecisiveInTime(dtz, clock) ? Outcome.Draw : mate;
                    Assert.True(tablebase.TryProbeWithClock(Position.FromFen(string.Join(' ', fields)), out var outcome));
                    Assert.Equal(expected, outcome);
                    checkedProbes++;
                }
            }
        }
        Assert.True(checkedProbes > 50_000, $"{checkedProbes} probes");
    }

    [Fact]
    public void TheEngineSizesTheCacheFromItsOption()
    {
        var output = new StringWriter();
        var engine = new UciEngine(output);
        engine.Handle("uci");
        Assert.Contains($"option name TableCache type spin default {BlockCache.DefaultMegabytes} min 1 max 65536", output.ToString());
        engine.Handle("setoption name TableCache value 3");
        engine.Handle($"setoption name EndgameTables value {_tables.Directory}");
        engine.Handle("isready");
        Assert.Contains("cached in 3 MB at most", output.ToString());
        engine.Handle("setoption name TableCache value 2");
        Assert.Contains("info string endgame table cache 2 MB", output.ToString());
        engine.Handle("setoption name EndgameTables value <empty>");   // (lets go of the files, so the folder can be deleted)
        Assert.Contains("info string endgame tables off", output.ToString());
    }

    /// <summary>A stored value as a complete table means it (see EndgameTable.DecodeRaw).</summary>
    private static Outcome? Decode(short value) => value switch
    {
        short.MinValue => null,
        short.MaxValue or 0 => Outcome.Draw,
        > 0 => Outcome.Win(value),
        _ => Outcome.Loss(-value - 1),
    };

    [Fact]
    public void ProbesThroughCompressedTablesMatchThePlainOnes()
    {
        using var tablebase = new Tablebase(_tables.Directory) { SolveMissing = false };
        foreach (string text in new[] { "KRRvK", "KPvK" })
        {
            var plain = _tables.Plain.Get(Material.Parse(text));
            for (long i = 0; i < plain.Size; i += 101)
            {
                if (plain[i] is null)
                    continue;
                var position = plain.PositionAt(i);
                Assert.Equal(_tables.Plain.Probe(position), tablebase.Probe(position));
                Assert.Equal(_tables.Plain.ProbeDtz(position), tablebase.ProbeDtz(position));
                if (i % 10_000 < 101)   // every move, into the smaller tables too
                {
                    Assert.Equal(_tables.Plain.RankMoves(position), tablebase.RankMoves(position));
                    Assert.Equal(_tables.Plain.RankMovesUnderRule(position), tablebase.RankMovesUnderRule(position));
                }
            }
        }
        Assert.All(tablebase.Tables, table => Assert.True(table.IsCompressed));
    }

    [Theory]
    [InlineData("KRRvK", false)]   // captures into K+R v K, compressed
    [InlineData("KPvK", true)]     // promotions into K+Q, K+R, K+B, K+N v K, compressed
    public void ATableSolvedFromCompressedSmallerTablesIsTheSame(string text, bool dtz)
    {
        string dir = System.IO.Directory.CreateTempSubdirectory("cbc-solve").FullName;
        try
        {
            foreach (string file in System.IO.Directory.EnumerateFiles(_tables.Directory))
                if (!Path.GetFileName(file).StartsWith(text + ".", StringComparison.Ordinal))
                    File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
            var material = Material.Parse(text);
            using var tablebase = new Tablebase(dir);
            var solved = dtz ? tablebase.GetDtz(material) : tablebase.Get(material);
            Assert.False(solved.IsCompressed);
            var plain = _tables.PlainTable(text, dtz);
            for (long i = 0; i < plain.Size; i++)
                Assert.Equal(plain[i], solved[i]);
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void OnlyCompleteTablesAreCompressed()
    {
        var capped = EndgameTable.Solve(Material.Parse("KRvK"), _tables.Plain.Probe, cap: 5);
        Assert.Equal(5, capped.Cap);
        string path = Path.Combine(Path.GetTempPath(), $"krvk-capped-{Guid.NewGuid():N}.cbt");
        Assert.Throws<InvalidOperationException>(() => capped.SaveCompressed(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ACompressedFileCutShortIsRefused()
    {
        string path = Path.Combine(Path.GetTempPath(), $"krrvk-cut-{Guid.NewGuid():N}.cbt");
        try
        {
            var bytes = File.ReadAllBytes(_tables.PathOf("KRRvK", dtz: false));
            File.WriteAllBytes(path, bytes[..^100]);
            Assert.Throws<InvalidDataException>(() => EndgameTable.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PreloadKeepsCompressedTablesCompressedAndPlainOnesWithinItsMemory()
    {
        string dir = System.IO.Directory.CreateTempSubdirectory("cbc-preload").FullName;
        try
        {
            // Compressed: K+R+R v K.  Plain: K v K and K+R v K (small), K+P v K (the biggest plain one).
            File.Copy(_tables.PathOf("KRRvK", dtz: false), Path.Combine(dir, "KRRvK.cbt"));
            foreach (string text in new[] { "KvK", "KRvK", "KPvK" })
                _tables.Plain.Get(Material.Parse(text)).Save(Path.Combine(dir, text + ".cbt"));
            long small = new[] { "KvK", "KRvK" }.Sum(text => _tables.Plain.Get(Material.Parse(text)).Size * sizeof(short));

            using var tablebase = new Tablebase(dir) { SolveMissing = false, LoadOnDemand = false };
            var (count, bytes, onDisk) = tablebase.Preload(memoryBytes: small + 1000);
            Assert.Equal(4, count);
            Assert.Equal(small, bytes);
            Assert.Equal(2, onDisk);
            var byName = tablebase.Tables.ToDictionary(t => t.Material.ToString());
            Assert.True(byName["KRRvK"].IsCompressed);
            Assert.Null(byName["KvK"].FilePath);             // in memory
            Assert.Null(byName["KRvK"].FilePath);
            Assert.NotNull(byName["KPvK"].FilePath);         // past the memory given: read from its file
            Assert.False(byName["KPvK"].IsCompressed);

            var position = Position.FromFen("8/8/8/8/8/2k5/8/R4RK1 w - - 0 1");   // K+R+R v K, from the compressed file
            Assert.True(tablebase.TryProbe(position, out var outcome));
            Assert.Equal(_tables.Plain.Probe(position), outcome);
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }
}
