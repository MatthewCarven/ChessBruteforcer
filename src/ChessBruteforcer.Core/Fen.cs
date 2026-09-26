namespace ChessBruteforcer.Core;

/// <summary>
/// Converts between boards and the piece-placement field of FEN.
///
/// Only placement is stored in a PackedBoard, so side to move, castling and
/// en passant fields are accepted on input and ignored.  Output is placement
/// only.
/// </summary>
public static class Fen
{
    public const string StartPosition = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    public static PackedBoard Parse(string fen)
    {
        string placement = fen.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? throw new FormatException("Empty FEN.");
        string[] rows = placement.Split('/');
        if (rows.Length != PackedBoard.Ranks)
            throw new FormatException($"FEN placement needs {PackedBoard.Ranks} ranks, got {rows.Length}.");

        var squares = new Piece[PackedBoard.SquareCount];
        for (int row = 0; row < rows.Length; row++)
        {
            int rank = PackedBoard.Ranks - 1 - row;
            int file = 0;
            foreach (char c in rows[row])
            {
                if (c is >= '1' and <= '8')
                {
                    file += c - '0';
                }
                else if (Piece.TryFromFenChar(c, out var piece))
                {
                    if (file >= PackedBoard.Files)
                        throw new FormatException($"Rank {rank + 1} has more than {PackedBoard.Files} squares.");
                    squares[PackedBoard.SquareIndex(file, rank)] = piece;
                    file++;
                }
                else
                {
                    throw new FormatException($"Unexpected character '{c}' in FEN placement.");
                }
            }
            if (file != PackedBoard.Files)
                throw new FormatException($"Rank {rank + 1} covers {file} squares, not {PackedBoard.Files}.");
        }
        return PackedBoard.FromPieces(squares);
    }

    public static bool TryParse(string fen, out PackedBoard board)
    {
        try
        {
            board = Parse(fen);
            return true;
        }
        catch (FormatException)
        {
            board = new PackedBoard();
            return false;
        }
    }

    /// <summary>The placement field, or null if any square holds a meaningless code.</summary>
    public static string? ToPlacement(PackedBoard board)
    {
        var sb = new System.Text.StringBuilder();
        for (int rank = PackedBoard.Ranks - 1; rank >= 0; rank--)
        {
            int empties = 0;
            for (int file = 0; file < PackedBoard.Files; file++)
            {
                if (!board.TryGetPiece(PackedBoard.SquareIndex(file, rank), out var piece))
                    return null;
                if (piece.IsEmpty)
                {
                    empties++;
                    continue;
                }
                if (empties > 0)
                    sb.Append(empties);
                empties = 0;
                sb.Append(piece.ToFenChar());
            }
            if (empties > 0)
                sb.Append(empties);
            if (rank > 0)
                sb.Append('/');
        }
        return sb.ToString();
    }
}
