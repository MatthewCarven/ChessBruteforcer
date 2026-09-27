using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Game;
using ChessBruteforcer.Core.Match;
using ChessBruteforcer.Core.Records;

namespace ChessBruteforcer.Tests;

public class SanParseTests
{
    [Theory]
    [InlineData(Fen.StartPosition, "e4", "e2e4")]
    [InlineData(Fen.StartPosition, "Nf3", "g1f3")]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", "O-O", "e1g1")]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", "O-O-O", "e1c1")]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", "0-0", "e1g1")]            // zeros, not letters
    [InlineData("rnbqkbnr/ppp1pppp/8/3p4/4P3/8/PPPP1PPP/RNBQKBNR w KQkq d6 0 2", "exd5", "e4d5")]
    [InlineData("8/P6k/8/8/8/8/8/K7 w - - 0 1", "a8=Q", "a7a8q")]
    [InlineData("8/P6k/8/8/8/8/8/K7 w - - 0 1", "a8N", "a7a8n")]                  // no '='
    [InlineData("rnbqkbnr/pppp1ppp/8/4p3/6P1/5P2/PPPPP2P/RNBQKBNR b KQkq - 0 2", "Qh4#", "d8h4")]
    [InlineData("4k3/8/8/8/8/8/8/R4RK1 w - - 0 1", "Rad1", "a1d1")]
    [InlineData("4k3/8/8/R7/8/8/8/R3K3 w - - 0 1", "R1a3", "a1a3")]
    [InlineData("4k3/8/8/8/8/2N3N1/8/2N1K3 w - - 0 1", "Nc3e2", "c3e2")]
    [InlineData("4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1", "exd6", "e5d6")]             // en passant
    [InlineData(Fen.StartPosition, "Nf3!?", "g1f3")]                                // annotation ignored
    public void StandardNotationNamesTheRightMove(string fen, string san, string uci)
    {
        var position = Position.FromFen(fen);
        Assert.Equal(position.ParseUciMove(uci), San.Parse(position, san));
    }

    [Theory]
    [InlineData(Fen.StartPosition, "e5")]                                            // not legal
    [InlineData("4k3/8/8/8/8/8/8/R4RK1 w - - 0 1", "Rd1")]                          // two rooks can
    [InlineData(Fen.StartPosition, "Zz9")]                                           // not a move
    public void BadNotationIsRefused(string fen, string san)
    {
        Assert.Throws<FormatException>(() => San.Parse(Position.FromFen(fen), san));
    }

    [Fact]
    public void EveryMoveReadsBackFromItsOwnNotation()
    {
        foreach (var (position, moves) in GameFileTests.RandomPositions())
            foreach (var move in moves)
                Assert.Equal(move, San.Parse(position, San.Of(position, move)));
    }
}

public class MoveCodeTests
{
    [Fact]
    public void EveryMoveDecodesToItself()
    {
        foreach (var (position, moves) in GameFileTests.RandomPositions())
            foreach (var move in moves)
                Assert.Equal(move, MoveCode.Decode(position, MoveCode.Encode(position, move)));
    }

    [Fact]
    public void CodeOrderIsFromSquareThenToSquareThenPromotion()
    {
        var moves = MoveCode.Canonical(Position.FromFen("n1n5/PPPk4/8/8/8/8/4Kppp/5N1N w - - 0 1"));
        for (int i = 1; i < moves.Count; i++)
        {
            var (a, b) = (moves[i - 1], moves[i]);
            Assert.True((a.From, a.To, (int)a.Promotion).CompareTo((b.From, b.To, (int)b.Promotion)) < 0);
        }
    }

    [Fact]
    public void TheMostMovesAnyPositionHasStillFitsInAByte()
    {
        // The known record, 218 legal moves.
        var position = Position.FromFen("3Q4/1Q4Q1/4Q3/2Q4R/Q4Q2/3Q4/1Q4Rp/1K1BBNNk w - - 0 1");
        var moves = MoveCode.Canonical(position);
        Assert.Equal(218, moves.Count);
        Assert.Equal(217, MoveCode.Encode(position, moves[^1]));
    }

    [Fact]
    public void ACodePastTheLastMoveIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => MoveCode.Decode(Position.Start(), 20));   // 20 moves: 0-19
    }
}

public class GameAnalysisTests
{
    private static StoredGame Game(string moves, string result = "*", string termination = "Normal") =>
        Pgn.Parse($"[Result \"{result}\"]\n[Termination \"{termination}\"]\n\n{moves} {result}")[0];

    [Fact]
    public void KnightsGoingOutAndBackAreShufflesAndRepeats()
    {
        // Out and back twice: every return is a shuffle, and the start position comes round again.
        var m = GameAnalysis.Measure(Game("1. Nf3 Nf6 2. Ng1 Ng8 3. Nf3 Nf6 4. Ng1 Ng8"));
        Assert.Equal(8, m.Plies);
        Assert.Equal(6, m.Shuffles);      // Ng1, Ng8, Nf3, Nf6, Ng1, Ng8: each undoes its side's last move
        Assert.Equal(0, m.IdleShuffles);  // all inside the first 10 quiet plies
        Assert.Equal(5, m.Repeats);       // plies 4-8 each recreate the position from 4 plies before
        Assert.Equal(8, m.LongestQuiet);
        Assert.Equal(0, m.Captures);
        Assert.False(m.EndsInMate);
    }

    [Fact]
    public void ShufflesCountAsIdleOnlyOnceNothingHasHappenedForAWhile()
    {
        // Twelve quiet plies of knight dancing: shuffles from ply 3, the clock reaches 10 at ply 10.
        var m = GameAnalysis.Measure(Game("1. Nf3 Nf6 2. Ng1 Ng8 3. Nf3 Nf6 4. Ng1 Ng8 5. Nf3 Nf6 6. Ng1 Ng8"));
        Assert.Equal(10, m.Shuffles);
        Assert.Equal(3, m.IdleShuffles);  // plies 10, 11, 12

        // A pawn move restarts the count, so the same dance after it starts from zero again.
        var reset = GameAnalysis.Measure(Game("1. Nf3 Nf6 2. Ng1 Ng8 3. Nf3 Nf6 4. Ng1 Ng8 5. e3 Nf6 6. Nf3 Ng8"));
        Assert.Equal(0, reset.IdleShuffles);
    }

    [Fact]
    public void QuietStretchesResetOnCapturesAndPawnMoves()
    {
        var m = GameAnalysis.Measure(Game("1. e4 d5 2. exd5 Qxd5 3. Nc3 Qa5 4. Nf3 Nf6"));
        Assert.Equal(2, m.Captures);
        Assert.Equal(4, m.LongestQuiet);  // after Qxd5 the clock runs 3. Nc3 Qa5 4. Nf3 Nf6
        Assert.Equal(0, m.Shuffles);
    }

    [Theory]
    [InlineData("1. f3 e5 2. g4 Qh4#", "0-1", "Normal", GameStyle.EarlyKill)]
    [InlineData("1. f3 e5 2. g4 Qh4#", "0-1", "Time forfeit", GameStyle.Clock)]
    [InlineData("1. e4 e5", "1/2-1/2", "Normal", GameStyle.CleanDraw)]
    [InlineData("1. Nf3 Nf6 2. Ng1 Ng8 3. Nf3 Nf6 4. Ng1 Ng8", "1/2-1/2", "Normal", GameStyle.TimeWaster)]
    public void GamesAreGradedByHowTheyWent(string moves, string result, string termination, GameStyle style)
    {
        var game = Game(moves, result, termination);
        Assert.Equal(style, GameAnalysis.Grade(game, GameAnalysis.Measure(game)));
    }

    [Fact]
    public void AnEnPassantSquareCountsOnlyIfSomeoneCanUseIt()
    {
        // Same position both ways; the second ends on a double push nobody can capture.
        var a = Game("1. e4 e5 2. Nf3").PositionAt(3);
        var b = Game("1. Nf3 e5 2. e4").PositionAt(3);
        Assert.NotEqual(a.Hash, b.Hash);
        Assert.Equal(GameAnalysis.Key(a), GameAnalysis.Key(b));

        // Here the capture is on, so the square is part of the position.
        var live = Position.FromFen("4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1");
        var dead = Position.FromFen("4k3/8/8/3pP3/8/8/8/4K3 w - - 0 1");
        Assert.NotEqual(GameAnalysis.Key(dead), GameAnalysis.Key(live));
    }

    [Fact]
    public void FoolsMateIsMateInFour()
    {
        var m = GameAnalysis.Measure(Game("1. f3 e5 2. g4 Qh4#", "0-1"));
        Assert.True(m.EndsInMate);
        Assert.Equal(4, m.Plies);
    }

    [Fact]
    public void TheTreeStoresSharedOpeningsOnceAndSpotsRepeatGames()
    {
        var stats = new GameTreeStats();
        stats.Add(Game("1. e4 e5 2. Nf3 Nc6"));
        stats.Add(Game("1. e4 e5 2. Nf3 Nf6"));      // leaves the first game's path at ply 3
        stats.Add(Game("1. e4 e5 2. Nf3 Nc6"));      // a repeat of the first
        stats.Add(Game("1. Nf3 Nc6 2. e4 e5"));      // new moves, but it transposes to the first game's position
        stats.Add(Game("1. e4"));                     // a shorter game inside the first: not a repeat

        Assert.Equal(17, stats.Plies);
        Assert.Equal(9, stats.UniquePrefixes);       // 4 + 1 + 0 + 4 + 0
        Assert.Equal(1, stats.DuplicateGames);
        Assert.Equal(new[] { 0, 3, 4, 0, 1 }, stats.NewFrom);
        // Positions: the start, e4, e4 e5, e4 e5 Nf3, ...Nc6, ...Nf6, then Nf3, Nf3 Nc6, Nf3 Nc6 e4
        // (and e4 e5 Nf3 Nc6 again, merged).
        Assert.Equal(9, stats.UniquePositions);
    }
}

public class GameFileTests
{
    // Exactly what the writer produces, so it has to come back out unchanged.
    private const string Opera = """
        [Event "Casual \"Opera\" game"]
        [Site "Paris"]
        [Date "1858.??.??"]
        [White "Morphy, Paul"]
        [Black "Duke Karl / Count Isouard"]
        [Result "1-0"]

        1. e4 e5 2. Nf3 d6 3. d4 Bg4 4. dxe5 Bxf3 5. Qxf3 dxe5 6. Bc4 Nf6 7. Qb3 Qe7 8.
        Nc3 c6 9. Bg5 b5 10. Nxb5 cxb5 11. Bxb5+ Nbd7 12. O-O-O Rd8 13. Rxd7 Rxd7 14.
        Rd1 Qe6 15. Bxd7+ Nxd7 16. Qb8+ Nxb8 17. Rd8# 1-0


        """;

    // The same game the way PGN turns up in the wild, plus two more after it.
    private const string Wild = """
        % an escape line
        [Event "Casual \"Opera\" game"]
        [Site "Paris"]
        [Date "1858.??.??"]
        [White "Morphy, Paul"]
        [Black "Duke Karl / Count Isouard"]
        [Result "1-0"]

        1.e4 e5 {the Philidor follows} 2.Nf3 d6 3.d4 Bg4?! (3...exd4 {main line} (3...Nd7 {also ( fine )})) 4.dxe5
        Bxf3 5.Qxf3 dxe5 6.Bc4 Nf6 7.Qb3 Qe7 8.Nc3 c6 9.Bg5 b5 $6 10.Nxb5! cxb5 11.Bxb5+ Nbd7 12.0-0-0 Rd8 ; to the end
        13.Rxd7 Rxd7 14.Rd1 Qe6 15.Bxd7+ Nxd7 16.Qb8+!! Nxb8 17.Rd8# 1-0
        [Event "Set-up: en passant, then an under-promotion"]
        [SetUp "1"]
        [FEN "4k3/1P6/8/3pP3/8/8/8/4K3 w - d6 0 1"]

        1. exd6 e.p. Kd7 2. b8=N+ Kxd6 *
        [Event "No result after the moves"]
        [Result "1/2-1/2"]

        1. d4 1... d5
        """;

    [Fact]
    public void PgnInIsPgnOut()
    {
        var games = Pgn.Parse(Opera);
        var game = Assert.Single(games);
        Assert.Equal(33, game.Moves.Count);
        Assert.Equal("1-0", game.Result);
        Assert.Equal(GameStatus.Checkmate, game.PositionAt(game.Moves.Count).Status());
        Assert.Equal(Lf(Opera), Lf(game.ToPgn()));
    }

    [Fact]
    public void CommentsVariationsAndNagsAreDroppedAndTheMainLineKept()
    {
        var games = Pgn.Parse(Wild);
        Assert.Equal(3, games.Count);
        Assert.Equal(Pgn.Parse(Opera)[0].Moves, games[0].Moves);
        Assert.Equal(Lf(Opera), Lf(games[0].ToPgn()));
        Assert.Equal("Casual \"Opera\" game", games[0].Tag("Event"));

        Assert.Equal("4k3/1P6/8/3pP3/8/8/8/4K3 w - d6 0 1", games[1].StartFen);
        Assert.Equal(new[] { "e5d6", "e8d7", "b7b8n", "d7d6" }, games[1].Moves.Select(m => m.ToUci()));
        Assert.Equal("*", games[1].Result);

        Assert.Equal(2, games[2].Moves.Count);
        Assert.Equal("1/2-1/2", games[2].Result);    // from the tag, as the moves have none
    }

    [Fact]
    public void GamesSurviveTheFileAtOneBytePerMove()
    {
        var games = Pgn.Parse(Wild);
        WithTempFile(path =>
        {
            var (written, moves) = GameFile.Write(path, games);
            Assert.Equal(3, written);
            Assert.Equal(39, moves);
            var read = GameFile.Read(path).ToList();
            Assert.Equal(games.Select(g => g.ToPgn()), read.Select(g => g.ToPgn()));

            // The same games with no moves: the difference is the moves, one byte each.
            var bare = games.Select(g => g with { Moves = Array.Empty<Move>() });
            long full = new FileInfo(path).Length;
            GameFile.Write(path, bare);
            Assert.Equal(39, full - new FileInfo(path).Length);
        });
    }

    [Fact]
    public void AppendingAddsToTheEnd()
    {
        var games = Pgn.Parse(Wild);
        WithTempFile(path =>
        {
            GameFile.Append(path, games.Take(1));
            GameFile.Append(path, games.Skip(1));
            Assert.Equal(games.Select(g => g.ToPgn()), GameFile.Read(path).Select(g => g.ToPgn()));
        });
    }

    [Fact]
    public void AnyOneGameCanBeReadByNumber()
    {
        var games = Enumerable.Repeat(Pgn.Parse(Wild), 400).SelectMany(g => g).ToList();   // 1,200 games
        WithTempFile(path =>
        {
            GameFile.Write(path, games);
            Assert.Equal(1200, GameFile.Count(path));
            foreach (int number in new[] { 1, 2, 3, 600, 1199, 1200 })
                Assert.Equal(games[number - 1].ToPgn(), GameFile.Read(path, number).ToPgn());
            Assert.Throws<ArgumentException>(() => GameFile.Read(path, 0));
            Assert.Throws<ArgumentException>(() => GameFile.Read(path, 1201));

            File.WriteAllBytes(path, File.ReadAllBytes(path)[..^1]);             // lose the last move byte
            Assert.Throws<InvalidDataException>(() => GameFile.Count(path));
            Assert.Equal(games[1198].ToPgn(), GameFile.Read(path, 1199).ToPgn());  // earlier games still fine
            Assert.Throws<InvalidDataException>(() => GameFile.Read(path, 1200));
        });
    }

    [Fact]
    public void ReplayReachesEveryPlyAndNoFurther()
    {
        var game = Pgn.Parse(Opera)[0];
        var position = game.StartPosition();
        for (int ply = 0; ply <= game.Moves.Count; ply++)
        {
            Assert.Equal(position.ToFen(), game.PositionAt(ply).ToFen());
            if (ply < game.Moves.Count)
                position.MakeMove(game.Moves[ply]);
        }
        Assert.Equal(Fen.StartPosition, game.PositionAt(0).ToFen());
        Assert.Equal(GameStatus.Checkmate, game.PositionAt(33).Status());
        Assert.Throws<ArgumentOutOfRangeException>(() => game.PositionAt(34));
        Assert.Throws<ArgumentOutOfRangeException>(() => game.PositionAt(-1));

        var san = game.San();
        Assert.Equal(33, san.Count);
        Assert.Equal(new[] { "e4", "e5", "Nf3" }, san.Take(3));
        Assert.Equal(new[] { "Qb8+", "Nxb8", "Rd8#" }, san.TakeLast(3));
        Assert.Contains("O-O-O", san);
        Assert.Contains("Nbd7", san);
    }

    [Fact]
    public void AnIllegalMoveSaysWhichGameAndMove()
    {
        var e = Assert.Throws<FormatException>(() => Pgn.Parse("[White \"A\"]\n\n1. e4 e5 2. Ke3 *"));
        Assert.Contains("game 1", e.Message);
        Assert.Contains("move 2", e.Message);
        Assert.Contains("Ke3", e.Message);
    }

    [Fact]
    public void DamagedFilesAreRefused()
    {
        WithTempFile(path =>
        {
            File.WriteAllBytes(path, "NOPE"u8.ToArray());
            Assert.Throws<InvalidDataException>(() => GameFile.Read(path).ToList());

            GameFile.Write(path, Pgn.Parse(Opera));
            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes[..^1]);                        // cut off the last move
            Assert.Throws<InvalidDataException>(() => GameFile.Read(path).ToList());

            bytes[^1] = 250;                                              // a code no position has
            File.WriteAllBytes(path, bytes);
            Assert.Throws<InvalidDataException>(() => GameFile.Read(path).ToList());
        });
    }

    /// <summary>Positions from seeded random games, each with its legal moves.</summary>
    internal static IEnumerable<(Position, List<Move>)> RandomPositions()
    {
        string[] starts =
        {
            Fen.StartPosition,
            "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",   // castling, pins
            "n1n5/PPPk4/8/8/8/8/4Kppp/5N1N b - - 0 1",                                // promotions
        };
        var random = new Random(27);
        foreach (string fen in starts)
        {
            for (int game = 0; game < 8; game++)
            {
                var position = Position.FromFen(fen);
                for (int ply = 0; ply < 120; ply++)
                {
                    var moves = MoveGenerator.Legal(position);
                    if (moves.Count == 0)
                        break;
                    yield return (position, moves);
                    position.MakeMove(moves[random.Next(moves.Count)]);
                }
            }
        }
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n");

    private static void WithTempFile(Action<string> test)
    {
        string path = Path.Combine(Path.GetTempPath(), $"games-{Guid.NewGuid():N}.cbg");
        try
        {
            test(path);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }
}
