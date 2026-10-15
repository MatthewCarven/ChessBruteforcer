using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Tests;

/// <summary>The transposition table shared between search threads, and the search on more than one.</summary>
public class ThreadTests
{
    [Fact]
    public void TableEntriesComeBackAsStored()
    {
        var table = new TranspositionTable(1);
        var cases = new (ulong Key, Move Move, int Score, int Depth, Bound Bound)[]
        {
            (0x0123_4567_89AB_CDEFul, new Move(12, 28, PieceType.None, MoveFlags.DoublePawnPush), 35, 7, Bound.Exact),
            (0xFEDC_BA98_7654_3210ul, new Move(52, 61, PieceType.Knight, MoveFlags.Capture), -29_990, 0, Bound.Upper),
            (0x8000_0000_0000_0001ul, new Move(4, 6, PieceType.None, MoveFlags.Castle), 29_995, 127, Bound.Lower),
            (0x7FFF_FFFF_FFFF_FFFFul, default, -1, -1, Bound.Exact),
        };
        foreach (var c in cases)
        {
            table.Store(c.Key, c.Move, c.Score, c.Depth, c.Bound);
            Assert.True(table.TryGet(c.Key, out var entry));
            Assert.Equal((c.Key, c.Move, (short)c.Score, (sbyte)c.Depth, c.Bound),
                         (entry.Key, entry.Move, entry.Score, entry.Depth, entry.Bound));
            Assert.False(table.TryGet(c.Key ^ 1, out _));
        }
    }

    [Fact]
    public void TheTableKeepsADeeperResultUnlessTheNewOneIsExact()
    {
        var table = new TranspositionTable(1);
        var move = new Move(12, 28);
        table.Store(42, move, 10, 8, Bound.Lower);
        table.Store(42, default, 20, 5, Bound.Upper);
        Assert.True(table.TryGet(42, out var entry));
        Assert.Equal((8, Bound.Lower), (entry.Depth, entry.Bound));
        table.Store(42, default, 30, 5, Bound.Exact);   // exact replaces it, and keeps the move it had
        Assert.True(table.TryGet(42, out entry));
        Assert.Equal((5, Bound.Exact, (short)30, move), (entry.Depth, entry.Bound, entry.Score, entry.Move));
    }

    /// <summary>
    /// Four threads writing different positions into the same few slots,
    /// four more reading: every hit must be the entry stored for that key,
    /// never half of one and half of another.
    /// </summary>
    [Fact]
    public void ThreadsWritingOneSlotNeverReadAMixedEntry()
    {
        var table = new TranspositionTable(1);
        const int slots = 1_048_576 / 24;
        static (Move Move, int Score, int Depth) For(ulong key) =>
            (new Move((byte)(key % 64), (byte)(key / 64 % 64), PieceType.None, MoveFlags.None),
             (int)(key % 20_000) - 10_000, (int)(key % 100));
        ulong KeyAt(int slot, int which) => (ulong)slot + (ulong)slots * (ulong)(which + 1) * 7919;

        long hits = 0, mixed = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            var rng = new Random(t);
            while (!stop.IsCancellationRequested)
            {
                ulong key = KeyAt(rng.Next(4), rng.Next(64));
                var (move, score, depth) = For(key);
                if (t < 4)
                {
                    table.Store(key, move, score, depth, Bound.Exact);
                }
                else if (table.TryGet(key, out var entry))
                {
                    Interlocked.Increment(ref hits);
                    if (entry.Move != move || entry.Score != score || entry.Depth != depth)
                        Interlocked.Increment(ref mixed);
                }
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.True(hits > 1000, $"only {hits} hits");
        Assert.Equal(0, mixed);
    }

    [Theory]
    [InlineData(0, 1, 15, new[] { -32_000, 32_000 })]
    [InlineData(0, 2, 15, new[] { -32_000, 0, 32_000 })]
    [InlineData(40, 4, 15, new[] { -32_000, 25, 40, 55, 32_000 })]
    [InlineData(0, 8, 15, new[] { -32_000, -60, -30, -15, 0, 15, 30, 60, 32_000 })]
    public void RangeWindowsAreNarrowNearTheLastScore(int previous, int windows, int step, int[] expected)
    {
        Assert.Equal(expected, Search.RangeBounds(previous, windows, step));
    }

    [Fact]
    public void RangeWindowsNearAMateRunOutRatherThanRepeat()
    {
        var bounds = Search.RangeBounds(31_990, 16, 15);
        Assert.True(bounds.Length >= 3);
        Assert.Equal(-32_000, bounds[0]);
        Assert.Equal(32_000, bounds[^1]);
        for (int i = 1; i < bounds.Length; i++)
            Assert.True(bounds[i] > bounds[i - 1]);
    }

    private static SearchResult Think(string fen, int depth, int threads, ParallelMode split, long? nodes = null) =>
        new Search(new TranspositionTable(8)) { Threads = threads, Split = split }
            .Run(Position.FromFen(fen), new SearchLimits { Depth = depth, Nodes = nodes });

    [Theory]
    [InlineData(2, ParallelMode.LazySmp)]
    [InlineData(4, ParallelMode.LazySmp)]
    [InlineData(2, ParallelMode.Ranges)]
    [InlineData(4, ParallelMode.Ranges)]
    [InlineData(8, ParallelMode.Ranges)]
    public void OnSeveralThreadsItStillFindsMatesAndFreeQueens(int threads, ParallelMode split)
    {
        var mate = Think("6k1/5ppp/8/8/8/8/5PPP/R5K1 w - - 0 1", 6, threads, split);
        Assert.Equal("a1a8", mate.BestMove?.ToUci());
        Assert.Equal(1, Search.MateIn(mate.Score));
        Assert.Equal("d1d5", Think("4k3/8/8/3q4/8/8/8/3QK3 w - - 0 1", 6, threads, split).BestMove?.ToUci());
        Assert.NotEqual("b1b7", Think("1r2k3/1p6/8/8/8/8/8/1Q2K3 w - - 0 1", 6, threads, split).BestMove?.ToUci());
    }

    [Theory]
    [InlineData(ParallelMode.LazySmp)]
    [InlineData(ParallelMode.Ranges)]
    public void OnSeveralThreadsItPlaysALegalMoveAtTheDepthAsked(ParallelMode split)
    {
        var position = Position.FromFen(MoveGeneratorTests.Kiwipete);
        var four = Think(MoveGeneratorTests.Kiwipete, 7, 4, split);
        Assert.NotNull(position.ParseUciMove(four.BestMove!.Value.ToUci()));
        Assert.Equal(7, four.Depth);
        Assert.Equal(four.BestMove, four.PrincipalVariation[0]);
    }

    [Theory]
    [InlineData(ParallelMode.LazySmp)]
    [InlineData(ParallelMode.Ranges)]
    public void ANodeLimitCountsEveryThread(ParallelMode split)
    {
        // (counting only one thread's, Lazy SMP would search ~4 x 200,000 and Ranges, whose
        // calling thread waits, would never stop)
        var result = Think(Fen.StartPosition, 64, 4, split, nodes: 200_000);
        Assert.NotNull(result.BestMove);
        Assert.True(result.Nodes < 400_000, $"{result.Nodes} nodes");
    }

    [Fact]
    public void OneThreadIsTheSameSearchWhicheverSplitIsSet()
    {
        var lazy = Think(MoveGeneratorTests.Kiwipete, 6, 1, ParallelMode.LazySmp);
        var ranges = Think(MoveGeneratorTests.Kiwipete, 6, 1, ParallelMode.Ranges);
        Assert.Equal((lazy.BestMove, lazy.Score, lazy.Nodes), (ranges.BestMove, ranges.Score, ranges.Nodes));
    }

    private static (UciEngine Engine, StringWriter Output) Start()
    {
        var output = new StringWriter();
        return (new UciEngine(output), output);
    }

    [Fact]
    public void UciOffersThreadsDefaultingToOne()
    {
        var (engine, output) = Start();
        engine.Handle("uci");
        Assert.Contains("option name Threads type spin default 1 min 1 max 64", output.ToString());
        Assert.Contains("option name ThreadSplit type combo default LazySMP var LazySMP var Ranges", output.ToString());
        engine.Handle("setoption name ThreadSplit value Sideways");
        Assert.Contains("info string error", output.ToString());
    }

    [Theory]
    [InlineData("LazySMP")]
    [InlineData("Ranges")]
    public void UciSearchesOnSeveralThreads(string split)
    {
        var (engine, output) = Start();
        engine.Handle("setoption name Threads value 4");
        engine.Handle($"setoption name ThreadSplit value {split}");
        engine.Handle("position startpos moves e2e4 e7e5 g1f3");
        engine.Handle("go depth 7");
        engine.WaitForSearch();
        string bestLine = output.ToString().Split('\n').Last(l => l.StartsWith("bestmove"));
        Assert.NotNull(engine.Position.ParseUciMove(bestLine.Split(' ')[1].Trim()));
        Assert.Contains("info depth 7", output.ToString());

        engine.Handle("go infinite");
        Thread.Sleep(200);
        engine.Handle("stop");
        Assert.Equal(2, output.ToString().Split('\n').Count(l => l.StartsWith("bestmove")));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        engine.Handle("go movetime 300");
        engine.WaitForSearch();
        Assert.InRange(clock.ElapsedMilliseconds, 250, 1500);
    }
}
