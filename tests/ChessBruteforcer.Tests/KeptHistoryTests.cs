using System.Text.RegularExpressions;
using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Tests;

/// <summary>
/// The move-ordering history between searches (PONDER.md, P5).  Before, every
/// search started it from nothing.  Now a new search keeps
/// <see cref="Search.HistoryKept"/> percent of it from the one before, on
/// every thread, and "ucinewgame" forgets it.
/// </summary>
public class KeptHistoryTests
{
    private static (UciEngine Engine, StringWriter Output) Start()
    {
        var output = new StringWriter();
        return (new UciEngine(output), output);
    }

    /// <summary>The "info depth" lines from <paramref name="from"/> on, without the clock's figures (nps and time).</summary>
    private static List<string> Depths(StringWriter output, int from = 0) =>
        output.ToString()[from..].Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("info depth"))
              .Select(l => Regex.Replace(l, @" nps \d+ time \d+", "")).ToList();

    /// <summary>Search <paramref name="fen"/> to <paramref name="depth"/>: its depths' lines.</summary>
    private static List<string> Go(UciEngine engine, StringWriter output, string fen, int depth)
    {
        int from = output.ToString().Length;
        engine.Handle($"position fen {fen}");
        engine.Handle($"go depth {depth}");
        engine.WaitForSearch();
        return Depths(output, from);
    }

    /// <summary>
    /// Gives a search and its two helpers history, with <paramref name="go"/> (threads, position, depth) and all
    /// of it kept from search to search: on three threads first, which gives the helpers theirs, then on one,
    /// since on three the main thread may find all it needs in the table its helpers fill and cut off nothing
    /// itself.  The histories, the search's first.
    /// </summary>
    private static List<int[]> Filled(Func<Search> search, Action<int, string, int> go)
    {
        go(3, MoveGeneratorTests.Kiwipete, 7);
        go(1, MoveGeneratorTests.Middlegame, 5);
        var histories = search().Histories.ToList();
        Assert.Equal(3, histories.Count);
        Assert.Contains(histories[0], entry => entry > 0);
        Assert.Contains(histories.Skip(1).SelectMany(history => history), entry => entry > 0);
        return histories;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(100)]
    public void ANewSearchKeepsItsShareOfTheHistoryOnEveryThread(int percent)
    {
        var search = new Search(new TranspositionTable(8)) { HistoryKept = 100 };
        var before = Filled(() => search, (threads, fen, depth) =>
        {
            search.Threads = threads;
            search.Run(Position.FromFen(fen), new SearchLimits { Depth = depth });
        });

        // A search to depth 1 from the start adds nothing to the history: no root move reaches beta (infinity), and
        // none gives check, so below the root is quiescence.  Ranges searches the depths below 4 on this thread
        // alone, so the helpers sit it out: each history is only aged, once.
        search.HistoryKept = percent;
        search.Threads = 3;
        search.Split = ParallelMode.Ranges;
        search.Run(Position.Start(), new SearchLimits { Depth = 1 });
        var after = search.Histories.ToList();
        Assert.Equal(3, after.Count);
        for (int thread = 0; thread < 3; thread++)
            Assert.Equal(before[thread].Select(entry => (int)((long)entry * percent / 100)), after[thread]);
    }

    [Fact]
    public void UcinewgameForgetsTheHistoryOnEveryThread()
    {
        var (engine, output) = Start();
        engine.Handle("setoption name HistoryKept value 100");
        Filled(() => engine.Search, (threads, fen, depth) =>
        {
            engine.Handle($"setoption name Threads value {threads}");
            Go(engine, output, fen, depth);
        });

        engine.Handle("ucinewgame");
        Assert.All(engine.Search.Histories, history => Assert.DoesNotContain(history, entry => entry != 0));
    }

    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(100)]
    public void AFirstSearchIsTheSameNodeForNodeAsOneThatKeepsNothing(int percent)
    {
        // One thread: with no search before it, keeping a share of nothing is nothing.
        var (kept, keptOutput) = Start();
        kept.Handle($"setoption name HistoryKept value {percent}");
        var (cleared, clearedOutput) = Start();
        cleared.Handle("setoption name HistoryKept value 0");
        foreach (string fen in new[] { MoveGeneratorTests.Kiwipete, MoveGeneratorTests.Middlegame })
        {
            var lines = Go(kept, keptOutput, fen, 7);
            Assert.Equal(7, lines.Count);
            Assert.Equal(Go(cleared, clearedOutput, fen, 7), lines);
            kept.Handle("ucinewgame");
            cleared.Handle("ucinewgame");
        }
    }

    [Fact]
    public void AfterUcinewgameASearchIsAFreshEnginesNodeForNode()
    {
        var (fresh, freshOutput) = Start();
        var expected = Go(fresh, freshOutput, MoveGeneratorTests.Middlegame, 7);

        var (engine, output) = Start();
        Go(engine, output, MoveGeneratorTests.Kiwipete, 7);
        engine.Handle("ucinewgame");
        Assert.Equal(expected, Go(engine, output, MoveGeneratorTests.Middlegame, 7));

        // The history alone, kept, makes another search of it: a new table (Hash) clears the table and nothing else.
        Go(engine, output, MoveGeneratorTests.Kiwipete, 7);
        engine.Handle("setoption name Hash value 64");
        Assert.NotEqual(expected, Go(engine, output, MoveGeneratorTests.Middlegame, 7));
    }

    [Fact]
    public void TheHistoryKeptOptionReachesTheSearch()
    {
        var (engine, output) = Start();
        engine.Handle("uci");
        Assert.Contains($"option name HistoryKept type spin default {Search.DefaultHistoryKept} min 0 max 100", output.ToString());
        Assert.Equal(Search.DefaultHistoryKept, engine.Search.HistoryKept);
        foreach (var (value, kept) in new[] { ("25", 25), ("0", 0), ("150", 100), ("-5", 0) })
        {
            engine.Handle($"setoption name HistoryKept value {value}");
            Go(engine, output, MoveGeneratorTests.Middlegame, 1);
            Assert.Equal(kept, engine.Search.HistoryKept);
        }
    }
}
