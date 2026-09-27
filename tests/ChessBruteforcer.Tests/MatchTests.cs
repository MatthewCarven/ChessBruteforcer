using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Game;
using ChessBruteforcer.Core.Match;

namespace ChessBruteforcer.Tests;

public class SanTests
{
    [Theory]
    [InlineData(Fen.StartPosition, "e2e4", "e4")]
    [InlineData(Fen.StartPosition, "g1f3", "Nf3")]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", "e1g1", "O-O")]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", "e1c1", "O-O-O")]
    [InlineData("rnbqkbnr/ppp1pppp/8/3p4/4P3/8/PPPP1PPP/RNBQKBNR w KQkq d6 0 2", "e4d5", "exd5")]
    [InlineData("8/P6k/8/8/8/8/8/K7 w - - 0 1", "a7a8q", "a8=Q")]
    [InlineData("8/P7/8/8/8/8/8/K6k w - - 0 1", "a7a8q", "a8=Q+")]          // checks h1 down the diagonal
    [InlineData("rnbqkbnr/pppp1ppp/8/4p3/6P1/5P2/PPPPP2P/RNBQKBNR b KQkq - 0 2", "d8h4", "Qh4#")]
    [InlineData("4k3/8/8/8/8/8/8/R4RK1 w - - 0 1", "a1d1", "Rad1")]          // both rooks reach d1: file
    [InlineData("4k3/8/8/8/8/8/8/R3K2R w - - 0 1", "a1d1", "Rd1")]           // the h1 rook is blocked by the king
    [InlineData("4k3/8/8/R7/8/8/8/R3K3 w - - 0 1", "a1a3", "R1a3")]          // same file: rank
    [InlineData("4k3/8/8/8/8/2N3N1/8/2N1K3 w - - 0 1", "c3e2", "Nc3e2")]      // needs both
    [InlineData("4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1", "e5d6", "exd6")]         // en passant
    public void MovesAreWrittenInStandardNotation(string fen, string uci, string san)
    {
        var position = Position.FromFen(fen);
        var move = position.ParseUciMove(uci)!.Value;
        Assert.Equal(san, San.Of(position, move));
        Assert.Equal(fen.Split(' ')[0], position.ToFen().Split(' ')[0]);   // unchanged
    }

    [Fact]
    public void PgnHasTagsNumberedMovesAndTheResult()
    {
        var record = new GameRecord("A", "B", Fen.StartPosition, new[] { "f2f3", "e7e5", "g2g4", "d8h4" },
                                    GameResult.BlackWins, "checkmate", 3, "10+0.1");
        string pgn = record.ToPgn("Test", new DateTime(2026, 9, 27));
        Assert.Contains("[White \"A\"]", pgn);
        Assert.Contains("[Result \"0-1\"]", pgn);
        Assert.Contains("[Date \"2026.09.27\"]", pgn);
        Assert.Contains("1. f3 e5 2. g4 Qh4# 0-1", pgn);
        Assert.DoesNotContain("[FEN", pgn);
    }
}

public class MatchStatsTests
{
    [Fact]
    public void EvenScoreIsZeroElo()
    {
        var stats = new MatchStats(10, 10, 20);
        Assert.Equal(0.5, stats.Score);
        Assert.Equal(0, stats.Elo, 6);
        Assert.Equal(0.5, stats.LikelihoodOfSuperiority, 6);
    }

    [Fact]
    public void SeventyFivePercentIsAbout191Elo()
    {
        Assert.Equal(190.85, MatchStats.EloFromScore(0.75), 1);
        Assert.Equal(0.75, MatchStats.ScoreFromElo(190.85), 3);
    }

    [Fact]
    public void MoreGamesNarrowTheInterval()
    {
        var few = new MatchStats(6, 4, 10);
        var many = new MatchStats(60, 40, 100);
        Assert.Equal(few.Elo, many.Elo, 6);
        Assert.True(many.EloInterval.High - many.EloInterval.Low < few.EloInterval.High - few.EloInterval.Low);
        Assert.True(many.LikelihoodOfSuperiority > few.LikelihoodOfSuperiority);
    }

    [Fact]
    public void SprtLeansTheRightWay()
    {
        var winning = new MatchStats(300, 200, 500);
        var losing = new MatchStats(200, 300, 500);
        Assert.True(winning.LogLikelihoodRatio(0, 10) > MatchStats.SprtBounds().Upper);
        Assert.True(losing.LogLikelihoodRatio(0, 10) < MatchStats.SprtBounds().Lower);
    }

    [Theory]
    [InlineData("10+0.1", 10_000, 100, null)]
    [InlineData("60", 60_000, 0, null)]
    [InlineData("movetime=250", 0, 0, 250)]
    public void TimeControlsParse(string text, int baseMs, int incMs, int? moveTime)
    {
        Assert.Equal(new TimeControl(baseMs, incMs, moveTime), TimeControl.Parse(text));
    }
}

public class GamePlayerTests
{
    /// <summary>Plays moves from a script, then the first legal move (or whatever it is told to answer).</summary>
    private sealed class ScriptedPlayer : IPlayer
    {
        private readonly Queue<string> _script;
        private readonly int _delayMs;

        public ScriptedPlayer(string name, IEnumerable<string>? script = null, int delayMs = 0)
        {
            Name = name;
            _script = new Queue<string>(script ?? Array.Empty<string>());
            _delayMs = delayMs;
        }

        public string Name { get; }
        public int Games { get; private set; }

        public void NewGame() => Games++;

        public string? ChooseMove(MoveRequest request, TimeSpan timeout)
        {
            if (_delayMs > 0)
                Thread.Sleep(_delayMs);
            if (_script.TryDequeue(out string? scripted))
                return scripted;
            return MoveGenerator.Legal(request.Position).Select(m => m.ToUci()).Order().First();
        }

        public void Dispose() { }
    }

    private static readonly TimeControl Clock = new(60_000, 0);

    [Fact]
    public void CheckmateEndsTheGame()
    {
        var white = new ScriptedPlayer("W", new[] { "f2f3", "g2g4" });
        var black = new ScriptedPlayer("B", new[] { "e7e5", "d8h4" });
        var record = GamePlayer.Play(white, black, Array.Empty<string>(), Clock, 1);
        Assert.Equal(GameResult.BlackWins, record.Result);
        Assert.Equal("checkmate", record.Termination);
        Assert.Equal(4, record.Moves.Count);
        Assert.Equal(1, white.Games);
    }

    [Fact]
    public void AnIllegalMoveLoses()
    {
        var record = GamePlayer.Play(new ScriptedPlayer("W", new[] { "e2e5" }), new ScriptedPlayer("B"),
                                     Array.Empty<string>(), Clock, 1);
        Assert.Equal(GameResult.BlackWins, record.Result);
        Assert.Contains("illegal", record.Termination);
    }

    [Fact]
    public void RunningOutOfTimeLoses()
    {
        var record = GamePlayer.Play(new ScriptedPlayer("W"), new ScriptedPlayer("B", delayMs: 400),
                                     Array.Empty<string>(), new TimeControl(200, 0), 1);
        Assert.Equal(GameResult.WhiteWins, record.Result);
        Assert.Contains("lost on time", record.Termination);
    }

    [Fact]
    public void KnightsShufflingBackAndForthIsThreefoldRepetition()
    {
        var shuffle = new[] { "g1f3", "f3g1", "g1f3", "f3g1" };
        var back = new[] { "g8f6", "f6g8", "g8f6", "f6g8" };
        var record = GamePlayer.Play(new ScriptedPlayer("W", shuffle), new ScriptedPlayer("B", back),
                                     Array.Empty<string>(), Clock, 1);
        Assert.Equal(GameResult.Draw, record.Result);
        Assert.Equal("threefold repetition", record.Termination);
        Assert.Equal(8, record.Moves.Count);
    }

    [Fact]
    public void BareKingsAreADraw()
    {
        var record = GamePlayer.Play(new ScriptedPlayer("W", new[] { "e1d2" }), new ScriptedPlayer("B", new[] { "e8d7" }),
                                     Array.Empty<string>(), Clock, 1, startFen: "4k3/8/8/8/8/8/8/4K3 w - - 0 1");
        Assert.Equal(GameResult.Draw, record.Result);
        Assert.Equal("insufficient material", record.Termination);
        Assert.Empty(record.Moves);
    }

    [Fact]
    public void OpeningsArePlayedBeforeThePlayersStart()
    {
        var white = new ScriptedPlayer("W", new[] { "g2g4" });
        var black = new ScriptedPlayer("B", new[] { "d8h4" });
        var record = GamePlayer.Play(white, black, new[] { "f2f3", "e7e5" }, Clock, 1);
        Assert.Equal(new[] { "f2f3", "e7e5", "g2g4", "d8h4" }, record.Moves);
        Assert.Equal(GameResult.BlackWins, record.Result);
    }

    [Fact]
    public void EveryBuiltInOpeningIsLegal()
    {
        foreach (var opening in MatchRunner.DefaultOpenings)
        {
            var position = Position.Start();
            foreach (string uci in opening)
            {
                var move = position.ParseUciMove(uci);
                Assert.True(move is not null, $"{string.Join(' ', opening)}: {uci} is illegal");
                position.MakeMove(move!.Value);
            }
        }
    }
}
