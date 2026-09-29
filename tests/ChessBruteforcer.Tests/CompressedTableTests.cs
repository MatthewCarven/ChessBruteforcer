using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Endgame;
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
        // Two cache slots, and values read in a scattered order, so blocks keep displacing
        // each other (nearly every read decompresses a block: ~80 us, so 20,000 of them).
        var plain = _tables.PlainTable("KRRvK", dtz: false);
        using var compressed = CompressedTable.Open(_tables.PathOf("KRRvK", dtz: false), cacheSlots: 2);
        Assert.True(compressed.Size > 20 * 32768);   // many blocks of 32K values
        long step = 1_000_003 % compressed.Size;
        long index = 0;
        for (int n = 0; n < 20_000; n++)
        {
            Assert.Equal(plain[index], Decode(compressed[index]));
            index = (index + step) % compressed.Size;
        }
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
