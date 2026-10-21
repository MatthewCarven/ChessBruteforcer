using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Tests;

/// <summary>
/// A depth the search is cut off in (PONDER.md, P4).  Before, its work was
/// thrown away and the last finished depth's move played.  Now a move the cut
/// depth proved better is played: one whose search at that depth ended and
/// beat the best before it, once the last depth's best has been searched to
/// the end there.  Anything less (a move that failed high on a null window and
/// is still being searched again, or a depth that hasn't reached the last
/// best yet) leaves the last finished depth's move, as before.
/// </summary>
public class CutDepthTests
{
    // Positions whose best move changes at some depth, on one thread with the built-in evaluation (as written):
    // Kiwipete at depths 7 and 8, Late at 4 and 8, Early at 3 and 4, Tactic at 2 and 3.  At the start position a
    // move fails high on a null window at depth 8, then fails low when searched again.
    private const string Late = "r1b1k2r/ppppnppp/2n2q2/2b5/3NP3/2P1B3/PP3PPP/RN1QKB1R w KQkq - 0 1";
    private const string Early = "r2q1rk1/pp2ppbp/2p2np1/6B1/3PP1b1/Q1P2N2/P4PPP/3RKB1R b K - 0 13";
    private const string Tactic = "2kr3r/pp1q1ppp/5n2/1Nb5/2Pp1B2/7Q/P4PPP/1R3RK1 w - - 0 1";
    private static readonly string[] Changing = { Early, Tactic, MoveGeneratorTests.Kiwipete, Late };

    /// <summary>A search and its root, traced: every root event and every depth's line, in order.</summary>
    private sealed record Traced(Search Search, List<Search.RootEvent> Events, List<SearchInfo> Infos)
    {
        public SearchResult Result { get; set; } = null!;

        /// <summary>The line of a finished depth.</summary>
        public SearchInfo Depth(int depth) => Infos.Single(i => i.Depth == depth && !i.Partial);
    }

    /// <summary>
    /// Search <paramref name="fen"/>, tracing its root.  The callbacks run under one lock, on the thread that
    /// searched (so <paramref name="onEvent"/> may stop the search there and then).
    /// </summary>
    private static Traced Run(string fen, SearchLimits limits, int threads = 1, ParallelMode split = ParallelMode.LazySmp,
                              Action<Traced, Search.RootEvent>? onEvent = null, Action<Traced, SearchInfo>? onInfo = null,
                              TranspositionTable? table = null)
    {
        var search = new Search(table ?? new TranspositionTable(8)) { Threads = threads, Split = split };
        var traced = new Traced(search, new(), new());
        search.RootTrace = e =>
        {
            lock (traced)
            {
                traced.Events.Add(e);
                onEvent?.Invoke(traced, e);
            }
        };
        traced.Result = search.Run(Position.FromFen(fen), limits, onInfo: info =>
        {
            lock (traced)
            {
                traced.Infos.Add(info);
                onInfo?.Invoke(traced, info);
            }
        });
        return traced;
    }

    /// <summary>
    /// The rule, from what one search thread's root did at a depth: the last move that beat the best before it,
    /// if that isn't <paramref name="previousBest"/> and <paramref name="previousBest"/> had been searched to the
    /// end; otherwise null (the last finished depth stands).
    /// </summary>
    private static Search.RootEvent? Proved(IEnumerable<Search.RootEvent> atDepth, Move previousBest)
    {
        var ended = atDepth.Where(e => !e.Research).ToList();
        var last = ended.LastOrDefault(e => e.Raised);
        bool searched = ended.Any(e => e.Move == previousBest);
        return searched && last.Raised && last.Move != previousBest ? last : null;
    }

    /// <summary>
    /// Of the moves the threads that searched <paramref name="depth"/> proved (<see cref="Proved"/>), or only
    /// <paramref name="only"/>, those with the highest score.
    /// </summary>
    private static List<Search.RootEvent> BestProved(Traced traced, int depth, Move previousBest, Search? only = null)
    {
        var proved = traced.Events.Where(e => e.Depth == depth && (only is null || e.Thread == only)).GroupBy(e => e.Thread)
                           .Select(thread => Proved(thread, previousBest)).OfType<Search.RootEvent>().ToList();
        return proved.Where(e => e.Score == proved.Max(p => p.Score)).ToList();
    }

    /// <summary>The shallowest depth a test stops at: from where Ranges shares a depth out (shallower, one thread searches it).</summary>
    private static int MinDepth(ParallelMode split) => split == ParallelMode.Ranges ? Search.RangesFromDepth : 2;

    private static string Line(SearchInfo info) =>
        $"{info.Depth} {info.Score} {info.Nodes} {string.Join(' ', info.PrincipalVariation.Select(m => m.ToUci()))}";

    private static (int, Move, int, long, bool, bool) Plain(Search.RootEvent e) => (e.Depth, e.Move, e.Score, e.Nodes, e.Research, e.Raised);

    /// <summary>
    /// Node counts to cut <paramref name="full"/> at (one thread: the search stops as its count reaches a
    /// multiple of 2,048 at or past the limit), at each depth where some move was searched again: early in the
    /// depth, before its first event; and just after the last depth's best was searched, after each move began
    /// to be searched again, after each move's search that beat the best before it or had been searched again,
    /// and after the next two moves' searches that end after one that beat the best (they did worse).
    /// </summary>
    private static IEnumerable<long> CutPoints(Traced full)
    {
        static long After(long nodes) => (nodes / 2048 + 1) * 2048;
        var points = new SortedSet<long>();
        foreach (int depth in full.Events.Where(e => e.Research && e.Depth >= 2).Select(e => e.Depth).Distinct())
        {
            var previousBest = full.Depth(depth - 1).PrincipalVariation[0];
            var events = full.Events.Where(e => e.Depth == depth).ToList();
            var searchedAgain = events.Where(e => e.Research).Select(e => e.Move).ToHashSet();
            points.Add(After(full.Depth(depth - 1).Nodes));
            foreach (var e in events.Where(e => e.Research || e.Raised || e.Move == previousBest || searchedAgain.Contains(e.Move)))
                points.Add(After(e.Nodes));
            var ended = events.Where(e => !e.Research).ToList();
            for (int i = 0; i < ended.Count; i++)
            {
                if (ended[i].Raised && ended[i].Move != previousBest)
                    foreach (var after in ended.Skip(i + 1).TakeWhile(e => !e.Raised).Take(2))
                        points.Add(After(after.Nodes));
            }
        }
        return points.Where(p => p < full.Infos[^1].Nodes);   // (a point past a depth's end cuts the next one)
    }

    [Theory]
    [InlineData(MoveGeneratorTests.Kiwipete, 8)]
    [InlineData(Late, 8)]
    [InlineData(Fen.StartPosition, 8)]
    public void OnOneThreadACutPlaysWhatTheDepthProvedAndNothingElse(string fen, int depth)
    {
        // An uncut search, then the same search cut by a node limit at points through the depths where a move was
        // searched again: one thread repeats the search node for node until the cut.
        var full = Run(fen, new SearchLimits { Depth = depth });
        int cuts = 0, played = 0, unconfirmed = 0;
        foreach (long stopAt in CutPoints(full))
        {
            var cut = Run(fen, new SearchLimits { Nodes = stopAt });
            int inProgress = full.Infos.First(i => i.Nodes >= stopAt).Depth;
            var before = full.Depth(inProgress - 1);
            var atDepth = full.Events.Where(e => e.Depth == inProgress && e.Nodes < stopAt).ToList();
            string where = $"cut at {stopAt} in depth {inProgress} after {string.Join(" ", atDepth.Select(e => $"{(e.Research ? "R" : e.Raised ? "+" : "-")}{e.Move.ToUci()}"))}";
            cuts++;

            // Everything before the cut is the uncut search's: the root's events and every finished depth.
            Assert.Equal(full.Events.Where(e => e.Nodes < stopAt && e.Depth <= inProgress).Select(Plain), cut.Events.Select(Plain));
            Assert.Equal(full.Infos.Where(i => i.Depth < inProgress).Select(Line), cut.Infos.Where(i => !i.Partial).Select(Line));

            if (Proved(atDepth, before.PrincipalVariation[0]) is { } proved)
            {
                // A move proved better than the last depth's best: it's played, with the cut depth's line sent.
                played++;
                Assert.True(proved.Move == cut.Result.BestMove, where);
                Assert.Equal((inProgress, proved.Score), (cut.Result.Depth, cut.Result.Score));
                var line = cut.Infos[^1];
                Assert.True(line.Partial, where);
                Assert.Equal((inProgress, proved.Score, proved.Move), (line.Depth, line.Score, line.PrincipalVariation[0]));
                Assert.Equal(cut.Result.PrincipalVariation, line.PrincipalVariation);
                Assert.Single(cut.Infos, i => i.Partial);
            }
            else
            {
                // Nothing proved: the last finished depth, as before.
                Assert.True(before.PrincipalVariation[0] == cut.Result.BestMove, where);
                Assert.Equal((inProgress - 1, before.Score), (cut.Result.Depth, cut.Result.Score));
                Assert.DoesNotContain(cut.Infos, i => i.Partial);
            }
            if (atDepth.Count > 0 && atDepth[^1].Research)
            {
                // Cut while a move that failed high on a null window was being searched again: not played.
                unconfirmed++;
                Assert.True(atDepth[^1].Move != cut.Result.BestMove, where);
            }
        }
        Assert.True(cuts >= 3, $"{cuts} cuts");
        if (fen == Fen.StartPosition)
            Assert.True(unconfirmed > 0, "no cut came while a move was searched again");
        else
            Assert.True(played > 0 && unconfirmed > 0, $"{played} cuts played a proved move, {unconfirmed} came during a search again");
    }

    [Theory]
    [InlineData(1, ParallelMode.LazySmp)]
    [InlineData(4, ParallelMode.LazySmp)]
    [InlineData(4, ParallelMode.Ranges)]
    public void StoppedJustAfterAMoveIsProvedBetterItPlaysThatMove(int threads, ParallelMode split)
    {
        // Stop the search the moment a thread whose answer counts proves a move better than the last depth's best
        // (Lazy SMP: the main thread; Ranges: any window's thread).
        var log = new List<string>();
        foreach (string fen in Changing)
        {
            var (previousBest, previousDepth) = (default(Move), 0);
            Search.RootEvent? stoppedOn = null;
            var traced = Run(fen, new SearchLimits { Depth = 10 }, threads, split,
                onEvent: (t, e) =>
                {
                    if (stoppedOn is not null || e.Depth < MinDepth(split) || e.Depth != previousDepth + 1)
                        return;
                    if (split == ParallelMode.LazySmp && e.Thread != t.Search)
                        return;
                    if (Proved(t.Events.Where(x => x.Thread == e.Thread && x.Depth == e.Depth), previousBest) is { } proved && proved == e)
                    {
                        stoppedOn = e;
                        t.Search.Stop();
                    }
                },
                onInfo: (_, info) =>
                {
                    if (!info.Partial)
                        (previousBest, previousDepth) = (info.PrincipalVariation[0], info.Depth);
                });
            log.Add($"{fen}: {(stoppedOn is { } s ? $"stopped at depth {s.Depth} on {s.Move.ToUci()}" : "never stopped")}, played {traced.Result.BestMove?.ToUci()} at depth {traced.Result.Depth}");
            if (stoppedOn is not { } stop)
                continue;

            var result = traced.Result;
            Assert.Equal(stop.Depth, result.Depth);
            Assert.True(result.BestMove != previousBest, string.Join("\n", log));
            if (split == ParallelMode.Ranges)
            {
                // Every window's thread that proved a move counts: the one with the highest score is played.
                var best = BestProved(traced, stop.Depth, previousBest);
                Assert.Contains(result.BestMove!.Value, best.Select(e => e.Move));
                Assert.Equal(best[0].Score, result.Score);
                Assert.True(result.Score >= stop.Score);
            }
            else
            {
                Assert.Equal((stop.Move, stop.Score), (result.BestMove!.Value, result.Score));
            }
            var line = traced.Infos[^1];
            Assert.True(line.Partial);
            Assert.Equal((stop.Depth, result.Score, result.BestMove!.Value), (line.Depth, line.Score, line.PrincipalVariation[0]));
            return;
        }
        Assert.Fail("no position proved a better move at any depth:\n" + string.Join("\n", log));
    }

    [Theory]
    [InlineData(1, ParallelMode.LazySmp)]
    [InlineData(4, ParallelMode.LazySmp)]
    [InlineData(4, ParallelMode.Ranges)]
    public void StoppedAsAFailHighIsSearchedAgainItKeepsWhatWasProved(int threads, ParallelMode split)
    {
        // Stop the search the moment a move that failed high on a null window is to be searched again, by a thread
        // whose answer counts.  That move hasn't been proved better, so it isn't played (unless another window's
        // thread proved it).
        var log = new List<string>();
        int stops = 0;
        foreach (string fen in Changing.Prepend(Fen.StartPosition))
        {
            var (previousBest, previousDepth) = (default(Move), 0);
            Search.RootEvent? stoppedOn = null;
            var traced = Run(fen, new SearchLimits { Depth = 10 }, threads, split,
                onEvent: (t, e) =>
                {
                    if (stoppedOn is not null || !e.Research || e.Depth < MinDepth(split) || e.Depth != previousDepth + 1)
                        return;
                    if (split == ParallelMode.LazySmp && e.Thread != t.Search)
                        return;
                    stoppedOn = e;
                    t.Search.Stop();
                },
                onInfo: (_, info) =>
                {
                    if (!info.Partial)
                        (previousBest, previousDepth) = (info.PrincipalVariation[0], info.Depth);
                });
            log.Add($"{fen}: {(stoppedOn is { } s ? $"stopped at depth {s.Depth} as {s.Move.ToUci()} was searched again" : "never stopped")}, played {traced.Result.BestMove?.ToUci()} at depth {traced.Result.Depth}");
            if (stoppedOn is not { } stop)
                continue;
            stops++;

            var result = traced.Result;
            string where = string.Join("\n", log);
            // What the threads whose answer counts had proved by the time they stopped (Lazy SMP: the main thread).
            var best = BestProved(traced, stop.Depth, previousBest, split == ParallelMode.LazySmp ? traced.Search : null);
            if (best.Count == 0)
            {
                Assert.True(result.BestMove == previousBest, where);
                Assert.Equal(stop.Depth - 1, result.Depth);
                Assert.DoesNotContain(traced.Infos, i => i.Partial);
            }
            else
            {
                Assert.Contains(result.BestMove!.Value, best.Select(e => e.Move));
                Assert.Equal((stop.Depth, best[0].Score), (result.Depth, result.Score));
                Assert.True(traced.Infos[^1].Partial, where);
            }
            if (result.BestMove == stop.Move)   // (only if a thread other than the one stopped on proved it)
                Assert.Contains(best, e => e.Move == stop.Move && e.Thread != stop.Thread);
        }
        Assert.True(stops >= 2, "too few stops:\n" + string.Join("\n", log));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARangesDepthCutInItsFallbackCountsWhatEveryThreadProved(bool onAProvedMove)
    {
        // When no window holds a depth's score, the calling thread searches it again with the full window.  Make
        // that happen: stop each window's thread alone at its first move, so none settles the depth.  Then stop the
        // search in that full-window search: as it proves a better move, or as its first move's search ends.  What
        // the windows' threads proved before they stopped counts too.
        var log = new List<string>();
        foreach (string fen in Changing)
        {
            var (previousBest, previousDepth) = (default(Move), 0);
            Search.RootEvent? stoppedOn = null;
            var traced = Run(fen, new SearchLimits { Depth = 10 }, 4, ParallelMode.Ranges,
                onEvent: (t, e) =>
                {
                    if (stoppedOn is not null || e.Research || e.Depth < Search.RangesFromDepth || e.Depth != previousDepth + 1)
                        return;
                    if (e.Thread != t.Search)
                    {
                        e.Thread.Stop();   // (a window's thread: it alone stops)
                        return;
                    }
                    if (onAProvedMove && Proved(t.Events.Where(x => x.Thread == e.Thread && x.Depth == e.Depth), previousBest) != e)
                        return;
                    stoppedOn = e;
                    t.Search.Stop();
                },
                onInfo: (_, info) =>
                {
                    if (!info.Partial)
                        (previousBest, previousDepth) = (info.PrincipalVariation[0], info.Depth);
                });
            log.Add($"{fen}: {traced.Search.RangeFallbacks} fallbacks, {(stoppedOn is { } s ? $"stopped at depth {s.Depth} on {s.Move.ToUci()}" : "never stopped")}");
            if (stoppedOn is not { } stop)
                continue;

            var result = traced.Result;
            var best = BestProved(traced, stop.Depth, previousBest);
            log.Add($"  played {result.BestMove?.ToUci()} at depth {result.Depth}; proved: {string.Join(" ", best.Select(e => e.Move.ToUci()))}");
            if (onAProvedMove)
                Assert.Contains(best, e => e.Thread == traced.Search);
            if (best.Count == 0)
            {
                Assert.True(result.BestMove == previousBest, string.Join("\n", log));
                Assert.Equal(stop.Depth - 1, result.Depth);
                Assert.DoesNotContain(traced.Infos, i => i.Partial);
            }
            else
            {
                Assert.Contains(result.BestMove!.Value, best.Select(e => e.Move));
                Assert.Equal((stop.Depth, best[0].Score), (result.Depth, result.Score));
                Assert.True(traced.Infos[^1].Partial);
            }
            return;
        }
        Assert.Fail("never stopped in a fallback:\n" + string.Join("\n", log));
    }

    [Fact]
    public void ADepthThatHasNotSearchedTheLastBestMoveYetKeepsIt()
    {
        // On one thread the root searches the table's move first, and that is the last depth's best.  Put another
        // move there (the move a deeper depth will prefer), so the next depth searches it before the last best.
        var full = Run(MoveGeneratorTests.Kiwipete, new SearchLimits { Depth = 8 });
        var change = full.Infos.Skip(1).First(i => i.PrincipalVariation[0] != full.Depth(i.Depth - 1).PrincipalVariation[0]);
        int seeded = change.Depth - 1;
        Move previousBest = full.Depth(seeded).PrincipalVariation[0], first = change.PrincipalVariation[0];
        ulong rootKey = Position.FromFen(MoveGeneratorTests.Kiwipete).Hash;

        Traced Seeded(Func<Traced, Search.RootEvent, bool> stopOn)
        {
            var table = new TranspositionTable(8);
            bool stopped = false;
            return Run(MoveGeneratorTests.Kiwipete, new SearchLimits { Depth = seeded + 1 }, table: table,
                onInfo: (_, info) =>
                {
                    if (info.Depth == seeded)
                        table.Store(rootKey, first, info.Score, seeded, Bound.Exact);
                },
                onEvent: (t, e) =>
                {
                    if (!stopped && e.Depth == seeded + 1 && stopOn(t, e))
                    {
                        stopped = true;
                        t.Search.Stop();
                    }
                });
        }

        // Stopped once the other move's search ends: it's first in the line, but the last best hasn't been searched.
        var early = Seeded((_, e) => !e.Research);
        var firstEvent = early.Events.First(e => e.Depth == seeded + 1);
        Assert.Equal((first, true), (firstEvent.Move, firstEvent.Raised));
        Assert.Equal((previousBest, seeded), (early.Result.BestMove!.Value, early.Result.Depth));
        Assert.DoesNotContain(early.Infos, i => i.Partial);

        // Stopped once the last best's search ends too: whichever did better at this depth is played.
        var later = Seeded((_, e) => !e.Research && e.Move == previousBest);
        var events = later.Events.Where(e => e.Depth == seeded + 1 && !e.Research).ToList();
        Assert.Equal(first, events[0].Move);
        var last = events[^1];
        Assert.Equal(previousBest, last.Move);
        if (last.Raised)   // the last best did better: it's first in the line, and the finished depth stands
        {
            Assert.Equal((previousBest, seeded), (later.Result.BestMove!.Value, later.Result.Depth));
        }
        else               // it didn't: the move first in the line, proved at least as good, is played
        {
            var leader = events.Last(e => e.Raised);
            Assert.Equal((leader.Move, seeded + 1, leader.Score), (later.Result.BestMove!.Value, later.Result.Depth, later.Result.Score));
            Assert.True(later.Infos[^1].Partial);
        }
        Assert.False(last.Raised, "the last best did better here: this test no longer reaches the case it's for");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARangesThreadThatHasNotSearchedTheLastBestMoveYetProvesNothing(bool inTheFallback)
    {
        // As above, on four threads with Ranges: another move in the table at the root, so every window's thread
        // (or, made to search the depth again with the full window, the calling thread) searches it first.  Stop
        // as that move's search ends, before any thread has searched the last depth's best.
        const string fen = MoveGeneratorTests.Kiwipete;
        const int seeded = 6;
        ulong rootKey = Position.FromFen(fen).Hash;
        var table = new TranspositionTable(8);
        var (previousBest, first) = (default(Move), default(Move));
        Search.RootEvent? stoppedOn = null;
        var traced = Run(fen, new SearchLimits { Depth = seeded + 1 }, 4, ParallelMode.Ranges, table: table,
            onInfo: (_, info) =>
            {
                if (info.Depth != seeded)
                    return;
                previousBest = info.PrincipalVariation[0];
                first = MoveGenerator.Legal(Position.FromFen(fen)).First(m => m != previousBest && !m.IsCapture);
                table.Store(rootKey, first, info.Score, seeded, Bound.Exact);
            },
            onEvent: (t, e) =>
            {
                if (stoppedOn is not null || e.Depth != seeded + 1 || e.Research)
                    return;
                if (inTheFallback && e.Thread != t.Search)
                {
                    e.Thread.Stop();   // (no window settles the depth, so the calling thread searches it again)
                    return;
                }
                if (e.Move == first)
                {
                    stoppedOn = e;
                    t.Search.Stop();
                }
            });
        Assert.NotNull(stoppedOn);
        var stop = stoppedOn!.Value;
        Assert.True(inTheFallback == (stop.Thread == traced.Search), "stopped on the wrong thread");
        var atDepth = traced.Events.Where(e => e.Depth == seeded + 1).ToList();
        Assert.DoesNotContain(atDepth.TakeWhile(e => e != stop), e => !e.Research && e.Move == previousBest);

        // Whatever any thread had proved by the time it stopped: by the trace, nothing, unless a thread went on to
        // search the last best after all before it saw the stop.
        var best = BestProved(traced, seeded + 1, previousBest);
        if (best.Count == 0)
        {
            Assert.Equal((previousBest, seeded), (traced.Result.BestMove!.Value, traced.Result.Depth));
            Assert.DoesNotContain(traced.Infos, i => i.Partial);
        }
        else
        {
            Assert.Contains(traced.Result.BestMove!.Value, best.Select(e => e.Move));
        }
    }

    [Fact]
    public void ACutInTheFirstDepthPlaysTheBestMoveItHasFound()
    {
        // As before P4: depth 1 has no finished depth before it, so its best move so far is played, whatever it is.
        var traced = Run(MoveGeneratorTests.Kiwipete, new SearchLimits { Depth = 3 },
            onEvent: (t, e) =>
            {
                if (e.Depth == 1 && t.Events.Count(x => x.Depth == 1 && !x.Research) == 3)
                    t.Search.Stop();
            });
        var ended = traced.Events.Where(e => e.Depth == 1 && !e.Research).ToList();
        Assert.Equal(3, ended.Count);
        var leader = ended.Last(e => e.Raised);
        Assert.Equal((leader.Move, 1), (traced.Result.BestMove!.Value, traced.Result.Depth));
        var line = Assert.Single(traced.Infos);
        Assert.Equal((1, false, leader.Move), (line.Depth, line.Partial, line.PrincipalVariation[0]));
    }

    [Fact]
    public void UncutSearchesAreTheSameWithTheRootTraced()
    {
        // The root's note-taking changes nothing a search does: node for node, depth by depth, traced or not.
        foreach (string fen in new[] { MoveGeneratorTests.Kiwipete, Late, Fen.StartPosition })
        {
            var traced = Run(fen, new SearchLimits { Depth = 7 });
            var plain = new List<SearchInfo>();
            var result = new Search(new TranspositionTable(8)).Run(Position.FromFen(fen), new SearchLimits { Depth = 7 }, onInfo: plain.Add);
            Assert.Equal(traced.Infos.Select(Line), plain.Select(Line));
            Assert.Equal((traced.Result.BestMove, traced.Result.Score, traced.Result.Depth, traced.Result.Nodes),
                         (result.BestMove, result.Score, result.Depth, result.Nodes));
            Assert.DoesNotContain(plain, i => i.Partial);
        }
    }

    [Fact]
    public void TheCutDepthsLineNamesTheMovePlayedAsALowerBound()
    {
        // Through UCI: a node limit that cuts depth 7 of Kiwipete just after it proved a better move.
        var output = new StringWriter();
        var engine = new UciEngine(output);
        var events = new List<Search.RootEvent>();
        engine.Search.RootTrace = events.Add;   // (one thread: no lock needed)
        engine.Handle($"position fen {MoveGeneratorTests.Kiwipete}");
        engine.Handle("go depth 8");
        engine.WaitForSearch();
        var infos = output.ToString().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("info depth")).ToList();
        string BestAt(int depth) => infos[depth - 1].Split(" pv ")[1].Split(' ')[0];
        var proved = events.First(e => e.Depth >= 2 && e.Raised && e.Move.ToUci() != BestAt(e.Depth - 1));
        long stopAt = (proved.Nodes / 2048 + 1) * 2048;
        var next = events.Where(e => e.Depth == proved.Depth && e.Nodes > proved.Nodes).Select(e => e.Nodes).DefaultIfEmpty(long.MaxValue).Min();
        Assert.True(stopAt < next, $"no room to cut between {proved.Nodes} and {next}");

        engine.Handle("ucinewgame");
        int from = output.ToString().Length;
        engine.Handle($"go nodes {stopAt}");
        engine.WaitForSearch();
        var lines = output.ToString()[from..].Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        string move = proved.Move.ToUci();
        string last = lines.Last(l => l.StartsWith("info depth"));
        Assert.Matches($@"^info depth {proved.Depth} score cp {proved.Score} lowerbound nodes \d+ nps \d+ time \d+ hashfull \d+ pv {move}( \S+)+$", last);
        Assert.Equal(proved.Depth - 1, lines.Count(l => l.StartsWith("info depth") && !l.Contains("lowerbound")));
        string reply = last.Split(" pv ")[1].Split(' ')[1];
        Assert.Equal($"bestmove {move} ponder {reply}", lines[^1]);
        Assert.DoesNotContain("lowerbound", string.Join("\n", infos));   // (an uncut search's lines never are)
    }
}
