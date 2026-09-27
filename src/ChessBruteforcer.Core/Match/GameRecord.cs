using System.Text;
using ChessBruteforcer.Core.Game;

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
        void Tag(string name, string value) => pgn.Append('[').Append(name).Append(" \"").Append(value).AppendLine("\"]");
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

        var position = Position.FromFen(StartFen);
        var line = new StringBuilder();
        var words = new List<string>();
        foreach (string uci in Moves)
        {
            var move = position.ParseUciMove(uci)
                       ?? throw new InvalidOperationException($"Illegal move {uci} in {position.ToFen()}.");
            if (position.SideToMove == Colour.White)
                words.Add($"{position.FullmoveNumber}.");
            else if (words.Count == 0)
                words.Add($"{position.FullmoveNumber}...");
            words.Add(San.Of(position, move));
            position.MakeMove(move);
        }
        words.Add(ResultText);

        foreach (string word in words)
        {
            if (line.Length + word.Length + 1 > 79)
            {
                pgn.AppendLine(line.ToString());
                line.Clear();
            }
            if (line.Length > 0)
                line.Append(' ');
            line.Append(word);
        }
        pgn.AppendLine(line.ToString()).AppendLine();
        return pgn.ToString();
    }
}
