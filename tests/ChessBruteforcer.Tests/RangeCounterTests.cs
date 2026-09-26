using System.Numerics;
using ChessBruteforcer.Core;

namespace ChessBruteforcer.Tests;

/// <summary>
/// The counter never enumerates, so it is checked against two things that do
/// it the slow way: brute force over every board of a tiny geometry, and a
/// square-by-square dynamic programme on a board too big to brute-force.
/// </summary>
public class RangeCounterTests
{
    // Small armies so that the promotion rules actually bite on small boards.
    private static readonly MaterialRules Small = new()
    {
        Pawns = 1, Queens = 1, Rooks = 1, Knights = 0, LightBishops = 0, DarkBishops = 1,
    };

    private static readonly MaterialRules SmallNoPromotions = Small with { AllowPromotions = false };

    public static IEnumerable<object[]> BruteForceCases() => new[]
    {
        new object[] { 2, 3, Small },
        new object[] { 2, 3, SmallNoPromotions },
        new object[] { 3, 2, Small },            // every square is a back rank: no pawns at all
        new object[] { 2, 3, MaterialRules.Standard },
    };

    [Theory]
    [MemberData(nameof(BruteForceCases))]
    public void EveryTierMatchesBruteForce(int files, int ranks, MaterialRules rules)
    {
        var geometry = new BoardGeometry(files, ranks);
        var checker = new BoardChecker(rules, geometry);
        var counter = new RangeCounter(geometry, rules);

        var reached = new long[Tiers.Material + 1];
        var pieces = Piece.All.Prepend(Piece.Empty).ToArray();
        var squares = new Piece[geometry.SquareCount];
        var digits = new int[geometry.SquareCount];
        while (true)
        {
            for (int i = 0; i < squares.Length; i++)
                squares[i] = pieces[digits[i]];
            reached[checker.Check(squares).HighestTierPassed]++;

            int k = 0;
            while (k < digits.Length && ++digits[k] == pieces.Length)
                digits[k++] = 0;
            if (k == digits.Length)
                break;
        }

        for (int tier = Tiers.ValidCodes; tier <= Tiers.Material; tier++)
        {
            long survivors = reached.Skip(tier).Sum();
            Assert.Equal((BigInteger)survivors, counter.BoardsAtTier(tier));
        }
    }

    [Theory]
    [InlineData(4, 4, true)]
    [InlineData(4, 4, false)]
    [InlineData(3, 5, true)]
    public void MaterialTierMatchesSquareBySquareCount(int files, int ranks, bool promotions)
    {
        var rules = new MaterialRules
        {
            Pawns = 2, Queens = 1, Rooks = 1, Knights = 1, LightBishops = 1, DarkBishops = 1,
            AllowPromotions = promotions,
        };
        var geometry = new BoardGeometry(files, ranks);
        Assert.Equal(SquareBySquare(geometry, rules), new RangeCounter(geometry, rules).MaterialBoards());
    }

    [Fact]
    public void StandardClosedFormsMatchHandArithmetic()
    {
        var counter = RangeCounter.Standard;
        Assert.Equal(BigInteger.Pow(2, 384), counter.RawBoards());
        Assert.Equal(BigInteger.Pow(13, 64), counter.ValidCodeBoards());
        Assert.Equal(64 * 63 * BigInteger.Pow(11, 62), counter.KingBoards());
        BigInteger pawnRanks = 16 * 15 * BigInteger.Pow(9, 14) * BigInteger.Pow(11, 48)
                               + 2 * 16 * 48 * BigInteger.Pow(9, 15) * BigInteger.Pow(11, 47)
                               + 48 * 47 * BigInteger.Pow(9, 16) * BigInteger.Pow(11, 46);
        Assert.Equal(pawnRanks, counter.PawnRankBoards());
    }

    [Fact]
    public void StandardTiersShrinkMonotonically()
    {
        var counter = RangeCounter.Standard;
        for (int tier = Tiers.ValidCodes; tier <= Tiers.Material; tier++)
            Assert.True(counter.BoardsAtTier(tier) < counter.BoardsAtTier(tier - 1));

        var noPromotions = new RangeCounter(BoardGeometry.Standard, MaterialRules.StandardNoPromotions);
        Assert.True(noPromotions.MaterialBoards() < counter.MaterialBoards());
    }

    /// <summary>
    /// Independent count: walk the squares, tracking every side's running
    /// piece counts, pruning as soon as a side can no longer be explained
    /// (pawn counts only grow, so a side's promotion budget only shrinks).
    /// </summary>
    private static BigInteger SquareBySquare(BoardGeometry geometry, MaterialRules rules)
    {
        var states = new Dictionary<(SideMaterial W, SideMaterial B), BigInteger>
        {
            [(new SideMaterial(), new SideMaterial())] = 1,
        };
        for (int square = 0; square < geometry.SquareCount; square++)
        {
            var next = new Dictionary<(SideMaterial W, SideMaterial B), BigInteger>();
            foreach (var ((white, black), ways) in states)
            {
                Add(next, (white, black), ways);
                foreach (var piece in Piece.All)
                {
                    if (piece.Type == PieceType.Pawn && geometry.IsBackRank(square))
                        continue;
                    var side = piece.Colour == Colour.White ? white : black;
                    switch (piece.Type)
                    {
                        case PieceType.Pawn: side.Pawns++; break;
                        case PieceType.Knight: side.Knights++; break;
                        case PieceType.Bishop:
                            if (geometry.IsLight(square)) side.LightBishops++;
                            else side.DarkBishops++;
                            break;
                        case PieceType.Rook: side.Rooks++; break;
                        case PieceType.Queen: side.Queens++; break;
                        case PieceType.King: side.Kings++; break;
                    }
                    if (side.Kings > rules.Kings || side.Pawns > rules.Pawns
                        || side.PromotionsNeeded(rules) > side.PromotionsAvailable(rules))
                        continue;
                    Add(next, piece.Colour == Colour.White ? (side, black) : (white, side), ways);
                }
            }
            states = next;
        }
        return states
            .Where(s => s.Key.W.Kings == rules.Kings && s.Key.B.Kings == rules.Kings)
            .Aggregate(BigInteger.Zero, (total, s) => total + s.Value);
    }

    private static void Add(Dictionary<(SideMaterial, SideMaterial), BigInteger> map,
                            (SideMaterial, SideMaterial) key, BigInteger ways) =>
        map[key] = map.GetValueOrDefault(key) + ways;
}
