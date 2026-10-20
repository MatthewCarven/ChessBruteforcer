using System.Diagnostics;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Match;

/// <summary>A time control: base time plus increment per move, a fixed time per move, or a fixed depth (no clock).</summary>
public sealed record TimeControl(int BaseMs, int IncrementMs, int? MoveTimeMs = null, int? Depth = null)
{
    /// <summary>"10+0.1" (seconds + seconds), "movetime=200" (milliseconds), or "depth=9" (plies).</summary>
    public static TimeControl Parse(string text)
    {
        if (text.StartsWith("movetime="))
            return new TimeControl(0, 0, int.Parse(text["movetime=".Length..]));
        if (text.StartsWith("depth="))
            return new TimeControl(0, 0, Depth: int.Parse(text["depth=".Length..]));
        string[] parts = text.Split('+');
        int baseMs = (int)(double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) * 1000);
        int incMs = parts.Length > 1
            ? (int)(double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * 1000)
            : 0;
        return new TimeControl(baseMs, incMs);
    }

    /// <summary>PGN TimeControl tag: seconds+seconds, or "-" for fixed move time.</summary>
    public override string ToString() =>
        Depth is int depth ? $"depth {depth}"
        : MoveTimeMs is int ms ? $"movetime {ms}ms" : $"{BaseMs / 1000.0:0.###}+{IncrementMs / 1000.0:0.###}";
}

/// <summary>
/// Plays one game between two players with a real clock, and decides how it
/// ended: checkmate, stalemate, 50-move rule, threefold repetition,
/// insufficient material, time forfeit, an illegal or missing move, or
/// (optionally) adjudication by our endgame tables once few pieces remain.
/// </summary>
public static class GamePlayer
{
    /// <summary>Allowed overrun before a flag falls, for process and OS jitter.</summary>
    public const int TimeMarginMs = 100;

    public const int MaxPlies = 600;

    /// <summary>How long a fixed-depth search may take before it counts as no answer.</summary>
    public const int FixedDepthTimeoutMs = 600_000;

    /// <summary>
    /// <paramref name="clock"/> is White's time control, and Black's too unless
    /// <paramref name="blackClock"/> gives Black one of its own (time odds, or depth against depth).
    /// </summary>
    public static GameRecord Play(IPlayer white, IPlayer black, IReadOnlyList<string> opening, TimeControl clock,
                                  int round, Tablebase? adjudicator = null, string startFen = Fen.StartPosition,
                                  TimeControl? blackClock = null)
    {
        var whiteClock = clock;
        blackClock ??= clock;
        var position = Position.FromFen(startFen);
        var moves = new List<string>();
        var seen = new Dictionary<ulong, int> { [position.Hash] = 1 };

        foreach (string uci in opening)
        {
            var move = position.ParseUciMove(uci) ?? throw new ArgumentException($"Opening move {uci} is illegal.");
            position.MakeMove(move);
            moves.Add(uci);
            seen[position.Hash] = seen.GetValueOrDefault(position.Hash) + 1;
        }

        white.NewGame();
        black.NewGame();
        int whiteTime = whiteClock.BaseMs, blackTime = blackClock.BaseMs;
        string timeControl = whiteClock == blackClock ? whiteClock.ToString() : $"{whiteClock} v {blackClock}";

        GameRecord Finish(GameResult result, string termination) =>
            new(white.Name, black.Name, startFen, moves.ToList(), result, termination, round, timeControl);

        while (true)
        {
            if (Ended(position, seen, adjudicator) is var (result, termination))
                return Finish(result, termination);
            if (moves.Count >= MaxPlies)
                return Finish(GameResult.Draw, "move limit");

            bool whiteToMove = position.SideToMove == Colour.White;
            var mover = whiteToMove ? white : black;
            var loss = whiteToMove ? GameResult.BlackWins : GameResult.WhiteWins;
            int remaining = whiteToMove ? whiteTime : blackTime;
            var moverClock = whiteToMove ? whiteClock : blackClock;

            var request = new MoveRequest(startFen, moves, position, whiteTime, blackTime,
                                          whiteClock.IncrementMs, blackClock.IncrementMs, moverClock.MoveTimeMs,
                                          moverClock.Depth);
            var timeout = TimeSpan.FromMilliseconds(moverClock.Depth is not null
                ? FixedDepthTimeoutMs : (moverClock.MoveTimeMs ?? remaining) + 5_000);
            var stopwatch = Stopwatch.StartNew();
            string? answer = mover.ChooseMove(request, timeout);
            long elapsed = stopwatch.ElapsedMilliseconds;

            if (answer is null)
                return Finish(loss, $"{mover.Name} did not answer");
            if (moverClock.MoveTimeMs is null && moverClock.Depth is null)
            {
                if (elapsed > remaining + TimeMarginMs)
                    return Finish(loss, $"{mover.Name} lost on time (used {elapsed} ms with {remaining} ms left)");
                remaining = (int)(remaining - elapsed + moverClock.IncrementMs);
                if (whiteToMove) whiteTime = remaining; else blackTime = remaining;
            }

            var move = position.ParseUciMove(answer);
            if (move is null)
                return Finish(loss, $"{mover.Name} played illegal move {answer}");
            position.MakeMove(move.Value);
            moves.Add(answer);
            seen[position.Hash] = seen.GetValueOrDefault(position.Hash) + 1;
        }
    }

    /// <summary>The result if the game is over, by the rules or by the tables.</summary>
    public static (GameResult Result, string Termination)? Ended(Position position, IReadOnlyDictionary<ulong, int> seen,
                                                                 Tablebase? adjudicator = null)
    {
        bool whiteToMove = position.SideToMove == Colour.White;
        switch (position.Status())
        {
            case GameStatus.Checkmate:
                return (whiteToMove ? GameResult.BlackWins : GameResult.WhiteWins, "checkmate");
            case GameStatus.Stalemate:
                return (GameResult.Draw, "stalemate");
        }
        if (position.HalfmoveClock >= 100)
            return (GameResult.Draw, "50-move rule");
        if (seen.GetValueOrDefault(position.Hash) >= 3)
            return (GameResult.Draw, "threefold repetition");
        if (InsufficientMaterial(position))
            return (GameResult.Draw, "insufficient material");
        // By the 50-move rule at the game's clock, where the DTZ tables are on hand: a cursed win is a draw.
        if (adjudicator is not null && adjudicator.TryProbeWithClock(position, out var outcome))
        {
            return outcome.Kind switch
            {
                OutcomeKind.Win => (whiteToMove ? GameResult.WhiteWins : GameResult.BlackWins, $"tablebase ({outcome})"),
                OutcomeKind.Loss => (whiteToMove ? GameResult.BlackWins : GameResult.WhiteWins, $"tablebase ({outcome})"),
                _ => (GameResult.Draw, adjudicator.TryProbe(position, out var mate) && mate.Kind != OutcomeKind.Draw
                    ? $"tablebase draw (50-move rule; {mate} without it)" : "tablebase draw"),
            };
        }
        return null;
    }

    /// <summary>Only kings, or king and one minor piece against a bare king.</summary>
    public static bool InsufficientMaterial(Position position)
    {
        int minors = 0;
        for (int square = 0; square < 64; square++)
        {
            switch (position[square].Type)
            {
                case PieceType.None or PieceType.King: break;
                case PieceType.Knight or PieceType.Bishop: minors++; break;
                default: return false;
            }
        }
        return minors <= 1;
    }
}
