using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Tests;

/// <summary>Solving the small tables once, shared by every test in the class.</summary>
public sealed class SolvedTables
{
    public Tablebase Tablebase { get; } = new();

    public SolvedTables()
    {
        Tablebase.Get(Material.Parse("KQvK"));
        Tablebase.Get(Material.Parse("KRvK"));
        Tablebase.Get(Material.Parse("KPvK"));
    }
}

public class EndgameTests : IClassFixture<SolvedTables>
{
    private readonly Tablebase _tablebase;

    public EndgameTests(SolvedTables tables) => _tablebase = tables.Tablebase;

    [Theory]
    [InlineData("KQvK", 19)]   // mate in 10
    [InlineData("KRvK", 31)]   // mate in 16
    public void LongestMatesMatchTheKnownValues(string material, int plies)
    {
        var stats = _tablebase.Get(Material.Parse(material)).Statistics();
        Assert.Equal(Outcome.Win(plies), stats.Longest[(int)Colour.White]!.Value.Outcome);
        Assert.Equal(Outcome.Loss(plies + 1), stats.Longest[(int)Colour.Black]!.Value.Outcome);
        Assert.Equal(0, stats.Draws[(int)Colour.White]);   // with the extra piece to move, always a win
        Assert.Equal(0, stats.Wins[(int)Colour.Black]);
    }

    [Fact]
    public void KingVersusKingIsAlwaysADraw()
    {
        var stats = _tablebase.Get(Material.Parse("KvK")).Statistics();
        foreach (var side in new[] { Colour.White, Colour.Black })
        {
            Assert.Equal(3612, stats.Legal(side));   // 64 x 63 minus the 420 adjacent placements
            Assert.Equal(3612, stats.Draws[(int)side]);
        }
    }

    /// <summary>
    /// The defining equation of a solved table, checked at every legal
    /// position: no moves means mate (loss in 0) or stalemate (draw),
    /// otherwise the value is exactly the best outcome among the moves.
    /// Together with the mates this pins every value down uniquely.
    /// </summary>
    [Theory]
    [InlineData("KQvK")]
    [InlineData("KRvK")]
    [InlineData("KPvK")]
    public void EveryPositionEqualsTheBestOfItsMoves(string material)
    {
        var table = _tablebase.Get(Material.Parse(material));
        long checkedPositions = 0;
        for (long index = 0; index < table.Size; index++)
        {
            var stored = table[index];
            if (stored is null)
                continue;
            var position = table.PositionAt(index);
            var ranked = _tablebase.RankMoves(position);
            var expected = ranked.Count > 0
                ? ranked[0].Outcome
                : position.InCheck() ? Outcome.Loss(0) : Outcome.Draw;
            if (expected != stored.Value)
                Assert.Fail($"{position.ToFen()}: stored {stored}, moves give {expected}");
            checkedPositions++;
        }
        Assert.True(checkedPositions > 300_000);
    }

    [Theory]
    [InlineData("7k/5Q2/6K1/8/8/8/8/8 b - - 0 1", OutcomeKind.Draw, 0)]      // stalemate
    [InlineData("7k/6Q1/6K1/8/8/8/8/8 b - - 0 1", OutcomeKind.Loss, 0)]      // checkmate, queen defended
    [InlineData("6Qk/8/6K1/8/8/8/8/8 b - - 0 1", OutcomeKind.Draw, 0)]       // Kxg8: the queen was loose
    [InlineData("7k/8/6K1/8/8/8/8/1Q6 w - - 0 1", OutcomeKind.Win, 1)]       // Qb8 mates
    [InlineData("8/8/8/8/8/8/1q6/K4k2 w - - 0 1", OutcomeKind.Draw, 0)]      // Kxb2
    [InlineData("8/8/8/8/8/8/1q6/K1k5 w - - 0 1", OutcomeKind.Loss, 0)]      // queen defended: mate
    public void KnownPositionsProbeCorrectly(string fen, OutcomeKind kind, int plies)
    {
        Assert.Equal(new Outcome(kind, plies), _tablebase.Probe(Position.FromFen(fen)));
    }

    /// <summary>Textbook king-and-pawn results (only the verdict; the distances come from the solver).</summary>
    [Theory]
    [InlineData("7k/8/8/8/P7/8/8/7K w - - 0 1", OutcomeKind.Win)]     // black king outside the pawn's square
    [InlineData("8/8/8/2k5/P7/8/8/7K b - - 0 1", OutcomeKind.Draw)]   // inside it: Kb4 wins the pawn
    [InlineData("k7/8/1K6/P7/8/8/8/8 w - - 0 1", OutcomeKind.Draw)]   // rook pawn, defender in the corner
    [InlineData("k7/8/1K6/P7/8/8/8/8 b - - 0 1", OutcomeKind.Draw)]
    [InlineData("4k3/8/4K3/4P3/8/8/8/8 w - - 0 1", OutcomeKind.Win)]  // king on the 6th in front of its pawn
    [InlineData("4k3/8/4K3/4P3/8/8/8/8 b - - 0 1", OutcomeKind.Loss)] //   wins whoever is to move
    [InlineData("8/4P3/8/8/8/8/k7/4K3 w - - 0 1", OutcomeKind.Win)]   // promotes, then K+Q v K
    [InlineData("8/8/8/8/4k3/8/4p3/K7 w - - 0 1", OutcomeKind.Loss)]  // black's pawn: found by swapping colours
    public void KingAndPawnTheory(string fen, OutcomeKind kind)
    {
        Assert.Equal(kind, _tablebase.Probe(Position.FromFen(fen)).Kind);
    }

    [Fact]
    public void TheLoneKingNeverWins()
    {
        var stats = _tablebase.Get(Material.Parse("KPvK")).Statistics();
        Assert.Equal(0, stats.Wins[(int)Colour.Black]);
        Assert.Equal(0, stats.Losses[(int)Colour.White]);
        Assert.True(stats.Draws[(int)Colour.White] > 0);   // unlike K+Q v K, plenty of draws
    }

    [Fact]
    public void PawnsOnBothSidesAreRefusedRatherThanGuessed()
    {
        Assert.Throws<NotSupportedException>(() => new Tablebase().Get(Material.Parse("KPvKP")));
    }

    [Theory]
    [InlineData("4k3/8/4K3/4P3/8/8/8/8 w - - 0 1")]
    [InlineData("8/8/8/5k2/8/8/1q6/7K b - - 0 1")]
    public void SwappingColoursKeepsTheResult(string fen)
    {
        var position = Position.FromFen(fen);
        Assert.Equal(_tablebase.Probe(position), _tablebase.Probe(Tablebase.SwapColours(position)));
    }

    [Fact]
    public void SwappingColoursSwapsNothingButWhoWins()
    {
        // Black with the queen: looked up in the KQvK table with colours swapped.
        var position = Position.FromFen("8/8/8/5k2/8/8/1q6/7K b - - 0 1");
        var swapped = Tablebase.SwapColours(position);
        Assert.Equal(_tablebase.Probe(swapped), _tablebase.Probe(position));
        Assert.Equal(OutcomeKind.Win, _tablebase.Probe(position).Kind);
    }

    [Fact]
    public void MovesAreRankedBestFirst()
    {
        var ranked = _tablebase.RankMoves(Position.FromFen("8/8/8/4k3/8/8/8/4K2Q w - - 0 1"));
        Assert.Equal(Outcome.Win(13), ranked[0].Outcome);
        for (int i = 1; i < ranked.Count; i++)
            Assert.True(ranked[i - 1].Outcome.Score >= ranked[i].Outcome.Score);
    }

    [Fact]
    public void ThePrincipalLineMatesInExactlyTheStoredDistance()
    {
        var position = Position.FromFen("8/8/8/8/8/2k5/1R6/K7 w - - 0 1");
        var outcome = _tablebase.Probe(position);
        var line = _tablebase.PrincipalLine(position);
        Assert.Equal(outcome.Plies, line.Count);
        foreach (var move in line)
            position.MakeMove(move);
        Assert.Equal(GameStatus.Checkmate, position.Status());
    }

    [Fact]
    public void TablesSurviveASaveAndLoad()
    {
        string path = Path.Combine(Path.GetTempPath(), $"krvk-{Guid.NewGuid():N}.cbt");
        try
        {
            var table = _tablebase.Get(Material.Parse("KRvK"));
            table.Save(path);
            var loaded = EndgameTable.Load(path);
            Assert.Equal(table.Material, loaded.Material);
            Assert.Equal(table.Size, loaded.Size);
            for (long index = 0; index < table.Size; index += 97)
                Assert.Equal(table[index], loaded[index]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("KQvK", "KQvK", true)]
    [InlineData("kvkq", "KvKQ", false)]
    [InlineData("KRvKQ", "KRvKQ", false)]
    [InlineData("KNBvK", "KBNvK", true)]
    [InlineData("KBvKN", "KBvKN", true)]
    public void MaterialParsesAndKnowsWhichSideIsStronger(string text, string normalised, bool canonical)
    {
        var material = Material.Parse(text);
        Assert.Equal(normalised, material.ToString());
        Assert.Equal(canonical, material.IsCanonical);
        Assert.True(material.Canonical.IsCanonical);
    }

    [Fact]
    public void OutcomesFlipBetweenMovers()
    {
        Assert.Equal(Outcome.Win(1), Outcome.Loss(0).ForPreviousMover());
        Assert.Equal(Outcome.Loss(2), Outcome.Win(1).ForPreviousMover());
        Assert.Equal(Outcome.Draw, Outcome.Draw.ForPreviousMover());
        Assert.Equal(1, Outcome.Win(1).MovesToMate);
        Assert.Equal(10, Outcome.Win(19).MovesToMate);
        Assert.True(Outcome.Win(3).Score > Outcome.Win(5).Score);
        Assert.True(Outcome.Draw.Score > Outcome.Loss(40).Score);
        Assert.True(Outcome.Loss(40).Score > Outcome.Loss(2).Score);
    }
}
