using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Tests;

public class MoveGeneratorTests
{
    public const string Kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
    public const string EnPassantPins = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";
    public const string Promotions = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1";
    public const string CastlingDiscovery = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8";
    public const string Middlegame = "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10";

    /// <summary>
    /// Published perft counts (chessprogramming.org "Perft Results").  Depths
    /// are kept small enough for a debug test run; `perft` on the CLI goes deeper.
    /// </summary>
    [Theory]
    [InlineData(Fen.StartPosition, 1, 20)]
    [InlineData(Fen.StartPosition, 2, 400)]
    [InlineData(Fen.StartPosition, 3, 8_902)]
    [InlineData(Fen.StartPosition, 4, 197_281)]
    [InlineData(Kiwipete, 1, 48)]
    [InlineData(Kiwipete, 2, 2_039)]
    [InlineData(Kiwipete, 3, 97_862)]
    [InlineData(EnPassantPins, 1, 14)]
    [InlineData(EnPassantPins, 2, 191)]
    [InlineData(EnPassantPins, 3, 2_812)]
    [InlineData(EnPassantPins, 4, 43_238)]
    [InlineData(EnPassantPins, 5, 674_624)]
    [InlineData(Promotions, 1, 6)]
    [InlineData(Promotions, 2, 264)]
    [InlineData(Promotions, 3, 9_467)]
    [InlineData(Promotions, 4, 422_333)]
    [InlineData(CastlingDiscovery, 1, 44)]
    [InlineData(CastlingDiscovery, 2, 1_486)]
    [InlineData(CastlingDiscovery, 3, 62_379)]
    [InlineData(Middlegame, 1, 46)]
    [InlineData(Middlegame, 2, 2_079)]
    [InlineData(Middlegame, 3, 89_890)]
    public void PerftMatchesPublishedCounts(string fen, int depth, long expected)
    {
        Assert.Equal(expected, Perft.Count(Position.FromFen(fen), depth));
    }

    [Theory]
    [InlineData(Fen.StartPosition)]
    [InlineData(Kiwipete)]
    [InlineData(Promotions)]
    [InlineData(CastlingDiscovery)]
    public void MakeThenUnmakeRestoresEverything(string fen)
    {
        var position = Position.FromFen(fen);
        string before = position.ToFen();
        foreach (var move in MoveGenerator.Legal(position))
        {
            var undo = position.MakeMove(move);
            position.UnmakeMove(move, undo);
            Assert.Equal(before, position.ToFen());
        }
    }

    [Theory]
    [InlineData(Fen.StartPosition)]
    [InlineData(Kiwipete)]
    [InlineData("8/8/8/4k3/8/8/8/4K2Q b - - 12 40")]
    public void FullFenRoundTrips(string fen)
    {
        Assert.Equal(fen, Position.FromFen(fen).ToFen());
    }

    [Fact]
    public void DoublePushSetsTheEnPassantSquareAndClocksAdvance()
    {
        var position = Position.Start();
        position.MakeMove(position.ParseUciMove("e2e4")!.Value);
        Assert.Equal("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1", position.ToFen());
        position.MakeMove(position.ParseUciMove("g8f6")!.Value);
        Assert.Equal("rnbqkb1r/pppppppp/5n2/8/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 1 2", position.ToFen());
    }

    [Fact]
    public void FoolsMateIsCheckmate()
    {
        var position = Position.Start();
        foreach (string uci in new[] { "f2f3", "e7e5", "g2g4", "d8h4" })
            position.MakeMove(position.ParseUciMove(uci)!.Value);
        Assert.True(position.InCheck());
        Assert.Equal(GameStatus.Checkmate, position.Status());
        Assert.Empty(MoveGenerator.Legal(position));
    }

    [Fact]
    public void StalemateIsNotCheckmate()
    {
        var position = Position.FromFen("7k/5Q2/6K1/8/8/8/8/8 b - - 0 1");
        Assert.False(position.InCheck());
        Assert.Equal(GameStatus.Stalemate, position.Status());
    }

    [Theory]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", "e1g1", true)]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", "e1c1", true)]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w Qkq - 0 1", "e1g1", false)]     // right lost
    [InlineData("r3k2r/8/8/8/8/8/8/R3K1NR w KQkq - 0 1", "e1g1", false)]   // blocked
    [InlineData("r3k2r/8/8/8/8/8/5r2/R3K2R w KQkq - 0 1", "e1g1", false)]  // f1 attacked: passes through check
    [InlineData("r3k2r/8/8/8/8/8/1r6/R3K2R w KQkq - 0 1", "e1c1", true)]   // only b1 attacked: allowed
    [InlineData("r3k2r/8/8/8/8/8/4r3/R3K2R w KQkq - 0 1", "e1g1", false)]  // in check
    public void CastlingFollowsTheRules(string fen, string uci, bool legal)
    {
        Assert.Equal(legal, Position.FromFen(fen).ParseUciMove(uci) is not null);
    }

    [Fact]
    public void CastlingMovesTheRookAndLosesTheRights()
    {
        var position = Position.FromFen("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");
        position.MakeMove(position.ParseUciMove("e1g1")!.Value);
        Assert.Equal("r3k2r/8/8/8/8/8/8/R4RK1 b kq - 1 1", position.ToFen());
        position.MakeMove(position.ParseUciMove("a8a1")!.Value);
        Assert.Equal("4k2r/8/8/8/8/8/8/r4RK1 w k - 0 2", position.ToFen());
    }

    [Fact]
    public void PromotionOffersFourPieces()
    {
        var position = Position.FromFen("8/P6k/8/8/8/8/8/K7 w - - 0 1");
        var promotions = MoveGenerator.Legal(position).Where(m => m.IsPromotion).Select(m => m.ToUci()).Order();
        Assert.Equal(new[] { "a7a8b", "a7a8n", "a7a8q", "a7a8r" }, promotions);
    }

    [Fact]
    public void EnPassantThatExposesTheKingIsIllegal()
    {
        // b5xc6 e.p. would remove both pawns from rank 5 and open the rook's line to a5.
        var position = Position.FromFen("8/8/8/KPp4r/8/8/8/7k w - c6 0 2");
        Assert.Null(position.ParseUciMove("b5c6"));
        var allowed = Position.FromFen("8/8/8/1Pp4r/K7/8/8/7k w - c6 0 2");
        Assert.NotNull(allowed.ParseUciMove("b5c6"));
    }
}
