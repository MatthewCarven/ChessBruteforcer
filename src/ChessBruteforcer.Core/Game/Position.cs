using System.Text;

namespace ChessBruteforcer.Core.Game;

[Flags]
public enum CastlingRights : byte
{
    None = 0,
    WhiteKingside = 1 << 0,
    WhiteQueenside = 1 << 1,
    BlackKingside = 1 << 2,
    BlackQueenside = 1 << 3,
    All = WhiteKingside | WhiteQueenside | BlackKingside | BlackQueenside,
}

/// <summary>What <see cref="Position.MakeMove"/> needs to put back on unmake.</summary>
public readonly record struct Undo(
    Piece Captured, CastlingRights Castling, int EnPassantSquare, int HalfmoveClock);

public enum GameStatus
{
    Ongoing,
    Checkmate,
    Stalemate,
}

/// <summary>
/// A full game state: the board plus everything FEN carries beyond it
/// (side to move, castling rights, en passant square, move clocks).
///
/// The board is a plain 64-square array (a1 = 0) for now; correctness first,
/// speed later.  <see cref="MakeMove"/> and <see cref="UnmakeMove"/> change
/// the position in place so searches don't copy boards.
/// </summary>
public sealed class Position
{
    public const int NoSquare = -1;

    private readonly Piece[] _squares = new Piece[64];
    private readonly int[] _kingSquare = { NoSquare, NoSquare };

    public Colour SideToMove { get; private set; }
    public CastlingRights Castling { get; private set; }

    /// <summary>The square a pawn could capture onto en passant, or <see cref="NoSquare"/>.</summary>
    public int EnPassantSquare { get; private set; } = NoSquare;

    public int HalfmoveClock { get; private set; }
    public int FullmoveNumber { get; private set; } = 1;

    public Piece this[int square] => _squares[square];

    public int KingSquare(Colour colour) => _kingSquare[(int)colour];

    public static Position Start() => FromFen(Fen.StartPosition);

    public static Position FromFen(string fen)
    {
        string[] fields = fen.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2)
            throw new FormatException("A position FEN needs at least placement and side to move.");

        var board = Fen.Parse(fields[0]);
        var position = new Position();
        for (int square = 0; square < 64; square++)
        {
            board.TryGetPiece(square, out var piece);
            position.Put(square, piece);
        }

        position.SideToMove = fields[1] switch
        {
            "w" => Colour.White,
            "b" => Colour.Black,
            _ => throw new FormatException($"Side to move must be 'w' or 'b', not '{fields[1]}'."),
        };

        if (fields.Length > 2 && fields[2] != "-")
        {
            foreach (char c in fields[2])
            {
                position.Castling |= c switch
                {
                    'K' => CastlingRights.WhiteKingside,
                    'Q' => CastlingRights.WhiteQueenside,
                    'k' => CastlingRights.BlackKingside,
                    'q' => CastlingRights.BlackQueenside,
                    _ => throw new FormatException($"Unexpected castling character '{c}'."),
                };
            }
        }

        if (fields.Length > 3 && fields[3] != "-")
            position.EnPassantSquare = ParseSquare(fields[3]);
        if (fields.Length > 4)
            position.HalfmoveClock = int.Parse(fields[4]);
        if (fields.Length > 5)
            position.FullmoveNumber = int.Parse(fields[5]);
        return position;
    }

    public string ToFen()
    {
        var sb = new StringBuilder(Fen.ToPlacement(ToPackedBoard()));
        sb.Append(SideToMove == Colour.White ? " w " : " b ");
        if (Castling == CastlingRights.None)
        {
            sb.Append('-');
        }
        else
        {
            if (Castling.HasFlag(CastlingRights.WhiteKingside)) sb.Append('K');
            if (Castling.HasFlag(CastlingRights.WhiteQueenside)) sb.Append('Q');
            if (Castling.HasFlag(CastlingRights.BlackKingside)) sb.Append('k');
            if (Castling.HasFlag(CastlingRights.BlackQueenside)) sb.Append('q');
        }
        sb.Append(' ').Append(EnPassantSquare == NoSquare ? "-" : PackedBoard.SquareName(EnPassantSquare));
        sb.Append(' ').Append(HalfmoveClock).Append(' ').Append(FullmoveNumber);
        return sb.ToString();
    }

    public PackedBoard ToPackedBoard() => PackedBoard.FromPieces(_squares);

    public static int ParseSquare(string name)
    {
        if (name.Length != 2 || name[0] is < 'a' or > 'h' || name[1] is < '1' or > '8')
            throw new FormatException($"'{name}' is not a square.");
        return (name[1] - '1') * 8 + (name[0] - 'a');
    }

    /// <summary>Is <paramref name="square"/> attacked by any piece of <paramref name="by"/>?</summary>
    public bool IsAttacked(int square, Colour by)
    {
        // Pawns: look one rank back from the attacker's point of view.
        int file = square % 8;
        int pawnRank = square / 8 + (by == Colour.White ? -1 : 1);
        if (pawnRank is >= 0 and < 8)
        {
            var pawn = new Piece(PieceType.Pawn, by);
            if (file > 0 && _squares[pawnRank * 8 + file - 1] == pawn) return true;
            if (file < 7 && _squares[pawnRank * 8 + file + 1] == pawn) return true;
        }

        var knight = new Piece(PieceType.Knight, by);
        foreach (int from in Attacks.Knight[square])
            if (_squares[from] == knight) return true;

        var king = new Piece(PieceType.King, by);
        foreach (int from in Attacks.King[square])
            if (_squares[from] == king) return true;

        var queen = new Piece(PieceType.Queen, by);
        for (int d = 0; d < Attacks.DirectionCount; d++)
        {
            var slider = new Piece(d < Attacks.FirstBishopDirection ? PieceType.Rook : PieceType.Bishop, by);
            foreach (int from in Attacks.Rays[square][d])
            {
                var piece = _squares[from];
                if (piece.IsEmpty)
                    continue;
                if (piece == slider || piece == queen)
                    return true;
                break;
            }
        }
        return false;
    }

    public bool InCheck(Colour colour)
    {
        int king = _kingSquare[(int)colour];
        return king != NoSquare && IsAttacked(king, Opponent(colour));
    }

    public bool InCheck() => InCheck(SideToMove);

    public GameStatus Status()
    {
        if (MoveGenerator.HasLegalMove(this))
            return GameStatus.Ongoing;
        return InCheck() ? GameStatus.Checkmate : GameStatus.Stalemate;
    }

    public Undo MakeMove(Move move)
    {
        var mover = _squares[move.From];
        var undo = new Undo(Piece.Empty, Castling, EnPassantSquare, HalfmoveClock);

        if ((move.Flags & MoveFlags.EnPassant) != 0)
        {
            int capturedSquare = move.To + (SideToMove == Colour.White ? -8 : 8);
            undo = undo with { Captured = _squares[capturedSquare] };
            Put(capturedSquare, Piece.Empty);
        }
        else
        {
            undo = undo with { Captured = _squares[move.To] };
        }

        Put(move.From, Piece.Empty);
        Put(move.To, move.IsPromotion ? new Piece(move.Promotion, SideToMove) : mover);

        if ((move.Flags & MoveFlags.Castle) != 0)
        {
            var (rookFrom, rookTo) = CastlingRook(move.To);
            Put(rookTo, _squares[rookFrom]);
            Put(rookFrom, Piece.Empty);
        }

        Castling &= ~(RightsLostAt(move.From) | RightsLostAt(move.To));
        EnPassantSquare = (move.Flags & MoveFlags.DoublePawnPush) != 0
            ? (move.From + move.To) / 2
            : NoSquare;
        HalfmoveClock = mover.Type == PieceType.Pawn || !undo.Captured.IsEmpty ? 0 : HalfmoveClock + 1;
        if (SideToMove == Colour.Black)
            FullmoveNumber++;
        SideToMove = Opponent(SideToMove);
        return undo;
    }

    public void UnmakeMove(Move move, Undo undo)
    {
        SideToMove = Opponent(SideToMove);
        if (SideToMove == Colour.Black)
            FullmoveNumber--;

        var moved = move.IsPromotion ? new Piece(PieceType.Pawn, SideToMove) : _squares[move.To];
        Put(move.From, moved);

        if ((move.Flags & MoveFlags.EnPassant) != 0)
        {
            Put(move.To, Piece.Empty);
            Put(move.To + (SideToMove == Colour.White ? -8 : 8), undo.Captured);
        }
        else
        {
            Put(move.To, undo.Captured);
        }

        if ((move.Flags & MoveFlags.Castle) != 0)
        {
            var (rookFrom, rookTo) = CastlingRook(move.To);
            Put(rookFrom, _squares[rookTo]);
            Put(rookTo, Piece.Empty);
        }

        Castling = undo.Castling;
        EnPassantSquare = undo.EnPassantSquare;
        HalfmoveClock = undo.HalfmoveClock;
    }

    /// <summary>Find the legal move with this UCI text (e2e4, e7e8q), or null.</summary>
    public Move? ParseUciMove(string uci) =>
        MoveGenerator.Legal(this).Cast<Move?>().FirstOrDefault(m => m!.Value.ToUci() == uci.Trim().ToLowerInvariant());

    public static Colour Opponent(Colour colour) => colour == Colour.White ? Colour.Black : Colour.White;

    /// <summary>Rook squares for a castling move, keyed on the king's destination.</summary>
    internal static (int From, int To) CastlingRook(int kingTo) => kingTo switch
    {
        6 => (7, 5),     // g1: h1 -> f1
        2 => (0, 3),     // c1: a1 -> d1
        62 => (63, 61),  // g8: h8 -> f8
        58 => (56, 59),  // c8: a8 -> d8
        _ => throw new InvalidOperationException($"{kingTo} is not a castling destination."),
    };

    private static CastlingRights RightsLostAt(int square) => square switch
    {
        4 => CastlingRights.WhiteKingside | CastlingRights.WhiteQueenside,
        7 => CastlingRights.WhiteKingside,
        0 => CastlingRights.WhiteQueenside,
        60 => CastlingRights.BlackKingside | CastlingRights.BlackQueenside,
        63 => CastlingRights.BlackKingside,
        56 => CastlingRights.BlackQueenside,
        _ => CastlingRights.None,
    };

    private void Put(int square, Piece piece)
    {
        _squares[square] = piece;
        if (piece.Type == PieceType.King)
            _kingSquare[(int)piece.Colour] = square;
    }

    public override string ToString() => ToFen();
}
