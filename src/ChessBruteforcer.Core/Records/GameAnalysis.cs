using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Records;

/// <summary>What a replay of one game measures.</summary>
/// <param name="Plies">Half-moves played.</param>
/// <param name="EndsInMate">The final position is checkmate (otherwise a decisive game was resigned or lost on time).</param>
/// <param name="Shuffles">Moves that take a piece straight back to the square its side's previous move took it from (Nf3, then Ng1).</param>
/// <param name="IdleShuffles">The shuffles made while nothing was happening: at least <see cref="GameAnalysis.IdlePlies"/> plies into a stretch with no capture or pawn move.</param>
/// <param name="Repeats">Plies that recreate a position already seen in the game (same pieces, side to move, castling, en passant).</param>
/// <param name="LongestQuiet">The longest run of plies with no capture and no pawn move, the stretch the 50-move rule counts.</param>
/// <param name="Captures">Captures, en passant included.</param>
public sealed record GameMetrics(int Plies, bool EndsInMate, int Shuffles, int IdleShuffles, int Repeats,
                                 int LongestQuiet, int Captures);

/// <summary>A game's idle moves by kind (see <see cref="GameAnalysis.Idle"/>).</summary>
/// <param name="Moves">Moves made at least <see cref="GameAnalysis.IdlePlies"/> plies into a stretch with no capture or pawn move.</param>
/// <param name="StraightBack">Of those, a piece sent straight back (the shuffle).</param>
/// <param name="Cycles">Back to an arrangement the mover's pieces already had in this stretch, not straight back.</param>
/// <param name="Fresh">An arrangement new to the stretch.</param>
public sealed record IdleMoves(int Moves, int StraightBack, int Cycles, int Fresh);

/// <summary>The three styles Matthew asked for, plus games the clock decided.</summary>
public enum GameStyle
{
    /// <summary>Won by mate or resignation within <see cref="GameAnalysis.EarlyKillPlies"/> plies.</summary>
    EarlyKill,

    /// <summary>Won later, with no sign of marking time.</summary>
    Efficient,

    /// <summary>Marked time on the way (a long quiet stretch, shuffling, or a repeated position), won or drawn.</summary>
    TimeWaster,

    /// <summary>A draw that shows no sign of marking time (agreed early, stalemate, bare kings...).</summary>
    CleanDraw,

    /// <summary>Lost on time or abandoned: the clock decided it, not the play.</summary>
    Clock,
}

public static class GameAnalysis
{
    /// <summary>
    /// The position's identity as FIDE's repetition rule sees it: the Zobrist
    /// hash, minus the en passant square unless an en passant capture is
    /// actually legal.  (The position after a double push keeps the square
    /// even when no pawn can use it, and "1. e4 e5 2. Nf3" must still meet
    /// "1. Nf3 e5 2. e4" as one position.)
    /// </summary>
    public static ulong Key(Position position)
    {
        if (position.EnPassantSquare == Position.NoSquare)
            return position.Hash;
        bool usable = MoveGenerator.Legal(position).Any(m => (m.Flags & MoveFlags.EnPassant) != 0);
        return usable ? position.Hash : position.Hash ^ Zobrist.EnPassant(position.EnPassantSquare);
    }

    /// <summary>25 moves each: the usual line for a "miniature".</summary>
    public const int EarlyKillPlies = 50;

    /// <summary>10 moves each with no capture or pawn move.</summary>
    public const int QuietLimit = 20;

    /// <summary>Idle shuffles (see below) before a game counts as shuffling.</summary>
    public const int ShuffleLimit = 3;

    /// <summary>
    /// "Nothing is happening" (Matthew's rule): 5 moves each with no capture or
    /// pawn move.  A shuffle counts only this far into a quiet stretch, so a
    /// piece that retreats because something just happened doesn't.
    /// </summary>
    public const int IdlePlies = 10;

    public static GameMetrics Measure(StoredGame game)
    {
        var position = game.StartPosition();
        var seen = new HashSet<ulong> { Key(position) };
        int shuffles = 0, idleShuffles = 0, repeats = 0, longestQuiet = 0, captures = 0;
        var moves = game.Moves;
        for (int i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            bool shuffle = i >= 2 && move.From == moves[i - 2].To && move.To == moves[i - 2].From;
            if (move.IsCapture)
                captures++;
            position.MakeMove(move);
            if (shuffle)
            {
                shuffles++;
                if (position.HalfmoveClock >= IdlePlies)
                    idleShuffles++;
            }
            longestQuiet = Math.Max(longestQuiet, position.HalfmoveClock);
            if (!seen.Add(Key(position)))
                repeats++;
        }
        return new GameMetrics(moves.Count, position.Status() == GameStatus.Checkmate, shuffles, idleShuffles,
                               repeats, longestQuiet, captures);
    }

    /// <summary>
    /// How a game spends its idle moves (Matthew's stalling question): a move
    /// made at least <see cref="IdlePlies"/> plies into a stretch with no
    /// capture or pawn move is sent straight back (the shuffle), or returns
    /// the mover's pieces to an arrangement they already had in this stretch
    /// some other way (a cycle: a piece walking a loop, or several pieces in
    /// turn, however orderly), or makes an arrangement new to the stretch.
    /// Only the mover's own pieces count, wherever the opponent's stand.
    /// </summary>
    public static IdleMoves Idle(StoredGame game)
    {
        var position = game.StartPosition();
        var seen = new[] { new HashSet<ulong>(), new HashSet<ulong>() };   // per side, since the last reset
        seen[0].Add(Arrangement(position, Colour.White));
        seen[1].Add(Arrangement(position, Colour.Black));
        int idle = 0, straightBack = 0, cycles = 0, fresh = 0;
        var moves = game.Moves;
        for (int i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            bool shuffle = i >= 2 && move.From == moves[i - 2].To && move.To == moves[i - 2].From;
            var mover = position.SideToMove;
            position.MakeMove(move);
            if (position.HalfmoveClock == 0)
            {
                // A capture or pawn move: a new stretch, and nothing before it can come back.
                seen[0].Clear();
                seen[1].Clear();
                seen[0].Add(Arrangement(position, Colour.White));
                seen[1].Add(Arrangement(position, Colour.Black));
                continue;
            }
            bool known = !seen[(int)mover].Add(Arrangement(position, mover));
            if (position.HalfmoveClock < IdlePlies)
                continue;
            idle++;
            if (shuffle)
                straightBack++;
            else if (known)
                cycles++;
            else
                fresh++;
        }
        return new IdleMoves(idle, straightBack, cycles, fresh);
    }

    /// <summary>One side's pieces and their squares, as a hash (the other side's are left out).</summary>
    private static ulong Arrangement(Position position, Colour side)
    {
        ulong hash = 0;
        for (int square = 0; square < 64; square++)
        {
            var piece = position[square];
            if (!piece.IsEmpty && piece.Colour == side)
                hash ^= Zobrist.Piece(piece, square);
        }
        return hash;
    }

    public static bool MarksTime(GameMetrics m) =>
        m.LongestQuiet >= QuietLimit || m.IdleShuffles >= ShuffleLimit || m.Repeats > 0;

    public static GameStyle Grade(StoredGame game, GameMetrics m)
    {
        string termination = game.Tag("Termination") ?? "Normal";
        if (termination is "Time forfeit" or "Abandoned")
            return GameStyle.Clock;
        if (game.Result == "1/2-1/2")
            return MarksTime(m) ? GameStyle.TimeWaster : GameStyle.CleanDraw;
        if (m.Plies <= EarlyKillPlies)
            return GameStyle.EarlyKill;
        return MarksTime(m) ? GameStyle.TimeWaster : GameStyle.Efficient;
    }
}

/// <summary>
/// How much games share, measured as a tree: games that start with the same
/// moves share those moves' branch, and a game played twice is one path.
/// Prefixes and positions are kept as 64-bit hashes, so with millions of them
/// the chance that two different ones collide is about one in a million.
/// </summary>
public sealed class GameTreeStats
{
    private readonly HashSet<ulong> _prefixes = new();
    private readonly HashSet<ulong> _endings = new();
    private readonly HashSet<ulong> _positions = new();
    private readonly List<int> _newFrom = new();

    public long Games { get; private set; }
    public long Plies { get; private set; }
    public long DuplicateGames { get; private set; }

    /// <summary>Tree nodes: distinct move sequences from the start, each stored once.</summary>
    public long UniquePrefixes => _prefixes.Count;

    /// <summary>Distinct positions reached by any game, however they got there (the start included).</summary>
    public long UniquePositions => _positions.Count;

    /// <summary>For each game, the ply at which it first left every earlier game's path (its length if it never did).</summary>
    public IReadOnlyList<int> NewFrom => _newFrom;

    /// <summary>The opening: 6 moves each.</summary>
    public const int OpeningPlies = 12;

    /// <summary>The first <see cref="OpeningPlies"/> plies of every game: how many, and how many were new tree nodes.</summary>
    public (long Plies, long New) Opening => (_openingPlies, _openingNew);

    /// <summary>Endgames by the most pieces on the board (kings included): 6, and 4 (what our tables cover).</summary>
    public static readonly int[] EndgamePieces = { 6, 4 };

    /// <summary>Per entry of <see cref="EndgamePieces"/>: how often games stood in such a position, how many were new, how many games got there, and how many arrived in a position some earlier game had already reached.</summary>
    public IReadOnlyList<EndgameCounts> Endgames => _endgames;

    private long _openingPlies, _openingNew;
    private readonly EndgameCounts[] _endgames = EndgamePieces.Select(n => new EndgameCounts(n)).ToArray();

    public void Add(StoredGame game)
    {
        var position = game.StartPosition();
        ulong prefix = Mix(position.Hash);
        _positions.Add(GameAnalysis.Key(position));
        int pieces = Enumerable.Range(0, 64).Count(s => !position[s].IsEmpty);
        var entered = new bool[_endgames.Length];
        int newFrom = -1;
        for (int i = 0; i < game.Moves.Count; i++)
        {
            var move = game.Moves[i];
            prefix = Mix(prefix ^ (ulong)((move.From << 9) | (move.To << 3) | (int)move.Promotion) ^ ((ulong)i << 32));
            bool newNode = _prefixes.Add(prefix);
            if (newNode && newFrom < 0)
                newFrom = i;
            if (i < OpeningPlies)
            {
                _openingPlies++;
                if (newNode)
                    _openingNew++;
            }
            if (move.IsCapture)
                pieces--;
            position.MakeMove(move);
            bool newPosition = _positions.Add(GameAnalysis.Key(position));
            for (int b = 0; b < _endgames.Length; b++)
            {
                if (pieces > _endgames[b].Pieces)
                    continue;
                _endgames[b].Plies++;
                if (newPosition)
                    _endgames[b].New++;
                if (!entered[b])
                {
                    entered[b] = true;
                    _endgames[b].Games++;
                    if (!newPosition)
                        _endgames[b].KnownEntries++;
                }
            }
        }
        if (!_endings.Add(Mix(prefix ^ (ulong)game.Moves.Count)))
            DuplicateGames++;
        _newFrom.Add(newFrom < 0 ? game.Moves.Count : newFrom);
        Games++;
        Plies += game.Moves.Count;
    }

    private static ulong Mix(ulong x)   // splitmix64's finaliser
    {
        x += 0x9E3779B97F4A7C15;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EB;
        return x ^ (x >> 31);
    }
}

/// <summary>Positions with at most <see cref="Pieces"/> pieces, across games.</summary>
public sealed class EndgameCounts(int pieces)
{
    public int Pieces { get; } = pieces;

    /// <summary>Plies that ended in such a position (a game sitting in one endgame for 40 plies counts 40).</summary>
    public long Plies { get; internal set; }

    /// <summary>Of those, positions no game had reached before.</summary>
    public long New { get; internal set; }

    /// <summary>Games that got down to this many pieces.</summary>
    public long Games { get; internal set; }

    /// <summary>Games whose first such position had already been reached by an earlier game: where games meet again.</summary>
    public long KnownEntries { get; internal set; }

    public EndgameCounts Copy() => new(Pieces) { Plies = Plies, New = New, Games = Games, KnownEntries = KnownEntries };
}
