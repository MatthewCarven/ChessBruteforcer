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

    public void Add(StoredGame game)
    {
        var position = game.StartPosition();
        ulong prefix = Mix(position.Hash);
        _positions.Add(GameAnalysis.Key(position));
        int newFrom = -1;
        for (int i = 0; i < game.Moves.Count; i++)
        {
            var move = game.Moves[i];
            prefix = Mix(prefix ^ (ulong)((move.From << 9) | (move.To << 3) | (int)move.Promotion) ^ ((ulong)i << 32));
            if (_prefixes.Add(prefix) && newFrom < 0)
                newFrom = i;
            position.MakeMove(move);
            _positions.Add(GameAnalysis.Key(position));
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
