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

        /// <summary>
        /// The "info depth" lines so far in the <paramref name="round"/>th search, once one of them matches
        /// <paramref name="found"/>, waiting up to <paramref name="ms"/> for it; null if none came.
        /// </summary>
        public Info[]? WaitForInfo(int round, Func<Info, bool> found, int ms)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var infos = InfosBefore(round);
                if (infos.Any(found))
                    return infos;
                if (clock.ElapsedMilliseconds > ms)
                    return null;
                Thread.Sleep(1);
            }
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

    private static (UciEngine Engine, Output Output) Start(int threads = 1, string split = "LazySMP", bool? ponderFinish = null,
                                                           bool? ponderCap = null)
    {
        var output = new Output();
        var engine = new UciEngine(output);
        engine.Handle($"setoption name Threads value {threads}");
        engine.Handle($"setoption name ThreadSplit value {split}");
        if (ponderFinish is bool finish)   // (otherwise the engine's default, off)
            engine.Handle($"setoption name PonderFinish value {(finish ? "true" : "false")}");
        if (ponderCap is bool cap)
            engine.Handle($"setoption name PonderCap value {(cap ? "true" : "false")}");
        return (engine, output);
    }

    // As lichess-bot sends it: the game after our move (Nf3) and the reply we expect (Nc6), White to move.
    private const string Pondered = "position startpos moves e2e4 e7e5 g1f3 b8c6";

    // 3 s on our clock: about 100 ms planned for the move and 294 ms at most (Search's TimeBudget, less
    // the 50 ms Move Overhead: 2,950 / 30 = 98, the most three times that).
    private const string Clock = "wtime 3000 btime 3000";
    private const int PlannedMs = 98;
    private const int HardLimitMs = 294;

    /// <summary>A clock on which the move's planned time is <paramref name="plannedMs"/> (and its most three times that).</summary>
    private static string ClockPlanning(int plannedMs) => $"wtime {30 * plannedMs + 50} btime {30 * plannedMs + 50}";

    // What a search may take beyond its limit to notice it, stop its threads and answer, on a busy machine.
    private const int Margin = 200;

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
        Assert.Contains("option name PonderFinish type check default false", output.Lines());
        Assert.Contains("option name PonderCap type check default false", output.Lines());
        engine.Handle("setoption name Ponder value true");
        engine.Handle("setoption name Ponder value false");
        engine.Handle("setoption name PonderFinish value false");
        engine.Handle("setoption name PonderFinish value true");
        engine.Handle("setoption name PonderCap value false");
        engine.Handle("setoption name PonderCap value true");
        Assert.DoesNotContain(output.Lines(), l => l.StartsWith("info string"));
        engine.Handle("setoption name PonderFinish value sometimes");
        Assert.Contains("info string error: PonderFinish is true or false, not sometimes.", output.Lines());
        engine.Handle("setoption name PonderCap value 2");
        Assert.Contains("info string error: PonderCap is true or false, not 2.", output.Lines());
    }

    [Theory]
    [InlineData(null, null, false, false)]
    [InlineData(true, null, true, false)]
    [InlineData(null, true, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, true, true)]
    public void PonderFinishAndPonderCapReachTheSearch(bool? finish, bool? cap, bool expectedFinish, bool expectedCap)
    {
        var (engine, _) = Start(ponderFinish: finish, ponderCap: cap);
        if (finish is not null || cap is not null)
        {
            // (each option set the other way first: the last value counts)
            engine.Handle($"setoption name PonderFinish value {(expectedFinish ? "false" : "true")}");
            engine.Handle($"setoption name PonderCap value {(expectedCap ? "false" : "true")}");
            engine.Handle($"setoption name PonderFinish value {(expectedFinish ? "true" : "false")}");
            engine.Handle($"setoption name PonderCap value {(expectedCap ? "true" : "false")}");
        }
        engine.Handle("position startpos");
        engine.Handle("go depth 1");
        engine.WaitForSearch();
        Assert.Equal((expectedFinish, expectedCap), (engine.Search.PonderFinish, engine.Search.PonderCap));
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

        // The opponent took longer than our whole budget: what pondering found is the answer, at once, or
        // within the move's planned time if the depth in progress finishes by then (PonderFinish).
        engine.Handle($"go ponder {Clock}");
        Thread.Sleep(HardLimitMs + 300);
        clock.Restart();
        engine.Handle("ponderhit");
        best = output.WaitForBestMove(2, 5000);
        Assert.NotNull(best);
        Assert.InRange(clock.ElapsedMilliseconds, 0, PlannedMs + Margin);
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
        // PonderFinish off, so the depth in progress is dropped and the hit answers at once, as before P2.  (With it
        // on, that depth may finish after the hit and send its own line; APonderHitLetsTheDepthItWouldDropFinish
        // checks the line sent again when it doesn't.)
        var (engine, output) = Start(threads, split, ponderFinish: false);
        for (int round = 1; round <= 2; round++)   // (twice: one search's hit mustn't count for the next)
        {
            engine.Handle(Pondered);
            engine.Handle($"go ponder {Clock}");
            Thread.Sleep(HardLimitMs + 300);   // past the whole budget, so the hit answers at once, with no new depth
            var clock = Stopwatch.StartNew();
            engine.Handle("ponderhit");
            Assert.NotNull(output.WaitForBestMove(round, 5000));
            Assert.InRange(clock.ElapsedMilliseconds, 0, 200);

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APonderHitKeepsTheDepthPonderingReached(bool ponderFinish)
    {
        var limits = new SearchLimits { WhiteTimeMs = 3000, BlackTimeMs = 3000 };
        int ownTime = new Search(new TranspositionTable(8)).Run(Position.FromFen(MoveGeneratorTests.Kiwipete), limits).Depth;

        var search = new Search(new TranspositionTable(8));
        Assert.False(search.PonderFinish || search.PonderCap);   // (P2's rules are off by default, as in the UCI engine)
        search.PonderFinish = ponderFinish;
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
        // Off, at once.  With PonderFinish, not quite: pondering is past the hard limit, so the depth in progress has
        // the move's planned time (98 ms here) from the hit to finish, and then the answer is that depth.
        Assert.InRange(clock.ElapsedMilliseconds, 0, (ponderFinish ? PlannedMs : 0) + Margin);
        Assert.True(pondered!.Depth > ownTime, $"depth {pondered.Depth} pondering, {ownTime} on our own time");
    }

    // P2 (PONDER.md): a hit lets the depth in progress finish, if it began past half the planned time S (or the hit
    // comes past the hard limit) and finishes within S of the hit; no new depth starts after it.  Anything else that
    // runs on after a hit stops by the hit + S, not at the hard limit, 3S from "go ponder".

    [Theory]
    // Both off: the rule before P2.  The hard limit stays; a depth begun past S/2 is stopped at once.
    [InlineData(500, 100, 1000, 3000, false, false, 3000, false)]
    [InlineData(600, 501, 1000, 3000, false, false, 3000, true)]
    [InlineData(600, 500, 1000, 3000, false, false, 3000, false)]   // (begun at S/2 exactly is not past it)
    [InlineData(3500, 400, 1000, 3000, false, false, 3000, false)]   // (past the hard limit: the clock check stops it)
    // Both on (P2 as first measured).  A depth begun past S/2 has S from the hit.
    [InlineData(600, 501, 1000, 3000, true, true, 1600, false)]
    [InlineData(2500, 2400, 1000, 3000, true, true, 3500, false)]   // (past the hard limit, if it must)
    [InlineData(4000, 3900, 1000, 3000, true, true, 5000, false)]
    // A hit past the hard limit gives the depth in progress S from the hit, however early it began.
    [InlineData(3500, 400, 1000, 3000, true, true, 4500, false)]
    [InlineData(3000, 400, 1000, 3000, true, true, 4000, false)]
    // A depth begun before S/2 goes on, as in a plain search, but stops by the hit + S or the hard limit.
    [InlineData(100, 50, 1000, 3000, true, true, 1100, false)]
    [InlineData(600, 500, 1000, 3000, true, true, 1600, false)]
    [InlineData(2500, 400, 1000, 3000, true, true, 3000, false)]
    // Finish only: the late depth as with both on; any other depth to the hard limit.
    [InlineData(600, 501, 1000, 3000, true, false, 1600, false)]
    [InlineData(3500, 400, 1000, 3000, true, false, 4500, false)]
    [InlineData(100, 50, 1000, 3000, true, false, 3000, false)]
    // Cap only: a late depth stopped at once; any other by the hit + S or the hard limit.
    [InlineData(600, 501, 1000, 3000, false, true, 1600, true)]
    [InlineData(100, 50, 1000, 3000, false, true, 1100, false)]
    [InlineData(3500, 400, 1000, 3000, false, true, 3000, false)]
    // No soft time (movetime): the hard limit, whatever the options.
    [InlineData(500, 400, 0, 2000, true, true, 2000, false)]
    [InlineData(2500, 1400, 0, 2000, true, true, 2000, false)]
    [InlineData(2500, 1400, 0, 2000, false, false, 2000, false)]
    public void APonderHitSetsTheLimitsP2Describes(long hitMs, long iterationStartMs, long softMs, long hardMs, bool finish,
                                                   bool cap, long expectedHardMs, bool expectedStop)
    {
        Assert.Equal((expectedHardMs, expectedStop),
                     Search.AfterPonderHit(hitMs, iterationStartMs, softMs, hardMs, finish, cap));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void PonderHitAppliesBothOptions(bool finish, bool cap)
    {
        long HardAfterHit(int clockMs, int ponderMs, out long hitFrom, out long hitTo)
        {
            var search = new Search(new TranspositionTable(8)) { PonderFinish = finish, PonderCap = cap };
            using var started = new ManualResetEventSlim();
            var thread = new Thread(() => search.Run(Position.FromFen(MoveGeneratorTests.Kiwipete),
                                                     new SearchLimits { WhiteTimeMs = clockMs, BlackTimeMs = clockMs, Ponder = true },
                                                     onStarted: started.Set)) { IsBackground = true };
            thread.Start();
            Assert.True(started.Wait(5000));
            Thread.Sleep(ponderMs);
            hitFrom = (long)search.Elapsed.TotalMilliseconds;
            search.PonderHit();
            hitTo = (long)Math.Ceiling(search.Elapsed.TotalMilliseconds);
            long hard = search.HardLimitMs;
            search.Stop();
            Assert.True(thread.Join(5000));
            return hard;
        }

        // A hit at once on 60 s (S 1,998 ms, H 5,994): the depth in progress began before S/2; the cap stops it by hit + S.
        long hard = HardAfterHit(60_000, 0, out long from, out long to);
        if (cap)
            Assert.InRange(hard, from + 1998, to + 1998);
        else
            Assert.Equal(5994, hard);

        // A hit past the hard limit on 3 s (S 98 ms, H 294): finish gives the depth in progress S from the hit.
        hard = HardAfterHit(3000, 400, out from, out to);
        if (finish)
            Assert.InRange(hard, from + 98, to + 98);
        else
            Assert.Equal(294, hard);
    }

    [Theory]
    [InlineData(1, "LazySMP", false)]
    [InlineData(4, "LazySMP", false)]
    [InlineData(4, "Ranges", false)]
    [InlineData(1, "LazySMP", true)]
    public void APonderHitLetsTheDepthItWouldDropFinish(int threads, string split, bool switchedOffAndOn)
    {
        var (engine, output) = Start(threads, split, ponderFinish: true, ponderCap: true);
        if (switchedOffAndOn)
        {
            engine.Handle("setoption name PonderFinish value false");
            engine.Handle("setoption name PonderFinish value true");
        }
        int finished = 0, rounds = 0;
        var log = new List<string>();
        for (int attempt = 1; finished == 0 && attempt <= 4; attempt++)   // (whether a depth can finish within S depends on the depth)
        {
            // First the depths' finish times from an empty table, pondering on a clock that limits nothing for 1.5 s.
            int profiled = ++rounds;
            engine.Handle("ucinewgame");
            engine.Handle(Pondered);
            engine.Handle($"go ponder {ClockPlanning(100_000)}");
            output.WaitForInfo(profiled, i => i.TimeMs > 1500, 30_000);
            engine.Handle("stop");
            var profile = output.InfosBefore(profiled);
            // S just under twice the time of a depth k whose next depth is quick for its time: depth k finishes past
            // S/2, so depth k + 1 begins past it, which the clock alone would never do, and has S from the hit.
            var quickest = profile.Zip(profile.Skip(1))
                                  .Where(p => p.First.TimeMs is >= 40 and <= 1500 && p.Second.Depth == p.First.Depth + 1)
                                  .OrderBy(p => (double)(p.Second.TimeMs - p.First.TimeMs) / p.First.TimeMs)
                                  .FirstOrDefault();
            int planned = quickest.First is null ? 400 : (int)(1.7 * quickest.First.TimeMs);

            int round = ++rounds;
            engine.Handle("ucinewgame");
            engine.Handle(Pondered);
            engine.Handle($"go ponder {ClockPlanning(planned)}");
            var sinceGo = Stopwatch.StartNew();
            // Once a depth finishes past S/2, the one after it (which the clock would never have begun) is in progress.
            string Lines() => $"round {round}, S {planned} ms: " + string.Join(" | ", output.InfosBefore(round).Select(i => $"{i.Depth} at {i.TimeMs}"));
            Assert.True(output.WaitForInfo(round, i => i.TimeMs > planned / 2, 30_000) is not null, $"no depth past S/2: {Lines()}");
            var before = output.InfosBefore(round);
            long hitMs = sinceGo.ElapsedMilliseconds;
            var clock = Stopwatch.StartNew();
            engine.Handle("ponderhit");
            string? best = output.WaitForBestMove(round, planned + 5000);
            Assert.True(best is not null, $"no answer, hit at {hitMs}: {Lines()}");
            Assert.True(clock.ElapsedMilliseconds <= planned + Margin, $"answered {clock.ElapsedMilliseconds} ms after the hit at {hitMs}: {Lines()}");

            var after = output.InfosBefore(round)[before.Length..];
            var lastBefore = before[^1];
            log.Add($"{Lines()}, hit at {hitMs} after {before.Length}");
            var line = Assert.Single(after);   // one depth at most, and the line sent again (P1) only if none
            if (line.Depth == lastBefore.Depth + 1)
            {
                // It finished within S of the hit, and it's the move played.
                finished++;
                Assert.InRange(line.TimeMs, 0, hitMs + planned + Margin);
                Assert.Equal(line.Pv.Split(' ')[0], best!.Split(' ')[1]);
            }
            else
            {
                // It didn't: stopped by the hit + S, and the depth before is played, its line sent again.
                Assert.Equal((lastBefore.Depth, lastBefore.Score, lastBefore.Pv), (line.Depth, line.Score, line.Pv));
                Assert.Equal(lastBefore.Pv.Split(' ')[0], best!.Split(' ')[1]);
            }
        }
        Assert.True(finished > 0, $"no depth finished after the hit in {rounds} rounds: {string.Join("\n", log)}");
    }

    [Theory]
    [InlineData(1, "LazySMP")]
    [InlineData(4, "LazySMP")]
    [InlineData(4, "Ranges")]
    public void APonderHitAnswersWithinThePlannedTime(int threads, string split)
    {
        const int planned = 400;   // the hard limit 1,200 ms from "go ponder"
        var (engine, output) = Start(threads, split, ponderFinish: true, ponderCap: true);
        int round = 0;
        foreach (int ponderMs in new[] { 0, 360, 1400 })   // at once (a plain search could run to 1,200), at 0.9 S, past the hard limit
        {
            round++;
            engine.Handle(Pondered);
            engine.Handle($"go ponder {ClockPlanning(planned)}");
            Thread.Sleep(ponderMs);
            var clock = Stopwatch.StartNew();
            engine.Handle("ponderhit");
            string? best = output.WaitForBestMove(round, 5000);
            Assert.NotNull(best);
            Assert.InRange(clock.ElapsedMilliseconds, 0, planned + Margin);
            AssertLegal(engine.Position, best!);
        }
    }

    [Theory]
    [InlineData(1, "LazySMP")]
    [InlineData(4, "LazySMP")]
    [InlineData(4, "Ranges")]
    public void AHitAtOnceStopsAsAPlainSearchWouldNotAtTheCap(int threads, string split)
    {
        // No depth begins past S/2, so the answer comes when the depth begun before it finishes: often well before
        // the hit + S, which only cuts a depth still running then.
        var (engine, output) = Start(threads, split, ponderFinish: true, ponderCap: true);
        var log = new List<string>();
        bool early = false;
        for (int round = 1; !early && round <= 6; round++)
        {
            int planned = 300 << ((round - 1) % 3);   // 300, 600, 1200 ms
            engine.Handle("ucinewgame");
            engine.Handle(Pondered);
            engine.Handle($"go ponder {ClockPlanning(planned)}");
            var clock = Stopwatch.StartNew();
            engine.Handle("ponderhit");
            Assert.NotNull(output.WaitForBestMove(round, planned + 5000));
            long answeredMs = clock.ElapsedMilliseconds;
            var infos = output.InfosBefore(round);
            log.Add($"S {planned} ms, answered at {answeredMs}: " + string.Join(" | ", infos.Select(i => $"{i.Depth} at {i.TimeMs}")));
            Assert.True(answeredMs <= planned + Margin, string.Join("\n", log));
            // each depth begins when the one before it ends: none past S/2 (a little more for the clock's grain)
            Assert.All(infos.SkipLast(1), i => Assert.True(i.TimeMs <= planned / 2 + 5, string.Join("\n", log)));
            early = answeredMs < planned - 100;
        }
        Assert.True(early, "never answered before the cap:\n" + string.Join("\n", log));
    }

    [Theory]
    [InlineData(1, "LazySMP")]
    [InlineData(4, "LazySMP")]
    [InlineData(4, "Ranges")]
    public void WithPonderFinishOffAHitDropsTheDepthBegunPastHalfThePlannedTime(int threads, string split)
    {
        const int planned = 400;
        var (engine, output) = Start(threads, split, ponderFinish: false);
        engine.Handle(Pondered);
        engine.Handle($"go ponder {ClockPlanning(planned)}");
        Assert.NotNull(output.WaitForInfo(1, i => i.TimeMs > planned / 2, 30_000));   // (and the next depth begun)
        var before = output.InfosBefore(1);
        var clock = Stopwatch.StartNew();
        engine.Handle("ponderhit");
        Assert.NotNull(output.WaitForBestMove(1, 5000));
        Assert.InRange(clock.ElapsedMilliseconds, 0, 200);   // at once, as before P2
        var line = Assert.Single(output.InfosBefore(1)[before.Length..]);   // no new depth: the last line sent again
        Assert.Equal((before[^1].Depth, before[^1].Pv), (line.Depth, line.Pv));
    }

    [Fact]
    public void PonderFinishLeavesTheSearchItselfAlone()
    {
        // One thread: the same depths, node for node, and the same move, on or off, when no limit cuts the search.
        string[] Play(bool ponderFinish, bool ponderCap = true)
        {
            var (engine, output) = Start(ponderFinish: ponderFinish, ponderCap: ponderCap);
            engine.Handle(Pondered);
            engine.Handle("go ponder depth 8 wtime 100000 btime 100000");
            engine.Handle("ponderhit");
            string? best = output.WaitForBestMove(1, 10_000);
            Assert.NotNull(best);
            return output.InfosBefore(1).DistinctBy(i => i.Depth)   // (not a line sent again, if the hit came after depth 8)
                         .Select(i => $"{i.Depth} {i.Score} {i.Nodes} {i.Pv}").Append(best!).ToArray();
        }
        var on = Play(true);
        Assert.Equal(9, on.Length);
        Assert.Equal(Play(false, ponderCap: false), on);
        Assert.Equal(Play(true, ponderCap: false), on);
        Assert.Equal(Play(false), on);
    }
}
