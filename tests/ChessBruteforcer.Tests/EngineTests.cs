using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Game;
using ChessBruteforcer.Core.Match;

namespace ChessBruteforcer.Tests;

public class HashTests
{
    [Fact]
    public void IncrementalHashAlwaysMatchesARecomputation()
    {
        var rng = new Random(5);
        foreach (string fen in new[] { Fen.StartPosition, MoveGeneratorTests.Kiwipete, MoveGeneratorTests.Promotions })
        {
            var position = Position.FromFen(fen);
            var played = new Stack<(Move, Undo)>();
            for (int ply = 0; ply < 60; ply++)
            {
                var moves = MoveGenerator.Legal(position);
                if (moves.Count == 0)
                    break;
                var move = moves[rng.Next(moves.Count)];
                played.Push((move, position.MakeMove(move)));
                Assert.Equal(position.ComputeHash(), position.Hash);
            }
            ulong start = Position.FromFen(fen).Hash;
            while (played.TryPop(out var step))
                position.UnmakeMove(step.Item1, step.Item2);
            Assert.Equal(start, position.Hash);
        }
    }

    [Fact]
    public void TranspositionsHashAlike()
    {
        var a = Position.Start();
        foreach (string m in new[] { "g1f3", "g8f6", "b1c3" })
            a.MakeMove(a.ParseUciMove(m)!.Value);
        var b = Position.Start();
        foreach (string m in new[] { "b1c3", "g8f6", "g1f3" })
            b.MakeMove(b.ParseUciMove(m)!.Value);
        Assert.Equal(a.ToFen().Split(' ')[0..4], b.ToFen().Split(' ')[0..4]);
        Assert.Equal(a.Hash, b.Hash);
        Assert.NotEqual(Position.Start().Hash, a.Hash);
    }

    [Fact]
    public void SideToMoveCastlingAndEnPassantChangeTheHash()
    {
        ulong white = Position.FromFen("4k3/8/8/8/8/8/8/4K3 w - - 0 1").Hash;
        ulong black = Position.FromFen("4k3/8/8/8/8/8/8/4K3 b - - 0 1").Hash;
        Assert.NotEqual(white, black);
        Assert.NotEqual(Position.FromFen("r3k3/8/8/8/8/8/8/4K3 b q - 0 1").Hash,
                        Position.FromFen("r3k3/8/8/8/8/8/8/4K3 b - - 0 1").Hash);
        Assert.NotEqual(Position.FromFen("4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1").Hash,
                        Position.FromFen("4k3/8/8/3pP3/8/8/8/4K3 w - - 0 1").Hash);
        Assert.Equal(Position.FromFen("4k3/8/8/8/8/8/8/4K3 b - - 0 1").Hash,
                     Position.Empty(Colour.Black).Hash ^ Zobrist.Piece(new Piece(PieceType.King, Colour.White), 4)
                                                       ^ Zobrist.Piece(new Piece(PieceType.King, Colour.Black), 60));
    }

    [Fact]
    public void NullMoveRoundTrips()
    {
        var position = Position.FromFen("4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1");
        string fen = position.ToFen();
        ulong hash = position.Hash;
        var undo = position.MakeNullMove();
        Assert.Equal(Colour.Black, position.SideToMove);
        Assert.Equal(Position.NoSquare, position.EnPassantSquare);
        Assert.Equal(position.ComputeHash(), position.Hash);
        position.UnmakeNullMove(undo);
        Assert.Equal(fen, position.ToFen());
        Assert.Equal(hash, position.Hash);
    }
}

public class EvaluationTests
{
    [Fact]
    public void TheStartPositionIsLevel()
    {
        Assert.Equal(0, Evaluation.Evaluate(Position.Start()));
    }

    [Theory]
    [InlineData(MoveGeneratorTests.Kiwipete)]
    [InlineData(MoveGeneratorTests.Middlegame)]
    [InlineData("8/8/8/4k3/8/8/3P4/4K3 w - - 0 1")]
    [InlineData("r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R b KQkq - 4 4")]
    public void EvaluationIsColourBlind(string fen)
    {
        var position = Position.FromFen(fen);
        Assert.Equal(Evaluation.Evaluate(position), Evaluation.Evaluate(Tablebase.SwapColours(position)));
    }

    [Fact]
    public void ExtraMaterialScoresForItsOwner()
    {
        Assert.True(Evaluation.Evaluate(Position.FromFen("4k3/8/8/8/8/8/8/3QK3 w - - 0 1")) > 800);
        Assert.True(Evaluation.Evaluate(Position.FromFen("4k3/8/8/8/8/8/8/3QK3 b - - 0 1")) < -800);
    }
}

/// <summary>K+R v K solved once, for checking the search's mate distances against it.</summary>
public sealed class RookEndgame
{
    public Tablebase Tablebase { get; } = new();
    public EndgameTable Table { get; }

    public RookEndgame() => Table = Tablebase.Get(Material.Parse("KRvK"));
}

public class SearchTests : IClassFixture<RookEndgame>
{
    private readonly RookEndgame _rook;

    public SearchTests(RookEndgame rook) => _rook = rook;

    private static SearchResult Think(string fen, int depth, Tablebase? tablebase = null) =>
        new Search(new TranspositionTable(8), tablebase).Run(Position.FromFen(fen), new SearchLimits { Depth = depth });

    [Theory]
    [InlineData("6k1/5ppp/8/8/8/8/5PPP/R5K1 w - - 0 1", "a1a8")]                              // back-rank mate
    [InlineData("r1bqkb1r/pppp1ppp/2n2n2/4p2Q/2B1P3/8/PPPP1PPP/RNB1K1NR w KQkq - 4 4", "h5f7")] // scholar's mate
    [InlineData("rnbqkbnr/ppppp2p/5p2/6p1/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 0 3", "d1h5")]       // fool's mate reversed
    public void FindsMateInOne(string fen, string mate)
    {
        var result = Think(fen, 4);
        Assert.Equal(mate, result.BestMove?.ToUci());
        Assert.Equal(1, Search.MateIn(result.Score));
    }

    [Fact]
    public void TakesAFreeQueen()
    {
        Assert.Equal("d1d5", Think("4k3/8/8/3q4/8/8/8/3QK3 w - - 0 1", 5).BestMove?.ToUci());
    }

    [Fact]
    public void DoesNotTakeAPoisonedPawn()
    {
        // Qxb7?? loses the queen to Rxb7: the pawn is defended by the rook on b8.
        var result = Think("1r2k3/1p6/8/8/8/8/8/1Q2K3 w - - 0 1", 5);
        Assert.NotEqual("b1b7", result.BestMove?.ToUci());
    }

    [Fact]
    public void KnowsStalemateIsADraw()
    {
        // Black to move has no moves and is not in check.
        var result = Think("7k/5Q2/6K1/8/8/8/8/8 b - - 0 1", 3);
        Assert.Null(result.BestMove);
        Assert.Equal(0, result.Score);
    }

    /// <summary>
    /// Mate distances from the search, which knows nothing about the
    /// tables, must match the tables exactly: two independent methods, one
    /// forwards and one backwards, agreeing.
    /// </summary>
    [Theory]
    [InlineData(3)]    // mate in 2
    [InlineData(5)]    // mate in 3
    public void SearchMateDistancesMatchTheEndgameTable(int plies)
    {
        var table = _rook.Table;
        int found = 0;
        for (long index = 0; index < table.Size && found < 5; index += 7)
        {
            if (table[index] != Outcome.Win(plies))
                continue;
            string fen = table.PositionAt(index).ToFen();
            var result = Think(fen, plies + 4);
            Assert.True(Search.MateIn(result.Score) == (plies + 1) / 2, $"{fen}: search says {result.Score}");
            found++;
        }
        Assert.Equal(5, found);
    }

    [Fact]
    public void WithTablesItPlaysPerfectEndgames()
    {
        var position = Position.FromFen("8/8/8/8/8/2k5/1R6/K7 w - - 0 1");   // K+R v K, mate in 16
        var tables = _rook.Tablebase;
        var result = new Search(new TranspositionTable(8), tables).Run(position, new SearchLimits { Depth = 1 });
        Assert.Equal(16, Search.MateIn(result.Score));
        Assert.Equal(Outcome.Win(31), tables.Probe(position));
        position.MakeMove(result.BestMove!.Value);
        Assert.Equal(Outcome.Loss(30), tables.Probe(position));   // the move kept the fastest mate
    }

    [Fact]
    public void StopsOnANodeLimit()
    {
        var result = new Search(new TranspositionTable(8))
            .Run(Position.Start(), new SearchLimits { Nodes = 20_000 });
        Assert.NotNull(result.BestMove);
        Assert.True(result.Nodes < 40_000);
    }

    [Fact]
    public void AvoidsARepetitionWhenWinning()
    {
        // White is a queen up.  Saying the current position has already been seen twice
        // means any line that returns to it scores 0, so the search must look elsewhere
        // and still report a winning score.
        var position = Position.FromFen("4k3/8/8/8/8/8/8/3QK3 w - - 0 1");
        var history = new List<ulong> { position.Hash, 1UL, position.Hash, 2UL };
        var result = new Search(new TranspositionTable(8))
            .Run(position, new SearchLimits { Depth = 5 }, history);
        Assert.True(result.Score > 500);
    }
}

/// <summary>
/// Table play under the 50-move rule at the game's own clock (Matthew's
/// "wanderer": an opponent who knows the tables can run the clock up).
/// </summary>
public class RuleAwareTableTests : IClassFixture<RookEndgame>
{
    private readonly Tablebase _tables;

    public RuleAwareTableTests(RookEndgame rook) => _tables = rook.Tablebase;   // (DTZ tables are solved as asked for)

    private SearchResult Play(string fen) =>
        new Search(new TranspositionTable(8), _tables).Run(Position.FromFen(fen), new SearchLimits { Depth = 1 });

    private static string WithClock(string fen, int clock)
    {
        var fields = fen.Split(' ');
        fields[4] = clock.ToString();
        return string.Join(' ', fields);
    }

    [Theory]
    [InlineData("8/8/8/8/8/2k5/1R6/K7 w - - 0 1")]   // K+R v K, white to move and win (mate in 16)
    [InlineData("8/8/8/8/8/2k5/8/KR6 b - - 0 1")]    // black to move and lose
    public void AResultTheClockRunsOutOnIsADraw(string fen)
    {
        var dtz = _tables.ProbeDtz(Position.FromFen(fen));
        Assert.NotEqual(OutcomeKind.Draw, dtz.Kind);
        int last = EndgameTable.RulePlies - dtz.Plies;   // the highest clock that still leaves time for it

        var inTime = Play(WithClock(fen, last));
        Assert.NotEqual(0, inTime.Score);
        Assert.Equal(dtz.Kind == OutcomeKind.Win, inTime.Score > 0);
        Assert.True(_tables.TryProbeWithClock(Position.FromFen(WithClock(fen, last)), out var fits));
        Assert.Equal(dtz.Kind, fits.Kind);

        var tooLate = Play(WithClock(fen, last + 1));
        Assert.Equal(0, tooLate.Score);
        Assert.True(_tables.TryProbeWithClock(Position.FromFen(WithClock(fen, last + 1)), out var drawn));
        Assert.Equal(Outcome.Draw, drawn);
        Assert.Equal(dtz.Kind, _tables.Probe(Position.FromFen(WithClock(fen, last + 1))).Kind);   // (without the rule, unchanged)
    }

    [Fact]
    public void WithOnePlyLeftOnlyAPawnMoveKeepsTheWin()
    {
        // K+P v K with the count at 99: any king move lets it reach 100, a draw; a pawn move starts it again.
        string fen = "8/8/8/8/8/8/4P3/4K2k w - - 99 1";
        var position = Position.FromFen(fen);
        var result = Play(fen);
        Assert.True(result.Score > 0);
        Assert.Equal(PieceType.Pawn, position[result.BestMove!.Value.From].Type);
        // Without the rule a king move wins too, so the clock is what made the choice.
        Assert.Contains(_tables.RankMoves(position), r => position[r.Move.From].Type == PieceType.King && r.Outcome.Kind == OutcomeKind.Win);
        Assert.All(_tables.RankMovesUnderRule(position).Where(r => position[r.Move.From].Type == PieceType.King),
                   r => Assert.Equal(Outcome.Draw, r.Outcome));
    }

    [Fact]
    public void WinningItPlaysTowardsTheNextResetFirst()
    {
        // Every move the engine picks, played out, keeps the win and brings the next capture,
        // pawn move or mate one ply nearer (or makes it): the clock can't catch it.
        var position = Position.FromFen("8/8/8/8/8/8/4P3/4K2k w - - 60 1");
        for (int ply = 0; ply < 12 && position.Status() == GameStatus.Ongoing; ply++)
        {
            var before = _tables.ProbeDtz(position);
            var move = Play(position.ToFen()).BestMove!.Value;
            bool zeroing = move.IsCapture || move.IsPromotion || position[move.From].Type == PieceType.Pawn;
            position.MakeMove(move);
            if (before.Kind == OutcomeKind.Win && !zeroing)
                Assert.Equal(Outcome.Loss(before.Plies - 1), _tables.ProbeDtz(position));
        }
    }

    [Fact]
    public void AdjudicationFollowsTheRule()
    {
        string fen = "8/8/8/8/8/2k5/1R6/K7 w - - 0 1";
        var seen = new Dictionary<ulong, int>();
        var (result, _) = GamePlayer.Ended(Position.FromFen(fen), seen, _tables)!.Value;
        Assert.Equal(GameResult.WhiteWins, result);
        var (late, why) = GamePlayer.Ended(Position.FromFen(WithClock(fen, 90)), seen, _tables)!.Value;
        Assert.Equal(GameResult.Draw, late);
        Assert.Contains("50-move rule", why);
    }

    /// <summary>A cursed win: K+B+B v K+N won with best play, drawn by the rule even with the count at 0.</summary>
    [SlowFact]
    public void ACursedWinIsPlayedAndAdjudicatedAsADraw()
    {
        using var tables = new Tablebase(Environment.GetEnvironmentVariable(SlowFactAttribute.Variable)) { SolveMissing = false };
        var mate = tables.Get(Material.Parse("KBBvKN"));
        var dtz = tables.GetDtz(Material.Parse("KBBvKN"));
        Position? cursed = null;
        for (long i = 0; i < mate.Size && cursed is null; i += 101)
            if (mate[i] is { Kind: OutcomeKind.Win } && dtz[i] is { Kind: OutcomeKind.Draw })
                cursed = mate.PositionAt(i);
        Assert.NotNull(cursed);

        var result = new Search(new TranspositionTable(8), tables).Run(cursed!, new SearchLimits { Depth = 1 });
        Assert.Equal(0, result.Score);
        var (outcome, why) = GamePlayer.Ended(cursed!, new Dictionary<ulong, int>(), tables)!.Value;
        Assert.Equal(GameResult.Draw, outcome);
        Assert.Contains("50-move rule", why);
    }
}

public class UciTests
{
    private static (UciEngine Engine, StringWriter Output) Start()
    {
        var output = new StringWriter();
        return (new UciEngine(output), output);
    }

    [Fact]
    public void IdentifiesItself()
    {
        var (engine, output) = Start();
        engine.Handle("uci");
        engine.Handle("isready");
        string text = output.ToString();
        Assert.Contains("id name ChessBruteforcer", text);
        Assert.Contains("uciok", text);
        Assert.Contains("readyok", text);
    }

    [Fact]
    public void PlaysALegalMoveFromAGivenGame()
    {
        var (engine, output) = Start();
        engine.Handle("position startpos moves e2e4 e7e5 g1f3");
        Assert.Equal(Colour.Black, engine.Position.SideToMove);
        engine.Handle("go depth 4");
        engine.WaitForSearch();
        string bestLine = output.ToString().Split('\n').Last(l => l.StartsWith("bestmove"));
        string move = bestLine.Split(' ')[1].Trim();
        Assert.NotNull(engine.Position.ParseUciMove(move));
        Assert.Contains("info depth 4", output.ToString());
    }

    [Fact]
    public void AcceptsFenPositionsAndReportsMates()
    {
        var (engine, output) = Start();
        engine.Handle("position fen 6k1/5ppp/8/8/8/8/5PPP/R5K1 w - - 0 1");
        engine.Handle("go depth 3");
        engine.WaitForSearch();
        string text = output.ToString();
        Assert.Contains("score mate 1", text);
        Assert.Contains("bestmove a1a8", text);
    }

    [Fact]
    public void StopEndsAnInfiniteSearch()
    {
        var (engine, output) = Start();
        engine.Handle("position startpos");
        engine.Handle("go infinite");
        Thread.Sleep(200);
        engine.Handle("stop");
        Assert.Contains("bestmove", output.ToString());
    }

    [Fact]
    public void MovetimeIsRespected()
    {
        var (engine, output) = Start();
        engine.Handle("position startpos");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        engine.Handle("go movetime 300");
        engine.WaitForSearch();
        Assert.InRange(clock.ElapsedMilliseconds, 250, 1500);
        Assert.Contains("bestmove", output.ToString());
    }

    [Fact]
    public void IllegalMovesAreReportedNotFatal()
    {
        var (engine, output) = Start();
        engine.Handle("position startpos moves e2e5");
        Assert.Contains("info string error", output.ToString());
        Assert.True(engine.Handle("isready"));
    }
}
