using System.Diagnostics;
using System.Text;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Tests;

/// <summary>Thinking on the opponent's time: "go ponder", "ponderhit", and the reply named in "bestmove ... ponder".</summary>
public class PonderTests
{
    /// <summary>The engine's output, readable while the search thread writes it.</summary>
    private sealed class Output : TextWriter
    {
        private readonly StringBuilder _text = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { lock (_text) _text.Append(value); }
        public override void Write(string? value) { lock (_text) _text.Append(value); }
        public override void Write(char[] buffer, int index, int count) { lock (_text) _text.Append(buffer, index, count); }

        public string[] Lines()
        {
            lock (_text)
                return _text.ToString().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        }

        public string[] BestMoves() => Lines().Where(l => l.StartsWith("bestmove")).ToArray();

        /// <summary>The "info depth" lines before the <paramref name="count"/>th "bestmove" (and after the one before it).</summary>
        public Info[] InfosBefore(int count)
        {
            var round = Lines().TakeWhile(l => !l.StartsWith("bestmove") || --count > 0).ToList();
            int start = round.FindLastIndex(l => l.StartsWith("bestmove")) + 1;
            return round.Skip(start).Where(l => l.StartsWith("info depth")).Select(Info.Parse).ToArray();
        }

        /// <summary>The <paramref name="count"/>th "bestmove" line, waiting up to <paramref name="ms"/> for it; null if it never came.</summary>
        public string? WaitForBestMove(int count, int ms)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var lines = BestMoves();
                if (lines.Length >= count)
                    return lines[count - 1];
                if (clock.ElapsedMilliseconds > ms)
                    return null;
                Thread.Sleep(1);
            }
        }
    }

    /// <summary>What an "info depth" line says.</summary>
    private sealed record Info(int Depth, string Score, long Nodes, long TimeMs, string Pv)
    {
        public static Info Parse(string line)
        {
            string[] words = line.Split(' ');
            string After(string word) => words[Array.IndexOf(words, word) + 1];
            int score = Array.IndexOf(words, "score");
            return new Info(int.Parse(After("depth")), $"{words[score + 1]} {words[score + 2]}", long.Parse(After("nodes")),
                            long.Parse(After("time")), line[(line.IndexOf(" pv ") + 4)..]);
        }
    }

    /// <summary>No depth was reported twice: nothing was sent again after the search's own lines.</summary>
    private static void AssertNoRepeat(Info[] infos)
    {
        Assert.NotEmpty(infos);
        Assert.Equal(infos.Length, infos.Select(i => i.Depth).Distinct().Count());
    }

    private static (UciEngine Engine, Output Output) Start(int threads = 1, string split = "LazySMP")
    {
        var output = new Output();
        var engine = new UciEngine(output);
        engine.Handle($"setoption name Threads value {threads}");
        engine.Handle($"setoption name ThreadSplit value {split}");
        return (engine, output);
    }

    // As lichess-bot sends it: the game after our move (Nf3) and the reply we expect (Nc6), White to move.
    private const string Pondered = "position startpos moves e2e4 e7e5 g1f3 b8c6";

    // 3 s on our clock: about 100 ms planned for the move and 294 ms at most (Search's TimeBudget, less
    // the 50 ms Move Overhead: 2,950 / 30 = 98, the most three times that).
    private const string Clock = "wtime 3000 btime 3000";
    private const int HardLimitMs = 294;

    private static void AssertLegal(Position position, string bestLine)
    {
        string[] words = bestLine.Split(' ');
        var after = Position.FromFen(position.ToFen());
        var ours = after.ParseUciMove(words[1]);
        Assert.NotNull(ours);
        if (words.Length > 2)
        {
            Assert.Equal(4, words.Length);
            Assert.Equal("ponder", words[2]);
            after.MakeMove(ours!.Value);
            Assert.NotNull(after.ParseUciMove(words[3]));
        }
    }

    [Fact]
    public void UciOffersPonderOffByDefault()
    {
        var (engine, output) = Start();
        engine.Handle("uci");
        Assert.Contains("option name Ponder type check default false", output.Lines());
        engine.Handle("setoption name Ponder value true");
        engine.Handle("setoption name Ponder value false");
        Assert.DoesNotContain(output.Lines(), l => l.StartsWith("info string"));
    }

    [Theory]
    [InlineData(1, "LazySMP")]
    [InlineData(4, "LazySMP")]
    [InlineData(4, "Ranges")]
    public void PonderThenStopGivesABestmoveAtOnce(int threads, string split)
    {
        var (engine, output) = Start(threads, split);
        engine.Handle(Pondered);
        engine.Handle($"go ponder {Clock}");
        Thread.Sleep(HardLimitMs + 300);
        Assert.Empty(output.BestMoves());   // past the clock's limit, and still thinking: it isn't our move yet

        var clock = Stopwatch.StartNew();
        engine.Handle("stop");   // (returns once the search has answered)
        Assert.InRange(clock.ElapsedMilliseconds, 0, 300);
        AssertLegal(engine.Position, Assert.Single(output.BestMoves()));
        AssertNoRepeat(output.InfosBefore(1));   // (the line a ponder hit may send again is for hits only)
    }

    [Theory]
    [InlineData(1, "LazySMP")]
    [InlineData(4, "LazySMP")]
    [InlineData(4, "Ranges")]
    public void PonderThenPonderhitAnswersWithinTheClock(int threads, string split)
    {
        var (engine, output) = Start(threads, split);
        engine.Handle(Pondered);

        // The opponent replied at once: the search has barely begun, and the clock decides.
        engine.Handle($"go ponder {Clock}");
        var clock = Stopwatch.StartNew();
        engine.Handle("ponderhit");
        string? best = output.WaitForBestMove(1, 5000);
        Assert.NotNull(best);
        Assert.InRange(clock.ElapsedMilliseconds, 0, HardLimitMs + 300);
        AssertLegal(engine.Position, best!);

        // The opponent took longer than our whole budget: what pondering found is the answer, at once.
        engine.Handle($"go ponder {Clock}");
        Thread.Sleep(HardLimitMs + 300);
        clock.Restart();
        engine.Handle("ponderhit");
        best = output.WaitForBestMove(2, 5000);
        Assert.NotNull(best);
        Assert.InRange(clock.ElapsedMilliseconds, 0, 200);
        AssertLegal(engine.Position, best!);
    }

    // python-chess keeps only the "info" lines that come after "ponderhit" (lichess-bot logs the move's stats from
    // them), so a hit that answers with no new depth finished sends its last line again.

    [Theory]
    [InlineData(1, "LazySMP")]
    [InlineData(4, "LazySMP")]
    [InlineData(4, "Ranges")]
    public void APonderHitAnsweredAtOnceSendsItsLastInfoAgain(int threads, string split)
    {
        var (engine, output) = Start(threads, split);
        for (int round = 1; round <= 2; round++)   // (twice: one search's hit mustn't count for the next)
        {
            engine.Handle(Pondered);
            engine.Handle($"go ponder {Clock}");
            Thread.Sleep(HardLimitMs + 300);   // past the whole budget, so the hit answers at once, with no new depth
            engine.Handle("ponderhit");
            Assert.NotNull(output.WaitForBestMove(round, 5000));

            var infos = output.InfosBefore(round);
            Assert.True(infos.Length >= 2, $"{infos.Length} info lines");
            var (pondered, sentAgain) = (infos[^2], infos[^1]);
            Assert.Equal(pondered.Depth, sentAgain.Depth);
            Assert.Equal(pondered.Score, sentAgain.Score);
            Assert.Equal(pondered.Pv, sentAgain.Pv);
            Assert.True(sentAgain.Nodes > pondered.Nodes, $"nodes {sentAgain.Nodes}, {pondered.Nodes} at depth {pondered.Depth}");
            Assert.True(sentAgain.TimeMs >= HardLimitMs + 300, $"time {sentAgain.TimeMs}: not counted from go ponder");
            AssertNoRepeat(infos[..^1]);
        }
    }

    [Theory]
    [InlineData(1, "LazySMP")]
    [InlineData(4, "LazySMP")]
    [InlineData(4, "Ranges")]
    public void APonderHitThatFinishesADepthSendsNothingAgain(int threads, string split)
    {
        var (engine, output) = Start(threads, split);
        engine.Handle(Pondered);
        // 10 s on the clock: about 330 ms planned for the move.  Hit at once, so depths go on finishing after it.
        engine.Handle("go ponder wtime 10000 btime 10000");
        engine.Handle("ponderhit");
        Assert.NotNull(output.WaitForBestMove(1, 5000));
        AssertNoRepeat(output.InfosBefore(1));
    }

    [Fact]
    public void ATableMoveFoundWhilePonderingIsSentAgainAfterTheHit()
    {
        string folder = Directory.CreateTempSubdirectory("cbc-ponder").FullName;
        new Tablebase().Get(Material.Parse("KRvK")).SaveCompressed(Path.Combine(folder, "KRvK.cbt"));
        var (engine, output) = Start();
        try
        {
            engine.Handle($"setoption name EndgameTables value {folder}");
            engine.Handle("position fen 8/8/8/4k3/8/8/8/R3K3 w - - 0 1");
            engine.Handle($"go ponder {Clock}");
            Thread.Sleep(200);   // the tables' move comes at once, as depth 1, and the answer waits for the opponent
            Assert.Empty(output.BestMoves());
            engine.Handle("ponderhit");
            Assert.NotNull(output.WaitForBestMove(1, 2000));

            var infos = output.InfosBefore(1);
            Assert.Equal(2, infos.Length);
            Assert.All(infos, i => Assert.Equal(1, i.Depth));
            Assert.Equal(infos[0] with { TimeMs = 0 }, infos[1] with { TimeMs = 0 });
            Assert.True(infos[1].TimeMs >= 200, $"time {infos[1].TimeMs}");
        }
        finally
        {
            engine.Handle("setoption name EndgameTables value <empty>");   // (lets go of the file, so the folder can be deleted)
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void APonderSearchThatEndsOnItsOwnStillWaitsForTheOpponent()
    {
        var (engine, output) = Start();
        engine.Handle("position fen 6k1/5ppp/8/8/8/8/5PPP/R5K1 w - - 0 1");
        engine.Handle("go ponder depth 3");   // mate in one, seen in a millisecond
        Thread.Sleep(200);
        Assert.Empty(output.BestMoves());
        engine.Handle("ponderhit");
        Assert.Equal("bestmove a1a8", output.WaitForBestMove(1, 2000));   // (mate: no reply to ponder on)
    }

    [Theory]
    [InlineData(1, "LazySMP")]
    [InlineData(4, "LazySMP")]
    [InlineData(4, "Ranges")]
    public void BestmoveNamesALegalPonderMove(int threads, string split)
    {
        var (engine, output) = Start(threads, split);
        engine.Handle("position startpos moves e2e4 e7e5 g1f3");
        engine.Handle("go depth 7");
        engine.WaitForSearch();
        string best = Assert.Single(output.BestMoves());
        Assert.Contains(" ponder ", best);
        AssertLegal(engine.Position, best);
    }

    [Fact]
    public void WhenTheLineStopsAtOurMoveTheTableNamesTheReply()
    {
        var (engine, output) = Start();
        engine.Handle("position startpos moves e2e4 e7e5 g1f3");
        engine.Handle("go depth 6");
        engine.WaitForSearch();
        engine.Handle("go depth 1");   // a line of one move, but the table remembers the replies from depth 6
        engine.WaitForSearch();
        string lastInfo = output.Lines().Last(l => l.StartsWith("info depth"));
        Assert.Single(lastInfo.Split(" pv ")[1].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        string best = output.BestMoves()[1];
        Assert.Contains(" ponder ", best);
        AssertLegal(engine.Position, best);
    }

    [Fact]
    public void APonderHitKeepsTheDepthPonderingReached()
    {
        var limits = new SearchLimits { WhiteTimeMs = 3000, BlackTimeMs = 3000 };
        int ownTime = new Search(new TranspositionTable(8)).Run(Position.FromFen(MoveGeneratorTests.Kiwipete), limits).Depth;

        var search = new Search(new TranspositionTable(8));
        using var started = new ManualResetEventSlim();
        SearchResult? pondered = null;
        var thread = new Thread(() => pondered = search.Run(Position.FromFen(MoveGeneratorTests.Kiwipete),
                                                            limits with { Ponder = true }, onStarted: started.Set))
        {
            IsBackground = true,
        };
        thread.Start();
        Assert.True(started.Wait(5000));
        Thread.Sleep(1500);   // five times the most the clock allows
        var clock = Stopwatch.StartNew();
        search.PonderHit();
        bool answered = thread.Join(2000);
        if (!answered)
            search.Stop();
        Assert.True(answered, "no answer after ponderhit");
        Assert.InRange(clock.ElapsedMilliseconds, 0, 200);
        Assert.True(pondered!.Depth > ownTime, $"depth {pondered.Depth} pondering, {ownTime} on our own time");
    }
}
