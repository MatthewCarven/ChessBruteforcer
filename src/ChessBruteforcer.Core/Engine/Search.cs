using System.Collections.Concurrent;
using System.Diagnostics;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Engine;

/// <summary>How long or how deep to search.  Unset fields mean "no limit".</summary>
public sealed record SearchLimits
{
    public int? Depth { get; init; }
    public long? Nodes { get; init; }
    public int? MoveTimeMs { get; init; }
    public int? WhiteTimeMs { get; init; }
    public int? BlackTimeMs { get; init; }
    public int WhiteIncrementMs { get; init; }
    public int BlackIncrementMs { get; init; }
    public int? MovesToGo { get; init; }
    public bool Infinite { get; init; }

    /// <summary>
    /// "go ponder": think on the opponent's time, with no time limit, until
    /// <see cref="Search.PonderHit"/> (then the clock fields above apply) or
    /// <see cref="Search.Stop"/>.  <see cref="Search.Run"/> doesn't return before one of them.
    /// </summary>
    public bool Ponder { get; init; }

    /// <summary>Time kept back on every move for communication and the operating system.</summary>
    public int MoveOverheadMs { get; init; } = 50;
}

/// <summary>Progress after each completed depth.</summary>
public sealed record SearchInfo(int Depth, int Score, long Nodes, TimeSpan Elapsed, IReadOnlyList<Move> PrincipalVariation)
{
    /// <summary>Moves to mate (positive: we mate, negative: we are mated), or null for a centipawn score.</summary>
    public int? MateIn => Search.MateIn(Score);
}

public sealed record SearchResult(Move? BestMove, int Score, int Depth, long Nodes, IReadOnlyList<Move> PrincipalVariation);

/// <summary>How a search on more than one thread shares out the work (see <see cref="Search.Threads"/>).</summary>
public enum ParallelMode
{
    /// <summary>
    /// Lazy SMP: every thread searches the whole tree from the root, and they
    /// share the transposition table, so each finds much of its work already
    /// done by the others.  Half the helpers start a depth ahead.  The main
    /// thread's result is the answer.
    /// </summary>
    LazySmp,

    /// <summary>
    /// Matthew's "ranges of possibility": at each depth the score range is
    /// cut into one window a thread, narrow near the last depth's score and
    /// wider further out, and every thread searches the root in its own
    /// window.  Exactly one window holds the true score, and its thread gives
    /// the answer; the others fail high or low and are stopped.  This is
    /// Baudet's parallel aspiration search (1978).  The threads share the
    /// transposition table too.
    /// </summary>
    Ranges,
}

/// <summary>
/// Iterative-deepening alpha-beta (negamax, principal variation search) with
/// a transposition table, null-move pruning, late-move reductions,
/// check extension, quiescence search on captures, killer / history move
/// ordering, repetition and 50-move draws, and endgame-table probing.
/// On more than one thread, see <see cref="ParallelMode"/>.
///
/// Scores are centipawns from the side to move's point of view; mates are
/// <see cref="Mate"/> minus the distance in plies.
/// </summary>
public sealed class Search
{
    public const int Mate = 30_000;
    public const int MateBound = Mate - 1_000;
    private const int Infinity = 32_000;
    private const int MaxPly = 128;

    /// <summary>Ranges search starts at this depth; the shallower ones take microseconds and run on one thread.</summary>
    public const int RangesFromDepth = 4;

    private readonly TranspositionTable _table;
    private readonly Tablebase? _tablebase;
    private readonly List<Move>[] _moveLists = Enumerable.Range(0, MaxPly).Select(_ => new List<Move>(64)).ToArray();
    private readonly int[][] _moveScores = Enumerable.Range(0, MaxPly).Select(_ => new int[256]).ToArray();
    private readonly Move[][] _pv = Enumerable.Range(0, MaxPly).Select(_ => new Move[MaxPly]).ToArray();
    private readonly int[] _pvLength = new int[MaxPly];
    private readonly Move[,] _killers = new Move[MaxPly, 2];
    private readonly int[,,] _history = new int[2, 64, 64];
    private readonly List<ulong> _hashes = new();

    private Stopwatch _clock = new();
    private long _nodes;
    private long _tableHits;   // nodes settled by an endgame table
    private long _nodeLimit;
    private long _softLimitMs, _hardLimitMs;       // in force now (0: none); written by PonderHit from another thread
    private long _plannedSoftMs, _plannedHardMs;   // the clock's budget, held back while pondering
    private long _iterationStartMs;                // when the depth being searched began
    private volatile bool _stop;
    private volatile bool _pondering;
    private readonly ManualResetEventSlim _ponderOver = new(true);

    // Threads: each helper is a Search of its own (move lists, killers, history, repetition
    // list), sharing this one's table.  A helper's _main is the search it works for.
    private Search? _main;
    private Search[] _helpers = Array.Empty<Search>();
    private int _activeHelpers;   // helpers in this search, whose nodes count as its own

    public Search(TranspositionTable table, Tablebase? tablebase = null)
    {
        _table = table;
        _tablebase = tablebase;
    }

    /// <summary>
    /// Threads to search on (1, the default: this one alone).  Set between
    /// searches.  With more than one, the endgame tables must be ones that
    /// never load or solve a table mid-search (LoadOnDemand off, as the UCI
    /// engine has them): probes from several threads at once are safe, a
    /// table arriving under them is not.
    /// </summary>
    public int Threads { get; set; } = 1;

    /// <summary>How the threads share the work when <see cref="Threads"/> is more than 1.</summary>
    public ParallelMode Split { get; set; } = ParallelMode.LazySmp;

    /// <summary>Width in centipawns of the narrowest windows in a <see cref="ParallelMode.Ranges"/> search.</summary>
    public int RangeStep { get; set; } = 15;

    /// <summary>
    /// Centipawns each <see cref="ParallelMode.Ranges"/> window reaches into
    /// its neighbours, both ways.  With none, a score near a boundary can be
    /// pushed out of both windows by pruning that depends on the window (on 8
    /// positions at depth 10 and 8 threads, 20 of ~56 depths had no window
    /// hold the score; with 10, 2).
    /// </summary>
    public int RangeOverlap { get; set; } = 10;

    /// <summary>Depths in the last <see cref="ParallelMode.Ranges"/> search at which no window held the score, so it was searched again on one thread with the full window.</summary>
    public int RangeFallbacks { get; private set; }

    /// <summary>Ask a running search to finish as soon as possible (thread-safe).</summary>
    public void Stop()
    {
        _stop = true;
        foreach (var helper in _helpers)
            helper._stop = true;
        _pondering = false;
        _ponderOver.Set();
    }

    /// <summary>
    /// The opponent played the move a <see cref="SearchLimits.Ponder"/> search
    /// was pondering on, so that search becomes the real one (thread-safe),
    /// keeping its tree, table and depth.  From now on the clock in its limits
    /// applies, with the time spent pondering counted, as if the search had
    /// begun on our own time at "go ponder": no new depth past half the
    /// planned time, none at all if pondering is already past it, and a depth
    /// begun past it (which the clock would never have started) is dropped.
    /// So a ponder hit plays as deep as our own time would have taken us, and
    /// usually at once, banking the clock for the moves we didn't foresee.
    /// </summary>
    public void PonderHit()
    {
        if (!_pondering)
            return;
        Interlocked.Exchange(ref _hardLimitMs, _plannedHardMs);
        Interlocked.Exchange(ref _softLimitMs, _plannedSoftMs);
        _pondering = false;
        if (_plannedSoftMs > 0 && Interlocked.Read(ref _iterationStartMs) > _plannedSoftMs / 2)
            Stop();
        _ponderOver.Set();
    }

    /// <summary>
    /// Search <paramref name="root"/>.  <paramref name="previousHashes"/> are
    /// the hashes of the game's earlier positions (oldest first, not
    /// including the root), for spotting repetitions.  <paramref name="onStarted"/>
    /// is called once the search is ready for <see cref="Stop"/> and
    /// <see cref="PonderHit"/> (both are lost if they come any earlier).
    /// </summary>
    public SearchResult Run(Position root, SearchLimits limits, IReadOnlyList<ulong>? previousHashes = null,
                            Action<SearchInfo>? onInfo = null, Action? onStarted = null)
    {
        _activeHelpers = 0;
        RangeFallbacks = 0;
        Prepare(root, limits, previousHashes);
        onStarted?.Invoke();
        var result = IterativeDeepening(root, limits, previousHashes, onInfo);
        // Pondering, the answer waits for ponderhit or stop, even when the search has nothing left to do.
        _ponderOver.Wait();
        return result;
    }

    private SearchResult IterativeDeepening(Position root, SearchLimits limits, IReadOnlyList<ulong>? previousHashes,
                                            Action<SearchInfo>? onInfo)
    {
        var legal = MoveGenerator.Legal(root);
        if (legal.Count == 0)
            return new SearchResult(null, root.InCheck() ? -Mate : 0, 0, 0, Array.Empty<Move>());

        // Perfect knowledge: with tables for this position and every reply, just play the best move.
        if (TablebaseMove(root, legal) is var (tableMove, tableScore))
        {
            onInfo?.Invoke(new SearchInfo(1, tableScore, legal.Count, _clock.Elapsed, new[] { tableMove }));
            return new SearchResult(tableMove, tableScore, 1, legal.Count, new[] { tableMove });
        }

        var result = new SearchResult(legal[0], 0, 0, 0, new[] { legal[0] });
        int maxDepth = Math.Min(limits.Depth ?? MaxPly - 8, MaxPly - 8);
        Thread[] threads = Array.Empty<Thread>();
        RangeWorkers? ranges = null;
        if (Threads > 1 && Split == ParallelMode.LazySmp)
            threads = StartHelpers(root, previousHashes);
        try
        {
            for (int depth = 1; depth <= maxDepth; depth++)
            {
                // (A full fence each side, here and in PonderHit: if a ponderhit lands just as this
                // depth begins, this check or PonderHit's sees the other's write, so it isn't searched.)
                long started = _clock.ElapsedMilliseconds;
                Interlocked.Exchange(ref _iterationStartMs, started);
                long softLimitMs = Interlocked.Read(ref _softLimitMs);
                if (depth > 1 && softLimitMs > 0 && started > softLimitMs / 2)
                    break;   // the next depth would likely overrun the budget

                int score;
                Move[] pv;
                bool complete;
                if (Threads > 1 && Split == ParallelMode.Ranges && depth >= RangesFromDepth)
                {
                    ranges ??= new RangeWorkers(this, root, previousHashes);
                    (score, pv, complete) = ranges.SearchDepth(depth, result.Score);
                }
                else
                {
                    score = Negamax(root, depth, -Infinity, Infinity, 0, allowNull: false);
                    pv = _pv[0].Take(_pvLength[0]).ToArray();
                    complete = !_stop;
                }
                if (!complete && depth > 1)
                    break;   // an unfinished iteration can't be trusted; keep the last complete one

                if (pv.Length > 0)
                    result = new SearchResult(pv[0], score, depth, TotalNodes, pv);
                onInfo?.Invoke(new SearchInfo(depth, score, TotalNodes, _clock.Elapsed, pv));

                if (_stop)
                    break;
                if (Math.Abs(score) >= MateBound && depth > 2 * (Mate - Math.Abs(score)) + 2 && !limits.Infinite)
                    break;   // a forced mate is fully seen
            }
        }
        finally
        {
            for (int i = 0; i < _activeHelpers; i++)
                _helpers[i]._stop = true;
            foreach (var thread in threads)
                thread.Join();
            ranges?.Dispose();
        }
        return result with { Nodes = TotalNodes };
    }

    /// <summary>Nodes searched in the current or last search, by this thread and its helpers.</summary>
    public long Nodes => TotalNodes;

    private long TotalNodes
    {
        get
        {
            long total = _nodes;
            var helpers = _helpers;
            for (int i = 0; i < _activeHelpers; i++)
                total += helpers[i]._nodes;   // (another thread's count, read as it runs: near enough)
            return total;
        }
    }

    /// <summary>Nodes in the current or last search that an endgame table settled, by this thread and its helpers.</summary>
    public long TableHits
    {
        get
        {
            long total = _tableHits;
            var helpers = _helpers;
            for (int i = 0; i < _activeHelpers; i++)
                total += helpers[i]._tableHits;
            return total;
        }
    }

    /// <summary>Moves to mate for a mate score (positive: side to move mates), otherwise null.</summary>
    public static int? MateIn(int score)
    {
        if (score >= MateBound) return (Mate - score + 1) / 2;
        if (score <= -MateBound) return -(Mate + score) / 2;
        return null;
    }

    /// <summary>Ready this search (or helper) for a new root.</summary>
    private void Prepare(Position root, SearchLimits limits, IReadOnlyList<ulong>? previousHashes)
    {
        _stop = false;
        _nodes = 0;
        _tableHits = 0;
        _clock = Stopwatch.StartNew();
        _nodeLimit = limits.Nodes ?? long.MaxValue;
        (_plannedSoftMs, _plannedHardMs) = TimeBudget(root, limits);
        _softLimitMs = limits.Ponder ? 0 : _plannedSoftMs;
        _hardLimitMs = limits.Ponder ? 0 : _plannedHardMs;
        _iterationStartMs = 0;
        _pondering = limits.Ponder;
        if (limits.Ponder)
            _ponderOver.Reset();
        else
            _ponderOver.Set();
        Array.Clear(_history);
        Array.Clear(_killers);

        _hashes.Clear();
        if (previousHashes is not null)
            _hashes.AddRange(previousHashes);
        _hashes.Add(root.Hash);
    }

    /// <summary>At least <paramref name="count"/> helpers, made once and kept: each holds ~250 KB of move lists and tables.</summary>
    private void EnsureHelpers(int count)
    {
        if (_helpers.Length >= count)
            return;
        var helpers = new Search[count];
        Array.Copy(_helpers, helpers, _helpers.Length);
        for (int i = _helpers.Length; i < count; i++)
            helpers[i] = new Search(_table, _tablebase) { _main = this };
        _helpers = helpers;
    }

    /// <summary>
    /// Lazy SMP: start <see cref="Threads"/> - 1 helpers, each deepening on a
    /// copy of the root until told to stop.  Helpers 1, 3, 5, ... start at
    /// depth 2, the rest at 1, so that at any moment about half of them are
    /// a depth ahead of the others and fill the table for them.
    /// </summary>
    private Thread[] StartHelpers(Position root, IReadOnlyList<ulong>? previousHashes)
    {
        int count = Threads - 1;
        EnsureHelpers(count);
        var threads = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            var helper = _helpers[i];
            var copy = Position.FromFen(root.ToFen());
            helper.Prepare(copy, new SearchLimits { Infinite = true }, previousHashes);
            int firstDepth = i % 2 == 0 ? 2 : 1;
            threads[i] = new Thread(() => helper.Deepen(copy, firstDepth))
            {
                IsBackground = true,
                Name = $"search helper {i + 1}",
            };
        }
        _activeHelpers = count;
        foreach (var thread in threads)
            thread.Start();
        return threads;
    }

    /// <summary>A Lazy SMP helper's whole job: deeper and deeper, until stopped.</summary>
    private void Deepen(Position root, int firstDepth)
    {
        for (int depth = firstDepth; depth <= MaxPly - 8 && !_stop; depth++)
            Negamax(root, depth, -Infinity, Infinity, 0, allowNull: false);
    }

    /// <summary>
    /// The boundaries between <paramref name="windows"/> score windows around
    /// <paramref name="previous"/>, lowest first, with -infinity and +infinity
    /// at the ends: at the previous score itself, then 1, 2, 4, 8, ... times
    /// <paramref name="step"/> below and above it, nearest first.
    /// </summary>
    public static int[] RangeBounds(int previous, int windows, int step)
    {
        // (Near a mate score, or with very many windows, the far cuts run into the ends and
        // there are fewer windows than asked for; the threads left over sit that depth out.)
        var cuts = new List<int> { -Infinity, Infinity };
        for (int k = 0; cuts.Count < windows + 1 && k < 64; k++)
        {
            long offset = k == 0 ? 0 : (long)Math.Max(1, step) << Math.Min(30, (k - 1) / 2);
            int cut = (int)Math.Clamp(k % 2 == 1 ? previous - offset : previous + offset, -Infinity + 2, Infinity - 2);
            if (!cuts.Contains(cut))
                cuts.Add(cut);
        }
        cuts.Sort();
        return cuts.ToArray();
    }

    /// <summary>
    /// The threads of a <see cref="ParallelMode.Ranges"/> search, one helper
    /// each, kept for the whole of a <see cref="Run"/>.  The thread calling
    /// <see cref="SearchDepth"/> hands out the windows and waits.
    /// </summary>
    private sealed class RangeWorkers : IDisposable
    {
        private readonly Search _owner;
        private readonly Position _root;   // the owner's own, for a depth it has to settle itself
        private readonly Worker[] _workers;
        private readonly BlockingCollection<Worker> _finished = new();

        private sealed class Worker
        {
            public required Search Helper;
            public required Position Root;
            public readonly SemaphoreSlim Go = new(0);
            public Thread? Thread;
            public bool Quit;
            public int Depth, Alpha, Beta;   // the job
            public int Score;                // and its answer
            public bool Complete;
            public Move[] Pv = Array.Empty<Move>();
        }

        public RangeWorkers(Search owner, Position root, IReadOnlyList<ulong>? previousHashes)
        {
            _owner = owner;
            _root = root;
            owner.EnsureHelpers(owner.Threads);
            _workers = new Worker[owner.Threads];
            for (int i = 0; i < _workers.Length; i++)
            {
                var copy = Position.FromFen(root.ToFen());
                var helper = owner._helpers[i];
                helper.Prepare(copy, new SearchLimits { Infinite = true }, previousHashes);
                var worker = new Worker { Helper = helper, Root = copy };
                worker.Thread = new Thread(() => Work(worker)) { IsBackground = true, Name = $"search range {i + 1}" };
                _workers[i] = worker;
            }
            owner._activeHelpers = _workers.Length;
            foreach (var worker in _workers)
                worker.Thread!.Start();
        }

        private void Work(Worker worker)
        {
            var helper = worker.Helper;
            while (true)
            {
                worker.Go.Wait();
                if (worker.Quit)
                    return;
                worker.Score = helper.Negamax(worker.Root, worker.Depth, worker.Alpha, worker.Beta, 0, allowNull: false);
                worker.Complete = !helper._stop;
                worker.Pv = helper._pv[0].Take(helper._pvLength[0]).ToArray();
                _finished.Add(worker);
            }
        }

        /// <summary>
        /// One depth: every thread its own window, the answer from the one
        /// whose window holds the score.  Not complete if the search was
        /// stopped before any window settled it.
        /// </summary>
        public (int Score, Move[] Pv, bool Complete) SearchDepth(int depth, int previous)
        {
            int[] bounds = RangeBounds(previous, _workers.Length, _owner.RangeStep);
            int windows = bounds.Length - 1;
            for (int i = 0; i < windows; i++)
            {
                var worker = _workers[i];
                worker.Depth = depth;
                // Each window overlaps the one below by a point at least, so that a score lying
                // exactly on a boundary is strictly inside a window, and that window's search returns it.
                int overlap = Math.Max(0, _owner.RangeOverlap);
                worker.Alpha = i == 0 ? -Infinity : bounds[i] - 1 - overlap;
                worker.Beta = i == windows - 1 ? Infinity : bounds[i + 1] + overlap;
                worker.Helper._stop = false;
                worker.Go.Release();
            }
            if (_owner._stop)
                StopAll();

            Worker? exact = null;
            for (int pending = windows; pending > 0;)
            {
                if (!_finished.TryTake(out var worker, 20))
                {
                    _owner.CheckLimits();   // (the workers watch the clock too; this is the backstop)
                    if (_owner._stop)
                        StopAll();
                    continue;
                }
                pending--;
                if (exact is null && worker.Complete && worker.Score > worker.Alpha && worker.Score < worker.Beta)
                {
                    exact = worker;
                    StopAll();   // the rest only fail high or low now
                }
            }
            if (exact is not null)
                return (exact.Score, exact.Pv, true);

            _owner.CheckLimits();
            if (_owner._stop)
                return (0, Array.Empty<Move>(), false);
            // No window held it: pruning that depends on the window (null move above all) can
            // make neighbouring windows disagree about which side of their boundary the score is.
            // Settle the depth on this thread with the full window.
            _owner.RangeFallbacks++;
            int score = _owner.Negamax(_root, depth, -Infinity, Infinity, 0, allowNull: false);
            return (score, _owner._pv[0].Take(_owner._pvLength[0]).ToArray(), !_owner._stop);
        }

        private void StopAll()
        {
            foreach (var worker in _workers)
                worker.Helper._stop = true;
        }

        public void Dispose()
        {
            StopAll();
            foreach (var worker in _workers)
            {
                worker.Quit = true;
                worker.Go.Release();
            }
            foreach (var worker in _workers)
            {
                worker.Thread!.Join();
                worker.Go.Dispose();
            }
            _finished.Dispose();
        }
    }

    private int Negamax(Position position, int depth, int alpha, int beta, int ply, bool allowNull)
    {
        _pvLength[ply] = ply;
        if ((++_nodes & 2047) == 0)
            CheckLimits();
        if (_stop)
            return 0;

        bool root = ply == 0;
        if (!root)
        {
            if (position.HalfmoveClock >= 100 || IsRepetition(position))
                return 0;
            // Mate-distance pruning: no line from here can beat a mate already found nearer the root.
            alpha = Math.Max(alpha, -Mate + ply);
            beta = Math.Min(beta, Mate - ply - 1);
            if (alpha >= beta)
                return alpha;
            // A table result, with the 50-move rule at this node's clock: a win too slow for it is a draw.
            if (_tablebase is not null && _tablebase.TryProbeWithClock(position, out var outcome))
            {
                _tableHits++;
                return ScoreFromOutcome(outcome, ply);
            }
        }
        if (ply >= MaxPly - 2)
            return Evaluation.Evaluate(position);

        bool inCheck = position.InCheck();
        if (inCheck)
            depth++;
        if (depth <= 0)
            return Quiescence(position, alpha, beta, ply);

        bool pvNode = beta - alpha > 1;
        Move ttMove = default;
        if (_table.TryGet(position.Hash, out var entry))
        {
            ttMove = entry.Move;
            if (!root && entry.Depth >= depth)
            {
                int ttScore = FromTable(entry.Score, ply);
                if (entry.Bound == Bound.Exact
                    || (entry.Bound == Bound.Lower && ttScore >= beta)
                    || (entry.Bound == Bound.Upper && ttScore <= alpha))
                    return ttScore;
            }
        }

        // Null move: if passing still leaves us at or above beta, this position is too good to need searching.
        if (allowNull && !pvNode && !inCheck && depth >= 3 && Evaluation.HasNonPawnMaterial(position)
            && Evaluation.Evaluate(position) >= beta)
        {
            int reduction = depth >= 6 ? 3 : 2;
            var nullUndo = position.MakeNullMove();
            _hashes.Add(position.Hash);
            int nullScore = -Negamax(position, depth - 1 - reduction, -beta, -beta + 1, ply + 1, allowNull: false);
            _hashes.RemoveAt(_hashes.Count - 1);
            position.UnmakeNullMove(nullUndo);
            if (_stop)
                return 0;
            if (nullScore >= beta)
                return nullScore >= MateBound ? beta : nullScore;
        }

        var moves = _moveLists[ply];
        MoveGenerator.Pseudo(position, moves);
        var scores = _moveScores[ply];
        ScoreMoves(position, moves, scores, ttMove, ply);

        Colour us = position.SideToMove;
        int originalAlpha = alpha;
        int best = -Infinity;
        Move bestMove = default;
        int legalMoves = 0;

        for (int i = 0; i < moves.Count; i++)
        {
            var move = PickNext(moves, scores, i);
            var undo = position.MakeMove(move);
            if (position.InCheck(us))
            {
                position.UnmakeMove(move, undo);
                continue;
            }
            legalMoves++;
            _hashes.Add(position.Hash);

            bool quiet = !move.IsCapture && !move.IsPromotion;
            int score;
            if (legalMoves == 1)
            {
                score = -Negamax(position, depth - 1, -beta, -alpha, ply + 1, allowNull: true);
            }
            else
            {
                int reduction = 0;
                if (depth >= 3 && legalMoves > 4 && quiet && !inCheck && !position.InCheck())
                    reduction = legalMoves > 12 ? 2 : 1;
                score = -Negamax(position, depth - 1 - reduction, -alpha - 1, -alpha, ply + 1, allowNull: true);
                if (score > alpha && reduction > 0)
                    score = -Negamax(position, depth - 1, -alpha - 1, -alpha, ply + 1, allowNull: true);
                if (score > alpha && score < beta)
                    score = -Negamax(position, depth - 1, -beta, -alpha, ply + 1, allowNull: true);
            }

            _hashes.RemoveAt(_hashes.Count - 1);
            position.UnmakeMove(move, undo);
            if (_stop)
                return 0;

            if (score > best)
            {
                best = score;
                bestMove = move;
                if (score > alpha)
                {
                    alpha = score;
                    _pv[ply][ply] = move;
                    for (int next = ply + 1; next < _pvLength[ply + 1]; next++)
                        _pv[ply][next] = _pv[ply + 1][next];
                    _pvLength[ply] = Math.Max(_pvLength[ply + 1], ply + 1);

                    if (score >= beta)
                    {
                        if (quiet)
                        {
                            if (_killers[ply, 0] != move)
                            {
                                _killers[ply, 1] = _killers[ply, 0];
                                _killers[ply, 0] = move;
                            }
                            _history[(int)us, move.From, move.To] += depth * depth;
                        }
                        break;
                    }
                }
            }
        }

        if (legalMoves == 0)
            return inCheck ? -Mate + ply : 0;

        var bound = best >= beta ? Bound.Lower : best > originalAlpha ? Bound.Exact : Bound.Upper;
        _table.Store(position.Hash, bestMove, ToTable(best, ply), depth, bound);
        return best;
    }

    /// <summary>Only captures (and queen promotions) until the position is quiet, so a hanging piece isn't missed.</summary>
    private int Quiescence(Position position, int alpha, int beta, int ply)
    {
        _pvLength[ply] = ply;
        if ((++_nodes & 2047) == 0)
            CheckLimits();
        if (_stop)
            return 0;
        if (ply >= MaxPly - 2)
            return Evaluation.Evaluate(position);

        bool inCheck = position.InCheck();
        int best;
        if (inCheck)
        {
            best = -Mate + ply;   // every evasion is searched; none means mate
        }
        else
        {
            best = Evaluation.Evaluate(position);
            if (best >= beta)
                return best;
            alpha = Math.Max(alpha, best);
        }

        var moves = _moveLists[ply];
        MoveGenerator.Pseudo(position, moves);
        var scores = _moveScores[ply];
        ScoreMoves(position, moves, scores, default, ply);
        Colour us = position.SideToMove;

        for (int i = 0; i < moves.Count; i++)
        {
            var move = PickNext(moves, scores, i);
            if (!inCheck && !move.IsCapture && move.Promotion != PieceType.Queen)
                continue;
            var undo = position.MakeMove(move);
            if (position.InCheck(us))
            {
                position.UnmakeMove(move, undo);
                continue;
            }
            int score = -Quiescence(position, -beta, -alpha, ply + 1);
            position.UnmakeMove(move, undo);
            if (_stop)
                return 0;
            if (score > best)
            {
                best = score;
                if (score > alpha)
                {
                    alpha = score;
                    if (score >= beta)
                        break;
                }
            }
        }
        return best;
    }

    /// <summary>Order: table move, captures (most valuable victim, least valuable attacker), promotions, killers, history.</summary>
    private void ScoreMoves(Position position, List<Move> moves, int[] scores, Move ttMove, int ply)
    {
        int side = (int)position.SideToMove;
        for (int i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            int score;
            if (move == ttMove)
            {
                score = 10_000_000;
            }
            else if (move.IsCapture)
            {
                var victim = (move.Flags & MoveFlags.EnPassant) != 0 ? PieceType.Pawn : position[move.To].Type;
                score = 1_000_000 + Evaluation.PieceValue(victim) * 10 - (int)position[move.From].Type;
                if (move.Promotion == PieceType.Queen)
                    score += 5_000;
            }
            else if (move.Promotion == PieceType.Queen)
            {
                score = 900_000;
            }
            else if (move == _killers[ply, 0])
            {
                score = 800_000;
            }
            else if (move == _killers[ply, 1])
            {
                score = 799_000;
            }
            else
            {
                score = Math.Min(_history[side, move.From, move.To], 700_000);
                if (move.IsPromotion)
                    score -= 1_000;   // under-promotions last
            }
            scores[i] = score;
        }
    }

    /// <summary>Selection sort, one step at a time: most moves are never looked at after a cutoff.</summary>
    private static Move PickNext(List<Move> moves, int[] scores, int start)
    {
        int bestIndex = start;
        for (int i = start + 1; i < moves.Count; i++)
            if (scores[i] > scores[bestIndex]) bestIndex = i;
        if (bestIndex != start)
        {
            (moves[start], moves[bestIndex]) = (moves[bestIndex], moves[start]);
            (scores[start], scores[bestIndex]) = (scores[bestIndex], scores[start]);
        }
        return moves[start];
    }

    /// <summary>Has this position occurred before, since the last capture or pawn move?</summary>
    private bool IsRepetition(Position position)
    {
        int top = _hashes.Count - 1;
        int oldest = Math.Max(0, top - position.HalfmoveClock);
        for (int i = top - 2; i >= oldest; i -= 2)
            if (_hashes[i] == position.Hash) return true;
        return false;
    }

    private void CheckLimits()
    {
        var owner = _main ?? this;   // a helper stops on the limits of the search it works for
        long hardLimitMs = Volatile.Read(ref owner._hardLimitMs);   // (0 while pondering)
        if (owner.TotalNodes >= owner._nodeLimit
            || (hardLimitMs > 0 && owner._clock.ElapsedMilliseconds >= hardLimitMs))
            _stop = true;
    }

    /// <summary>
    /// Soft limit: the planned time for this move (no new depth is started
    /// past half of it, since the next depth usually takes longer than all
    /// the previous ones).  Hard limit: abort mid-search.  Zero means none.
    /// </summary>
    private static (long Soft, long Hard) TimeBudget(Position root, SearchLimits limits)
    {
        if (limits.Infinite)
            return (0, 0);
        if (limits.MoveTimeMs is int moveTime)
            return (0, Math.Max(1, moveTime - limits.MoveOverheadMs / 2));
        int? time = root.SideToMove == Colour.White ? limits.WhiteTimeMs : limits.BlackTimeMs;
        if (time is not int remaining)
            return (0, 0);
        int increment = root.SideToMove == Colour.White ? limits.WhiteIncrementMs : limits.BlackIncrementMs;
        int movesToGo = Math.Clamp(limits.MovesToGo ?? 30, 1, 60);
        remaining = Math.Max(1, remaining - limits.MoveOverheadMs);
        long soft = remaining / movesToGo + increment * 3L / 4;
        long hard = Math.Min(soft * 3, remaining / 3 + increment);
        hard = Math.Max(5, Math.Min(hard, remaining - 20));
        return (Math.Min(soft, hard), hard);
    }

    /// <summary>
    /// The best move by the endgame tables, if the root and every position
    /// one move from it are covered by tables on hand.
    ///
    /// With the DTZ tables too, the 50-move rule decides, at the game's own
    /// clock (Matthew's "wanderer": an opponent who knows the tables can run
    /// the clock up and turn a slow win into a draw).  Winning, play the win
    /// that reaches its next capture, pawn move or mate soonest, since that
    /// starts the count again (ties: the quicker mate).  Losing, the loss
    /// furthest from the opponent's next one, where the clock may yet save
    /// us.  A win that the clock would run out on counts as a draw.  Without
    /// DTZ tables, the quickest mate, as before.
    /// </summary>
    private (Move Move, int Score)? TablebaseMove(Position root, List<Move> legal)
    {
        if (_tablebase is null || !_tablebase.TryProbe(root, out _))
            return null;
        bool rule = _tablebase.TryProbeDtz(root, out _);
        (Move Move, int Rule, int Mate, int Score)? best = null;
        foreach (var move in legal)
        {
            bool zeroing = move.IsCapture || move.IsPromotion || root[move.From].Type == PieceType.Pawn;
            var undo = root.MakeMove(move);
            bool covered = _tablebase.TryProbe(root, out var reply);
            var dtz = Outcome.Draw;
            if (covered && rule)
                covered = _tablebase.TryProbeDtz(root, out dtz);
            int clock = root.HalfmoveClock;
            root.UnmakeMove(move, undo);
            if (!covered)
                return null;

            int mate = ScoreFromOutcome(reply.ForPreviousMover(), 0);
            int ruleScore = 0, score = mate;
            if (rule)
            {
                // For us after this move: the opponent's result, a draw if it comes too late for the
                // count, one ply further off (or one ply away for a capture or pawn move: the count restarts).
                var ours = !Tablebase.DecisiveInTime(dtz, clock) ? Outcome.Draw
                           : zeroing ? new Outcome(dtz.Kind, 0).ForPreviousMover()
                           : dtz.ForPreviousMover();
                ruleScore = ours.Score;
                if (ours.Kind == OutcomeKind.Draw)
                    score = 0;   // (among draws the mate distance still breaks ties: a slip by them may revive it)
            }
            if (best is null || ruleScore > best.Value.Rule || (ruleScore == best.Value.Rule && mate > best.Value.Mate))
                best = (move, ruleScore, mate, score);
        }
        return best is { } chosen ? (chosen.Move, chosen.Score) : null;
    }

    private static int ScoreFromOutcome(Outcome outcome, int ply) => outcome.Kind switch
    {
        OutcomeKind.Win => Mate - (ply + outcome.Plies),
        OutcomeKind.Loss => -(Mate - (ply + outcome.Plies)),
        _ => 0,
    };

    // Mate scores are stored relative to the position, not the root.
    private static int ToTable(int score, int ply) =>
        score >= MateBound ? score + ply : score <= -MateBound ? score - ply : score;

    private static int FromTable(int score, int ply) =>
        score >= MateBound ? score - ply : score <= -MateBound ? score + ply : score;
}
