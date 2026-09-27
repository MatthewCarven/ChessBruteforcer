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
