using System.Text.RegularExpressions;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Tests;

/// <summary>
/// The line an "info" shows: the search's own, then on along the table's moves (PONDER.md, option (a)).  A PV node
/// that takes a table entry returns without the line under it, so a depth-15 search could show one move.  The walk
/// changes the text sent and nothing else: it only peeks at the table, so the search, and the ones after it, go
/// node for node as without it.
/// </summary>
public class TableLineTests
{
    private static Position After(params string[] moves)
    {
        var position = Position.Start();
        foreach (string move in moves)
            position.MakeMove(position.ParseUciMove(move)!.Value);
        return position;
    }

    /// <summary>The table holds <paramref name="move"/> for the position after <paramref name="moves"/> (from the start).</summary>
    private static void Plant(TranspositionTable table, Bound bound, string move, params string[] moves)
    {
        var position = After(moves);
        table.Store(position.Hash, position.ParseUciMove(move)!.Value, 0, 10, bound);
    }

    /// <summary>The line shown for a search from the start whose own line is <paramref name="own"/>, to <paramref name="plies"/>.</summary>
    private static string Walk(TranspositionTable table, int plies, IReadOnlyCollection<ulong>? history = null, string own = "e2e4") =>
        string.Join(' ', UciEngine.WithTableTail(table, Position.Start().ToFen(), history ?? Array.Empty<ulong>(),
                                                 new[] { Position.Start().ParseUciMove(own)!.Value }, plies)
                                  .Select(m => m.ToUci()));

    [Fact]
    public void TheLineGoesOnAlongTheTablesMovesToTheDepth()
    {
        var table = new TranspositionTable(1);
        Plant(table, Bound.Exact, "e7e5", "e2e4");
        Plant(table, Bound.Lower, "g1f3", "e2e4", "e7e5");   // (a lower bound's move is one good enough to stop on)
        Plant(table, Bound.Exact, "b8c6", "e2e4", "e7e5", "g1f3");
        Assert.Equal("e2e4 e7e5 g1f3 b8c6", Walk(table, 10));   // and no further: nothing is stored after Nc6
        Assert.Equal("e2e4 e7e5 g1f3", Walk(table, 3));
        Assert.Equal("e2e4", Walk(table, 1));
        Assert.Equal("e2e4", Walk(new TranspositionTable(1), 10));
    }

    [Fact]
    public void TheLineStopsAtAnUpperBoundOrAMoveThatIsntLegalThere()
    {
        var table = new TranspositionTable(1);
        Plant(table, Bound.Exact, "e7e5", "e2e4");
        Plant(table, Bound.Upper, "g1f3", "e2e4", "e7e5");   // only the least bad of moves that all failed low
        Assert.Equal("e2e4 e7e5", Walk(table, 10));

        table.Clear();
        table.Store(After("e2e4").Hash, new Move(12, 28), 0, 10, Bound.Exact);   // e2e4 again, with Black to move
        Assert.Equal("e2e4", Walk(table, 10));
    }

    [Fact]
    public void TheWalkLeavesAnOldEntryItReadsOld()
    {
        // Read through TryGet, an entry the walk passes would become this search's, as good as new, and outlive the
        // shallow entries a later store brings to its bucket, so the next search would find a different table.  Read
        // through TryPeek it stays five searches old (worth 10 - 5 x AgePenalty = 0) and the fourth store pushes it out.
        var table = new TranspositionTable(1);
        Assert.Equal(16_384 * TranspositionTable.BucketSlots, table.Slots);   // a key's bucket: its top 14 bits
        Plant(table, Bound.Exact, "e7e5", "e2e4");
        for (int i = 0; i < 5; i++)
            table.NewSearch();
        Assert.Equal("e2e4 e7e5", Walk(table, 10));   // the walk went through the entry
        ulong key = After("e2e4").Hash, bucket = key & ~((1UL << 50) - 1);
        for (ulong which = 1; which <= TranspositionTable.BucketSlots; which++)
            table.Store(bucket | which * 7919, new Move(12, 28), 0, 2, Bound.Exact);   // this search's, depth 2
        Assert.False(table.TryPeek(key, out _));
    }

    [Fact]
    public void TheLineEndsWithAMoveThatRepeatsAPositionOfTheLineOrTheGame()
    {
        // Knights out and back: the fourth move brings the root round again, and the table would go on for ever.
        var table = new TranspositionTable(1);
        Plant(table, Bound.Exact, "g1f3");
        Plant(table, Bound.Exact, "g8f6", "g1f3");
        Plant(table, Bound.Exact, "f3g1", "g1f3", "g8f6");
        Plant(table, Bound.Exact, "f6g8", "g1f3", "g8f6", "f3g1");
        Assert.Equal("g1f3 g8f6 f3g1 f6g8", Walk(table, 20, own: "g1f3"));
        // A position the game had before the root ends it as well.
        Assert.Equal("g1f3 g8f6", Walk(table, 20, new[] { After("g1f3", "g8f6").Hash }, "g1f3"));
    }

    [Fact]
    public void TheLineEndsWhereTheFiftyMoveRuleDoes()
    {
        // 99 plies with no capture or pawn move: the line's first move is the hundredth, a draw.
        var root = Position.FromFen("4k3/8/8/8/8/8/8/R3K3 w - - 99 80");
        var rook = root.ParseUciMove("a1a2")!.Value;
        var after = Position.FromFen(root.ToFen());
        after.MakeMove(rook);
        var table = new TranspositionTable(1);
        table.Store(after.Hash, after.ParseUciMove("e8d8")!.Value, 0, 10, Bound.Exact);
        var line = UciEngine.WithTableTail(table, root.ToFen(), Array.Empty<ulong>(), new[] { rook }, 10);
        Assert.Equal("a1a2", string.Join(' ', line.Select(m => m.ToUci())));
    }

    // One engine runs these one after another, the table carrying over (at Hash 1, small enough that what it keeps
    // matters).  Kiwipete, then the same shallower: that search's line meets the deep one's entries at once and is
    // cut there.  Then a game's positions two plies apart, as the bot meets them, and another middlegame.
    private static readonly (string Position, int Depth)[] Searches =
    {
        ($"fen {MoveGeneratorTests.Kiwipete}", 6),
        ($"fen {MoveGeneratorTests.Kiwipete}", 4),
        ("startpos moves e2e4 e7e5 g1f3 b8c6", 7),
        ("startpos moves e2e4 e7e5 g1f3 b8c6 f1b5 a7a6", 7),
        ($"fen {MoveGeneratorTests.Middlegame}", 6),
    };

    /// <summary>Each search's "info depth" and "bestmove" lines, with the walk on or off.</summary>
    private static List<List<string>> Play(bool tableTail)
    {
        var output = new StringWriter();
        var engine = new UciEngine(output) { TableTail = tableTail };
        engine.Handle("setoption name Hash value 1");
        var searches = new List<List<string>>();
        foreach (var (position, depth) in Searches)
        {
            int from = output.ToString().Length;
            engine.Handle($"position {position}");
            engine.Handle($"go depth {depth}");
            engine.WaitForSearch();
            searches.Add(output.ToString()[from..].Split('\n').Select(l => l.TrimEnd('\r'))
                               .Where(l => l.StartsWith("info depth") || l.StartsWith("bestmove")).ToList());
        }
        return searches;
    }

    private static readonly Lazy<(List<List<string>> On, List<List<string>> Off)> Played =
        new(() => (Play(tableTail: true), Play(tableTail: false)));

    /// <summary>An "info" line without its clock (nps, time) or its pv: depth, score, nodes and hashfull.</summary>
    private static string Stats(string line) => Regex.Replace(line.Split(" pv ")[0], @" nps \d+ time \d+", "");

    private static string[] Pv(string line) =>
        line.Contains(" pv ") ? line.Split(" pv ")[1].Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();

    [Fact]
    public void TheTableTailChangesNothingNodeForNode()
    {
        // One thread: every depth's score, nodes and hashfull, and every bestmove with its ponder move, as without the
        // walk, search after search.  (A walk that marked the entries it read as in use would change what the small
        // table keeps, and so the later searches, and hashfull at once.)
        var (on, off) = Played.Value;
        for (int s = 0; s < Searches.Length; s++)
        {
            Assert.Equal(off[s].Count, on[s].Count);
            Assert.Contains(on[s], l => l.StartsWith("bestmove"));
            for (int i = 0; i < on[s].Count; i++)
                Assert.Equal(on[s][i].StartsWith("bestmove") ? off[s][i] : Stats(off[s][i]),
                             on[s][i].StartsWith("bestmove") ? on[s][i] : Stats(on[s][i]));
        }
    }

    [Fact]
    public void ADepthsLineIsTheSearchsOwnThenTheTables()
    {
        var (on, off) = Played.Value;
        int longer = 0;
        for (int s = 0; s < Searches.Length; s++)
        {
            var root = new UciEngine(TextWriter.Null);
            root.Handle($"position {Searches[s].Position}");
            for (int i = 0; i < on[s].Count; i++)
            {
                if (!on[s][i].StartsWith("info depth"))
                    continue;
                string[] own = Pv(off[s][i]), shown = Pv(on[s][i]);
                int depth = int.Parse(on[s][i].Split(' ')[2]);
                Assert.Equal(own, shown.Take(own.Length));
                Assert.InRange(shown.Length, own.Length, Math.Max(depth, own.Length));
                var board = Position.FromFen(root.Position.ToFen());
                foreach (string move in shown)
                {
                    var legal = board.ParseUciMove(move);
                    Assert.True(legal is not null, $"{move} isn't legal in {board.ToFen()}: {on[s][i]}");
                    board.MakeMove(legal!.Value);
                }
                if (shown.Length > own.Length)
                    longer++;
            }
        }
        Assert.True(longer > 0);

        // The shallower search of Kiwipete meets the deep one's entries at once: its own line stops a move or two in,
        // and the table's goes on.
        string last = on[1].Last(l => l.StartsWith("info depth")), lastOwn = off[1].Last(l => l.StartsWith("info depth"));
        Assert.True(Pv(lastOwn).Length <= 2, lastOwn);
        Assert.True(Pv(last).Length >= 3, last);
    }
}
