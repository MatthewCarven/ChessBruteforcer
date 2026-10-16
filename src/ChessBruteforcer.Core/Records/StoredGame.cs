using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Records;

/// <summary>
/// A game as it is stored: its PGN tags in their original order, the moves
/// from the start position, and the result.  The start is the standard
/// position unless a FEN tag says otherwise, so most games carry no FEN.
/// </summary>
public sealed record StoredGame(IReadOnlyList<(string Name, string Value)> Tags, IReadOnlyList<Move> Moves,
                                string Result)
{
    /// <summary>The four ways PGN can end a game's moves.</summary>
    public static readonly IReadOnlyList<string> Results = new[] { "*", "1-0", "0-1", "1/2-1/2" };

    public string? Tag(string name)
    {
        foreach (var (key, value) in Tags)
            if (key == name)
                return value;
        return null;
    }

    /// <summary>
    /// A rated game, by its Event tag. Lichess writes "Rated" there ("Rated Blitz game", "Rated Rapid tournament
    /// https://lichess.org/tournament/..."), except in Swiss tournament games ("Blitz swiss
    /// https://lichess.org/swiss/..."), which are rated too: its rated database holds them (2026-10-16, 1.07% of
    /// September's analysed games, left out until then). `tune positions` and `nnue data` both ask here.
    /// </summary>
    public bool IsRated
    {
        get
        {
            string name = Tag("Event") ?? "";
            return name.Contains("Rated", StringComparison.Ordinal) || name.Contains(LichessSwiss, StringComparison.Ordinal);
        }
    }

    private const string LichessSwiss = " swiss https://lichess.org/swiss/";

    public string StartFen => Tag("FEN") ?? Fen.StartPosition;

    public Position StartPosition() => Position.FromFen(StartFen);

    /// <summary>The position after the first <paramref name="plies"/> moves (0 = the start).</summary>
    public Position PositionAt(int plies)
    {
        if (plies < 0 || plies > Moves.Count)
            throw new ArgumentOutOfRangeException(nameof(plies), $"The game has {Moves.Count} moves.");
        var position = StartPosition();
        for (int i = 0; i < plies; i++)
            position.MakeMove(Moves[i]);
        return position;
    }

    /// <summary>Every move in standard notation, in order.</summary>
    public List<string> San()
    {
        var position = StartPosition();
        var names = new List<string>(Moves.Count);
        foreach (var move in Moves)
        {
            names.Add(Match.San.Of(position, move));
            position.MakeMove(move);
        }
        return names;
    }

    public string ToPgn() => Pgn.Write(this);
}
