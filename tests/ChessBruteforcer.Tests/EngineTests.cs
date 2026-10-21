using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Game;
using ChessBruteforcer.Core.Match;
using ChessBruteforcer.Core.Records;

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

    // Quiescence generates only captures when not in check, every evasion when in check, and takes the
    // check status Negamax hands it at depth 0; these three would each see a mate that isn't there, or
    // miss one that is.

    [Fact]
    public void QuiescenceInCheckSearchesQuietEvasions()
    {
        // White is in check from the rook and can only step aside (Kg2, Kh2): no capture escapes, no mate.
        int score = new Search(new TranspositionTable(1)).QuiescenceScore(Position.FromFen("7k/8/8/8/8/8/8/r6K w - - 0 1"));
        Assert.True(score > -Search.MateBound, $"quiescence scored {score}: a mate");
    }

    [Fact]
    public void QuiescenceSeesACaptureThatMates()
    {
        // Qxf7 is a capture, and mate: quiescence must find Black in check with no move.
        int score = new Search(new TranspositionTable(1)).QuiescenceScore(
            Position.FromFen("r1bqkb1r/pppp1ppp/2n2n2/4p2Q/2B1P3/8/PPPP1PPP/RNB1K1NR w KQkq - 4 4"));
        Assert.Equal(1, Search.MateIn(score));
    }

    [Fact]
    public void AStalemateAtTheHorizonIsNotMate()
    {
        // Kc7 stalemates Black; a depth-1 search only stands pat there, and must not take it for mate.
        var result = Think("k7/8/1PK5/8/8/8/8/8 w - - 0 1", 1);
        Assert.Null(Search.MateIn(result.Score));
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

    // The repetition rule (TURNS.md, "The false draw"): Stockfish's, a draw if the position came before inside
    // the search, or on its third time.  The old rule called any position seen once before a draw, the game
    // before the search included, so a losing side "drew" by going back to where it was a few moves ago.

    /// <summary>
    /// K+Q v K.  After <see cref="QueenUpMoves"/> (Qd1+ Ke8, then the queen d1-d2-d1 and the king e8-f8), Black is
    /// to move, and Kf8-e8 brings back the position after Ke8, the game's third (not its first, so a search that
    /// lost track of where the root is in the game's list would be caught).
    /// </summary>
    private const string QueenUp = "3k4/8/8/8/8/8/8/2Q1K3 w - - 0 1";
    private const string QueenUpMoves = "c1d1 d8e8 d1d2 e8f8 d2d1";

    /// <summary>White to move, a rook and a pawn down, with a perpetual check: Qe8+ Kh7 Qh5+ Kg8, again and again.</summary>
    private const string Perpetual = "6k1/6p1/8/7Q/8/6K1/r7/q7 w - - 0 1";

    /// <summary>The position after <paramref name="moves"/> from <paramref name="fen"/>, and the game's positions before it.</summary>
    private static (Position Root, List<ulong> Earlier) Game(string fen, string moves)
    {
        var position = Position.FromFen(fen);
        var earlier = new List<ulong>();
        foreach (string uci in moves.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            earlier.Add(position.Hash);
            position.MakeMove(position.ParseUciMove(uci) ?? throw new ArgumentException($"Illegal move {uci}."));
        }
        return (position, earlier);
    }

    private static SearchResult Think(Position root, List<ulong> earlier, int depth, int threads = 1, bool twofold = false) =>
        new Search(new TranspositionTable(8)) { Threads = threads, TwofoldRepetitions = twofold }
            .Run(root, new SearchLimits { Depth = depth }, earlier);

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void APositionSeenOnceBeforeInTheGameIsNotADrawYet(int threads)
    {
        // Kf8-e8 makes the position after Ke8 come round a second time.  White just plays on, so Black is still
        // a queen down; the old rule scored it 0 and played it.
        var (root, earlier) = Game(QueenUp, QueenUpMoves);
        var result = Think(root, earlier, 6, threads);
        Assert.True(result.Score < -500, $"scored {result.Score} with {result.BestMove?.ToUci()}");

        var old = Think(root, earlier, 6, threads, twofold: true);
        Assert.Equal(0, old.Score);
        Assert.Equal("f8e8", old.BestMove?.ToUci());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ThePositionsThirdTimeIsADraw(int threads)
    {
        // The same dance twice: Kf8-e8 now makes that position's third time, a draw Black can take.
        var (root, earlier) = Game(QueenUp, QueenUpMoves + " f8e8 d1d2 e8f8 d2d1");
        var result = Think(root, earlier, 6, threads);
        Assert.Equal(0, result.Score);
        Assert.Equal("f8e8", result.BestMove?.ToUci());
    }

    [Theory]
    [InlineData("", 3, 1)]
    [InlineData("", 3, 4)]
    [InlineData("h5e8", 2, 1)]
    [InlineData("h5e8", 2, 4)]
    public void ARepetitionInsideTheSearchIsADraw(string moves, int depth, int threads)
    {
        // Qe8+ Kh7 Qh5+ Kg8 (the root again: not a draw yet) Qe8+, the first check's position a second time,
        // inside the search: a draw (ply 5).  At depth 3 the search reaches that, but not the root's third time
        // (ply 8), which would be a draw by any rule.  And from Black's side, after Qe8+ (in check at the root,
        // so a ply deeper): Kh7 is forced and the checks go round, Kh7's position a second time at ply 5.
        // (Each needs the search to look past the root's return, not take the root's table entry for it.)
        var (root, earlier) = Game(Perpetual, moves);
        var result = Think(root, earlier, depth, threads);
        Assert.Equal(0, result.Score);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HelpersSearchByTheRuleOfTheSearchTheyWorkFor(bool twofold)
    {
        var (root, earlier) = Game(QueenUp, QueenUpMoves);
        var search = new Search(new TranspositionTable(8)) { Threads = 4, TwofoldRepetitions = twofold };
        search.Run(root, new SearchLimits { Depth = 4 }, earlier);
        Assert.Equal(new[] { twofold, twofold, twofold }, search.HelperRules);
    }

    [Fact]
    public void ATableEntryStoredBeforeAPositionCameRoundAgainIsNotTrusted()
    {
        // White, a queen up, and both kings shuffling: Kb1-a1 now brings back a position the game has had once
        // (Black to move), and from there Kh8 makes another position's third time, a draw.  An entry for that
        // position stored before it came round (here a made-up one: Black lost by far) mustn't stand in for
        // searching it, or Kb1-a1 looks best and lets Black draw.  (In the A/B match the new rule's leads were
        // thrown away like this: in 16 of 25 replayed, it played the move that let the other side repeat.)
        const string start = "7k/8/8/8/8/3Q4/8/K7 w - - 0 1", shuffle = "a1b1 h8g8 b1a1 g8h8 a1b1 h8g8";
        var (root, earlier) = Game(start, shuffle);
        var table = new TranspositionTable(8);
        table.Store(Game(start, shuffle + " b1a1").Root.Hash, default, -5000, 100, Bound.Exact);
        var result = new Search(table) { TwofoldRepetitions = false }.Run(root, new SearchLimits { Depth = 4 }, earlier);
        Assert.NotEqual("b1a1", result.BestMove?.ToUci());
        Assert.InRange(result.Score, 500, 4000);
    }

    [Fact]
    public void ComingBackToTheRootOnceIsNotADraw()
    {
        // At depth 2 the perpetual reaches Kg8, the root's position a second time (ply 4), but not the check
        // after it: the root was in the game before the search, so that alone is no draw, and White, a rook
        // down, has nothing better.  The old rule saw a draw there.
        var (root, earlier) = Game(Perpetual, "");
        var result = Think(root, earlier, 2);
        Assert.True(result.Score < -300, $"scored {result.Score} with {result.BestMove?.ToUci()}");
        Assert.Equal(0, Think(root, earlier, 2, twofold: true).Score);
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

/// <summary>Taking over real games where they enter a table (Matthew's early test, 2026-10-01).</summary>
public class TakeoverTests : IClassFixture<RookEndgame>
{
    private readonly Tablebase _tables;

    public TakeoverTests(RookEndgame rook) => _tables = rook.Tablebase;

    private static StoredGame FromFen(string fen, string moves) =>
        Pgn.Parse($"[SetUp \"1\"]\n[FEN \"{fen}\"]\n\n{moves} *")[0];

    [Fact]
    public void AGameIsTakenOverWhereItFirstReachesATableWeHave()
    {
        // K+R v K+P; the king takes the pawn (K+R v K), then one more move.
        var game = FromFen("4k3/8/8/8/8/8/3p4/R3K3 w - - 0 1", "1. Kxd2 Kd7");
        var point = Takeover.Find(game, m => m.ToString() == "KRvK")!;
        Assert.Equal(1, point.Ply);
        Assert.Equal(1, point.PliesLeft);
        Assert.Equal("KRvK", point.Material.ToString());
        Assert.Equal(2, point.Hashes.Count);                    // the start, and the takeover position
        Assert.Equal(point.Position.Hash, point.Hashes[^1]);
        Assert.Equal("KRvKP", Takeover.Find(game, _ => true)!.Material.ToString());   // with every table, at once
        Assert.Null(Takeover.Find(game, _ => false));
    }

    [Fact]
    public void CastlingRightsWaitUntilTheyAreGone()
    {
        var game = FromFen("4k3/8/8/8/8/8/8/R3K3 w Q - 0 1", "1. O-O-O Kf7 2. Rd7+");
        Assert.Equal(1, Takeover.Find(game, _ => true)!.Ply);   // tables assume no castling rights
    }

    [Fact]
    public void PlayedOutAWinIsMatedInExactlyTheTablesDistance()
    {
        var position = Position.FromFen("8/8/8/8/8/2k5/1R6/K7 w - - 0 1");   // mate in 16
        var (result, termination, plies) = Takeover.PlayOut(position, new[] { position.Hash }, _tables);
        Assert.Equal(GameResult.WhiteWins, result);
        Assert.Equal("checkmate", termination);
        Assert.Equal(31, plies);   // the winner as fast as it can go, the loser as slow
    }

    [Fact]
    public void TwoInProcessPlayersPlayATableEndingThroughTheGameLoop()
    {
        // As in a match: a start FEN, a clock, the rules deciding the end.
        using var white = new SearchPlayer("white", _tables);
        using var black = new SearchPlayer("black", _tables);
        var record = GamePlayer.Play(white, black, Array.Empty<string>(), TimeControl.Parse("movetime=50"), 1,
                                     startFen: "8/8/8/8/8/2k5/1R6/K7 w - - 0 1");
        Assert.Equal(GameResult.WhiteWins, record.Result);
        Assert.Equal("checkmate", record.Termination);
        Assert.Equal(31, record.Moves.Count);   // the table's distance: both sides played it perfectly
    }

    [Fact]
    public void PlayedOutFromALateClockTheRuleDrawsIt()
    {
        var position = Position.FromFen("8/8/8/8/8/2k5/1R6/K7 w - - 80 1");   // 31 plies of mate, 20 left on the count
        var (result, termination, plies) = Takeover.PlayOut(position, new[] { position.Hash }, _tables);
        Assert.Equal(GameResult.Draw, result);
        Assert.Equal("50-move rule", termination);
        Assert.Equal(20, plies);
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

    [Fact]
    public void TheGamesMovesCountForRepetitionsAndRepetitionRuleChoosesTheRule()
    {
        // Black, a queen down, can bring the position after Ke8 round again with Kf8-e8: no draw (Threefold,
        // the default), a draw by the old rule (Twofold).
        var (engine, output) = Start();
        (int Score, string Move) Go()
        {
            output.GetStringBuilder().Clear();
            engine.Handle("position fen 3k4/8/8/8/8/8/8/2Q1K3 w - - 0 1 moves c1d1 d8e8 d1d2 e8f8 d2d1");
            engine.Handle("go depth 5");
            engine.WaitForSearch();
            var lines = output.ToString().Split('\n');
            string info = lines.Last(l => l.StartsWith("info depth 5 "));
            var words = info.Split(' ');
            int score = int.Parse(words[Array.IndexOf(words, "cp") + 1]);
            return (score, lines.Last(l => l.StartsWith("bestmove")).Split(' ')[1].Trim());
        }

        engine.Handle("uci");
        Assert.Contains("option name RepetitionRule type combo default Threefold var Threefold var Twofold", output.ToString());
        Assert.True(Go().Score < -500);
        engine.Handle("setoption name RepetitionRule value Twofold");
        Assert.Equal((0, "f8e8"), Go());
        engine.Handle("setoption name RepetitionRule value Threefold");
        Assert.True(Go().Score < -500);
        engine.Handle("setoption name RepetitionRule value Sometimes");
        Assert.Contains("info string error: RepetitionRule is Threefold or Twofold", output.ToString());
    }
}
