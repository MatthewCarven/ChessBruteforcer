using System.Text;
using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Tests;

public class TableIndexTests
{
    [Fact]
    public void TheKingsHaveTheKnownNumberOfPlacements()
    {
        // 462 is the standard figure for two kings under the board's 8 symmetries.
        Assert.Equal(462, new TableIndex(Material.Parse("KQvK")).KingPairs);
        // With pawns only the mirror: white king on files a-d (32 squares), black king
        // anywhere but on or next to it.  32 * 64 - 242 = 1,806.
        Assert.Equal(1806, new TableIndex(Material.Parse("KPvK")).KingPairs);
    }

    [Theory]
    [InlineData("KQvK", 462L * 64 * 2)]
    [InlineData("KQvKR", 462L * 64 * 64 * 2)]
    [InlineData("KPvK", 1806L * 64 * 2)]
    [InlineData("KPvKP", 1806L * 64 * 64 * 2)]
    public void TablesShrinkBySymmetry(string material, long size)
    {
        Assert.Equal(size, EndgameTable.TableSize(Material.Parse(material)));
    }

    [Theory]
    [InlineData("KQvKR")]
    [InlineData("KBNvK")]
    [InlineData("KRRvK")]   // two identical pieces
    public void EveryImageOfAPositionWithoutPawnsGetsOneIndex(string material)
    {
        var index = new TableIndex(Material.Parse(material));
        var random = new Random(5);
        var squares = new int[index.Slots.Length];
        var image = new int[squares.Length];
        for (int trial = 0; trial < 2000; trial++)
        {
            RandomPlacement(random, squares);
            var side = (Colour)random.Next(2);
            long expected = index.Encode(squares, side);
            if (expected < 0)
                continue;
            for (int t = 0; t < index.Symmetries; t++)
            {
                for (int slot = 0; slot < squares.Length; slot++)
                    image[slot] = index.Map(t, squares[slot]);
                Assert.Equal(expected, index.Encode(image, side));
            }
        }
        Assert.Equal(8, index.Symmetries);
    }

    [Fact]
    public void WithPawnsOnlyTheMirrorIsUsed()
    {
        var index = new TableIndex(Material.Parse("KPvKP"));
        Assert.Equal(2, index.Symmetries);
        // e5 pawn and d7 pawn, kings e1 / e8, against its mirror image: d5, e7, d1 / d8.
        var squares = new[] { 4, 60, 36, 51 };
        var mirror = new[] { 3, 59, 35, 52 };
        Assert.Equal(index.Encode(squares, Colour.White), index.Encode(mirror, Colour.White));
        // Upside down is a different position (the pawns would run the other way).
        var flipped = squares.Select(s => s ^ 56).ToArray();
        Assert.NotEqual(index.Encode(squares, Colour.White), index.Encode(flipped, Colour.White));
    }

    [Theory]
    [InlineData("KvK")]
    [InlineData("KQvK")]
    [InlineData("KPvK")]
    public void EveryPlacementIsCountedExactlyOnce(string material)
    {
        // Decoding every index and weighting it by its images gives back the plain
        // count: kings apart and not touching, no two pieces on a square, no pawn
        // on the first or last rank.
        var index = new TableIndex(Material.Parse(material));
        var squares = new int[index.Slots.Length];
        long weighted = 0;
        for (long i = 0; i < index.Size; i++)
        {
            if (!index.Decode(i, squares, out var side))
                continue;
            Assert.Equal(i, index.Encode(squares, side));
            weighted += index.Weight(squares);
        }

        long kings = 64 * 63 - 420;                                 // 3,612 per side to move
        long expected = material switch
        {
            "KvK" => kings,
            "KQvK" => kings * 62,
            _ => KingPawnPlacements(),
        };
        Assert.Equal(expected * 2, weighted);
    }

    [Fact]
    public void TablesInTheFirstFormatStillLoad()
    {
        // Write K+Q v K the old way (every placement of every piece, no symmetry)
        // and check it reads back as the table it was made from.
        var tablebase = new Tablebase();
        var table = tablebase.Get(Material.Parse("KQvK"));
        var index = new TableIndex(table.Material);
        var legacy = new short[64L * 64 * 64 * 2];
        var squares = new int[3];
        for (long raw = 0; raw < legacy.LongLength; raw++)
        {
            squares[0] = (int)(raw >> 13);
            squares[1] = (int)(raw >> 7) & 63;
            squares[2] = (int)(raw >> 1) & 63;
            long i = squares.Distinct().Count() == 3 ? index.Encode(squares, (Colour)(raw & 1)) : -1;
            var value = i < 0 ? null : table[i];
            legacy[raw] = value switch
            {
                null => short.MinValue,
                { Kind: OutcomeKind.Win } v => (short)v.Plies,
                { Kind: OutcomeKind.Loss } v => (short)(-v.Plies - 1),
                _ => 0,
            };
        }

        string path = Path.Combine(Path.GetTempPath(), $"kqvk-legacy-{Guid.NewGuid():N}.cbt");
        try
        {
            using (var writer = new BinaryWriter(File.Create(path), Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("CBT1"));
                writer.Write("KQvK");
                writer.Write(legacy.LongLength);
                foreach (short value in legacy)
                    writer.Write(value);
            }
            Assert.True(EndgameTable.IsLegacyFile(path));
            using var loaded = EndgameTable.Load(path);
            Assert.Equal(table.Size, loaded.Size);
            for (long i = 0; i < table.Size; i++)
                Assert.Equal(table[i], loaded[i]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>King, king and a white pawn: kings apart, pawn on ranks 2-7 and on neither king.</summary>
    private static long KingPawnPlacements()
    {
        long count = 0;
        for (int wk = 0; wk < 64; wk++)
            for (int bk = 0; bk < 64; bk++)
            {
                if (wk == bk || Attacks.King[wk].Contains(bk))
                    continue;
                for (int pawn = 8; pawn < 56; pawn++)
                    if (pawn != wk && pawn != bk)
                        count++;
            }
        return count;
    }

    private static void RandomPlacement(Random random, int[] squares)
    {
        for (int slot = 0; slot < squares.Length; slot++)
        {
            int square;
            do square = random.Next(64);
            while (Array.IndexOf(squares, square, 0, slot) >= 0);
            squares[slot] = square;
        }
    }
}
