namespace ChessBruteforcer.Core;

/// <summary>Piece kinds. The numeric values are the 3-bit type field of a square code.</summary>
public enum PieceType : byte
{
    None = 0,
    Pawn = 1,
    Knight = 2,
    Bishop = 3,
    Rook = 4,
    Queen = 5,
    King = 6,
}

public enum Colour : byte
{
    White = 0,
    Black = 1,
}

/// <summary>What stands on one square: nothing, or a piece of one colour.</summary>
public readonly record struct Piece(PieceType Type, Colour Colour)
{
    public static readonly Piece Empty = new(PieceType.None, Colour.White);

    public bool IsEmpty => Type == PieceType.None;

    /// <summary>The 12 real pieces, white first, in <see cref="PieceType"/> order.</summary>
    public static readonly IReadOnlyList<Piece> All =
        (from colour in new[] { Colour.White, Colour.Black }
         from type in new[] { PieceType.Pawn, PieceType.Knight, PieceType.Bishop,
                              PieceType.Rook, PieceType.Queen, PieceType.King }
         select new Piece(type, colour)).ToArray();

    /// <summary>FEN letter: upper case for white, lower case for black, '.' for empty.</summary>
    public char ToFenChar()
    {
        char c = Type switch
        {
            PieceType.Pawn => 'p',
            PieceType.Knight => 'n',
            PieceType.Bishop => 'b',
            PieceType.Rook => 'r',
            PieceType.Queen => 'q',
            PieceType.King => 'k',
            _ => '.',
        };
        return Colour == Colour.White ? char.ToUpperInvariant(c) : c;
    }

    public static bool TryFromFenChar(char c, out Piece piece)
    {
        PieceType type = char.ToLowerInvariant(c) switch
        {
            'p' => PieceType.Pawn,
            'n' => PieceType.Knight,
            'b' => PieceType.Bishop,
            'r' => PieceType.Rook,
            'q' => PieceType.Queen,
            'k' => PieceType.King,
            _ => PieceType.None,
        };
        piece = type == PieceType.None
            ? Empty
            : new Piece(type, char.IsUpper(c) ? Colour.White : Colour.Black);
        return type != PieceType.None;
    }

    public override string ToString() => IsEmpty ? "Empty" : $"{Colour} {Type}";
}
