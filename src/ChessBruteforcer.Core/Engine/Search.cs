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

/// <summary>
/// Iterative-deepening alpha-beta (negamax, principal variation search) with
/// a transposition table, null-move pruning, late-move reductions,
/// check extension, quiescence search on captures, killer / history move
/// ordering, repetition and 50-move draws, and endgame-table probing.
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
    private long _nodeLimit;
    private long _hardLimitMs;
    private volatile bool _stop;

    public Search(TranspositionTable table, Tablebase? tablebase = null)
    {
        _table = table;
        _tablebase = tablebase;
    }

    /// <summary>Ask a running search to finish as soon as possible (thread-safe).</summary>
    public void Stop() => _stop = true;

    /// <summary>
    /// Search <paramref name="root"/>.  <paramref name="previousHashes"/> are
    /// the hashes of the game's earlier positions (oldest first, not
    /// including the root), for spotting repetitions.
    /// </summary>
    public SearchResult Run(Position root, SearchLimits limits, IReadOnlyList<ulong>? previousHashes = null,
                            Action<SearchInfo>? onInfo = null)
    {
        _stop = false;
        _nodes = 0;
        _clock = Stopwatch.StartNew();
        _nodeLimit = limits.Nodes ?? long.MaxValue;
        var (softLimitMs, hardLimitMs) = TimeBudget(root, limits);
        _hardLimitMs = hardLimitMs;
        Array.Clear(_history);
        Array.Clear(_killers);

        _hashes.Clear();
        if (previousHashes is not null)
            _hashes.AddRange(previousHashes);
        _hashes.Add(root.Hash);

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
        for (int depth = 1; depth <= maxDepth; depth++)
        {
            int score = Negamax(root, depth, -Infinity, Infinity, 0, allowNull: false);
            if (_stop && depth > 1)
                break;   // an unfinished iteration can't be trusted; keep the last complete one

            var pv = _pv[0].Take(_pvLength[0]).ToArray();
            if (pv.Length > 0)
                result = new SearchResult(pv[0], score, depth, _nodes, pv);
            onInfo?.Invoke(new SearchInfo(depth, score, _nodes, _clock.Elapsed, pv));

            if (_stop)
                break;
            if (softLimitMs > 0 && _clock.ElapsedMilliseconds > softLimitMs / 2)
                break;   // the next depth would likely overrun the budget
            if (Math.Abs(score) >= MateBound && depth > 2 * (Mate - Math.Abs(score)) + 2 && !limits.Infinite)
                break;   // a forced mate is fully seen
        }
        return result with { Nodes = _nodes };
    }

    public long Nodes => _nodes;

    /// <summary>Moves to mate for a mate score (positive: side to move mates), otherwise null.</summary>
    public static int? MateIn(int score)
    {
        if (score >= MateBound) return (Mate - score + 1) / 2;
        if (score <= -MateBound) return -(Mate + score) / 2;
        return null;
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
                return ScoreFromOutcome(outcome, ply);
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
        if (_nodes >= _nodeLimit || (_hardLimitMs > 0 && _clock.ElapsedMilliseconds >= _hardLimitMs))
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
