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

    /// <summary>
    /// The legal move this SAN names.  Lenient the way PGN from the wild needs:
    /// check marks and annotations (+ # ! ?) are ignored rather than verified,
    /// "0-0" means "O-O", and a promotion may leave out the '='.
    /// </summary>
    public static Move Parse(Position position, string san)
    {
        string text = san.TrimEnd('+', '#', '!', '?');
        var legal = MoveGenerator.Legal(position);

        if (text is "O-O" or "0-0" or "O-O-O" or "0-0-0")
        {
            int file = text.Length == 3 ? 6 : 2;
            return Single(legal.Where(m => (m.Flags & MoveFlags.Castle) != 0 && m.To % 8 == file), san, position);
        }

        var type = PieceType.Pawn;
        int start = 0;
        if (text.Length > 0 && PieceOf(text[0]) is PieceType named)   // "P" for a pawn is rare but allowed
        {
            type = named;
            start = 1;
        }

        var promotion = PieceType.None;
        int end = text.Length;
        if (type == PieceType.Pawn && end > 0 && PieceOf(text[end - 1]) is PieceType promoted
            && promoted is not (PieceType.Pawn or PieceType.King))
        {
            promotion = promoted;
            end--;
            if (end > 0 && text[end - 1] == '=')
                end--;
        }

        if (end - start < 2)
            throw new FormatException($"'{san}' is not a move.");
        int to = Position.ParseSquare(text[(end - 2)..end]);

        // Whatever is left between the piece and the destination: a capture mark and
        // the from-square's file, rank, or both.
        int? fromFile = null, fromRank = null;
        foreach (char c in text[start..(end - 2)])
        {
            if (c is >= 'a' and <= 'h')
                fromFile = c - 'a';
            else if (c is >= '1' and <= '8')
                fromRank = c - '1';
            else if (c is not ('x' or ':' or '-'))
                throw new FormatException($"'{san}' is not a move.");
        }

        return Single(legal.Where(m => m.To == to
                                       && position[m.From].Type == type
                                       && m.Promotion == promotion
                                       && (fromFile is null || m.From % 8 == fromFile)
                                       && (fromRank is null || m.From / 8 == fromRank)), san, position);
    }

    private static Move Single(IEnumerable<Move> candidates, string san, Position position)
    {
        var found = candidates.Take(2).ToList();
        return found.Count switch
        {
            1 => found[0],
            0 => throw new FormatException($"'{san}' is not a legal move in {position.ToFen()}."),
            _ => throw new FormatException($"'{san}' is ambiguous in {position.ToFen()}."),
        };
    }

    private static PieceType? PieceOf(char c) => c switch
    {
        'N' => PieceType.Knight,
        'B' => PieceType.Bishop,
        'R' => PieceType.Rook,
        'Q' => PieceType.Queen,
        'K' => PieceType.King,
        'P' => PieceType.Pawn,
        _ => null,
    };

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
