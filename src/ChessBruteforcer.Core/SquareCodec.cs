namespace ChessBruteforcer.Core;

/// <summary>
/// The 6-bit square code.
///
/// <code>
///   bit 5     bit 4     bits 3..1    bit 0
///   occupied  colour    piece type   spare
///             0=white   1..6         reserved (castling / en passant, later)
/// </code>
///
/// Only 13 of the 64 codes mean anything: the canonical empty code (all
/// zeros) and the 12 pieces with the spare bit clear.  The other 51 are the
/// first layer of "null space": an empty square with stray bits set, a type
/// of 0 or 7 with the occupied flag set, or the reserved bit in use.
/// </summary>
public static class SquareCodec
{
    public const int BitsPerSquare = 6;
    public const int CodeCount = 1 << BitsPerSquare;

    public const byte OccupiedBit = 0b10_0000;
    public const byte ColourBit = 0b01_0000;
    public const byte TypeMask = 0b00_1110;
    public const byte SpareBit = 0b00_0001;
    public const int TypeShift = 1;

    public const byte EmptyCode = 0;

    public static byte Encode(Piece piece)
    {
        if (piece.IsEmpty)
            return EmptyCode;
        int code = OccupiedBit | ((int)piece.Type << TypeShift);
        if (piece.Colour == Colour.Black)
            code |= ColourBit;
        return (byte)code;
    }

    /// <summary>Decode a code, returning false for any of the 51 meaningless ones.</summary>
    public static bool TryDecode(byte code, out Piece piece)
    {
        piece = Piece.Empty;
        if (code >= CodeCount)
            return false;
        if (code == EmptyCode)
            return true;
        if ((code & OccupiedBit) == 0 || (code & SpareBit) != 0)
            return false;
        int type = (code & TypeMask) >> TypeShift;
        if (type < (int)PieceType.Pawn || type > (int)PieceType.King)
            return false;
        piece = new Piece((PieceType)type, (code & ColourBit) != 0 ? Colour.Black : Colour.White);
        return true;
    }

    public static bool IsValid(byte code) => TryDecode(code, out _);

    /// <summary>How many of the 64 codes decode to something (13).</summary>
    public static int ValidCodeCount { get; } =
        Enumerable.Range(0, CodeCount).Count(c => IsValid((byte)c));
}
