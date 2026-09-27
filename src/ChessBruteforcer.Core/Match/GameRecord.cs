using System.Text;
using ChessBruteforcer.Core.Game;
using ChessBruteforcer.Core.Records;

namespace ChessBruteforcer.Core.Match;

public enum GameResult
{
    WhiteWins,
    BlackWins,
    Draw,
}

/// <summary>One finished game: who played, the moves (UCI), how it ended.</summary>
public sealed record GameRecord(
    string White, string Black, string StartFen, IReadOnlyList<string> Moves,
    GameResult Result, string Termination, int Round, string TimeControl)
{
    public string ResultText => Result switch
    {
        GameResult.WhiteWins => "1-0",
        GameResult.BlackWins => "0-1",
        _ => "1/2-1/2",
    };

    /// <summary>The game as PGN, readable by any chess program.</summary>
    public string ToPgn(string eventName, DateTime date)
    {
        var pgn = new StringBuilder();
        void Tag(string name, string value) => Pgn.AppendTag(pgn, name, value);
        Tag("Event", eventName);
        Tag("Site", "ChessBruteforcer match runner");
        Tag("Date", date.ToString("yyyy.MM.dd"));
        Tag("Round", Round.ToString());
        Tag("White", White);
        Tag("Black", Black);
        Tag("Result", ResultText);
        // PGN's TimeControl tag has no fixed-time-per-move form; record that separately.
        if (TimeControl.StartsWith("movetime"))
        {
            Tag("TimeControl", "-");
            Tag("MoveTime", TimeControl["movetime ".Length..]);
        }
        else
        {
            Tag("TimeControl", TimeControl);
        }
        Tag("Termination", Termination);
        if (StartFen != Fen.StartPosition)
        {
            Tag("SetUp", "1");
            Tag("FEN", StartFen);
        }
        pgn.AppendLine();

        var replay = Position.FromFen(StartFen);
        var moves = new List<Move>();
        foreach (string uci in Moves)
        {
            var move = replay.ParseUciMove(uci)
                       ?? throw new InvalidOperationException($"Illegal move {uci} in {replay.ToFen()}.");
            replay.MakeMove(move);
            moves.Add(move);
        }
        Pgn.AppendMoveText(pgn, Position.FromFen(StartFen), moves, ResultText);
        return pgn.ToString();
    }
}
