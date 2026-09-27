using System.Text;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Match;

/// <summary>
/// Standard algebraic notation, the move text of PGN: "e4", "Nbd7", "exd5",
/// "O-O", "e8=Q+", "Qh4#".
/// </summary>
public static class San
{
    /// <summary>The SAN of a legal move in this position.  The position is left unchanged.</summary>
    public static string Of(Position position, Move move)
    {
        var text = new StringBuilder();
        var piece = position[move.From];

        if ((move.Flags & MoveFlags.Castle) != 0)
        {
            text.Append(move.To % 8 == 6 ? "O-O" : "O-O-O");
        }
        else if (piece.Type == PieceType.Pawn)
        {
            if (move.IsCapture)
                text.Append((char)('a' + move.From % 8)).Append('x');
            text.Append(PackedBoard.SquareName(move.To));
            if (move.IsPromotion)
                text.Append('=').Append(Letter(move.Promotion));
        }
        else
        {
            text.Append(Letter(piece.Type));
            text.Append(Disambiguation(position, move, piece));
            if (move.IsCapture)
                text.Append('x');
            text.Append(PackedBoard.SquareName(move.To));
        }

        var undo = position.MakeMove(move);
        if (position.InCheck())
            text.Append(MoveGenerator.HasLegalMove(position) ? '+' : '#');
        position.UnmakeMove(move, undo);
        return text.ToString();
    }

    /// <summary>File, rank, or both, when another piece of the same kind could also go there.</summary>
    private static string Disambiguation(Position position, Move move, Piece piece)
    {
        var rivals = MoveGenerator.Legal(position)
            .Where(m => m.To == move.To && m.From != move.From && position[m.From] == piece)
            .ToList();
        if (rivals.Count == 0)
            return "";
        bool fileUnique = rivals.All(m => m.From % 8 != move.From % 8);
        bool rankUnique = rivals.All(m => m.From / 8 != move.From / 8);
        string square = PackedBoard.SquareName(move.From);
        if (fileUnique)
            return square[..1];
        if (rankUnique)
            return square[1..];
        return square;
    }

    private static char Letter(PieceType type) => type switch
    {
        PieceType.Knight => 'N',
        PieceType.Bishop => 'B',
        PieceType.Rook => 'R',
        PieceType.Queen => 'Q',
        PieceType.King => 'K',
        _ => '?',
    };
}
