using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Records;

/// <summary>
/// One byte per move: the move's place among the position's legal moves,
/// sorted by from-square, then to-square, then promotion piece.
///
/// The order comes from the rules, not from the move generator's output, so
/// rewriting the generator (bitboards, say) can't change what a stored game
/// means.  No position has more than 218 legal moves, so a byte always fits.
/// </summary>
public static class MoveCode
{
    /// <summary>The legal moves in code order: move number n is <c>Canonical(position)[n]</c>.</summary>
    public static List<Move> Canonical(Position position)
    {
        var moves = MoveGenerator.Legal(position);
        moves.Sort(static (a, b) => Key(a).CompareTo(Key(b)));
        return moves;
    }

    public static byte Encode(Position position, Move move)
    {
        int index = Canonical(position).IndexOf(move);
        if (index < 0)
            throw new ArgumentException($"{move} is not legal in {position.ToFen()}.");
        return checked((byte)index);
    }

    public static Move Decode(Position position, byte code)
    {
        var moves = Canonical(position);
        if (code >= moves.Count)
            throw new InvalidDataException($"Move code {code}, but {position.ToFen()} has {moves.Count} legal moves.");
        return moves[code];
    }

    private static int Key(Move move) => (move.From << 9) | (move.To << 3) | (int)move.Promotion;
}
