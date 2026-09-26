namespace ChessBruteforcer.Core;

/// <summary>
/// A whole position in 48 bytes: 64 squares x 6 bits = 384 bits.
///
/// Squares are indexed a1 = 0, b1 = 1, ... h8 = 63 (index = rank * 8 + file)
/// and written as one big-endian bit stream: square 0 fills the top six bits
/// of byte 0.  Two boards are the same position exactly when their bytes are
/// equal, so boards compare, hash, sort and deduplicate as plain byte strings.
///
/// A PackedBoard can hold any 384 bits, meaningless codes included; that is
/// deliberate, so the checker has something to reject.
/// </summary>
public sealed class PackedBoard : IEquatable<PackedBoard>, IComparable<PackedBoard>
{
    public const int Files = 8;
    public const int Ranks = 8;
    public const int SquareCount = Files * Ranks;
    public const int ByteLength = SquareCount * SquareCodec.BitsPerSquare / 8; // 48

    private readonly byte[] _bytes;

    public PackedBoard() => _bytes = new byte[ByteLength];

    public PackedBoard(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength)
            throw new ArgumentException($"A packed board is exactly {ByteLength} bytes, got {bytes.Length}.");
        _bytes = bytes.ToArray();
    }

    public ReadOnlySpan<byte> Bytes => _bytes;

    public static PackedBoard FromPieces(ReadOnlySpan<Piece> squares)
    {
        if (squares.Length != SquareCount)
            throw new ArgumentException($"Expected {SquareCount} squares, got {squares.Length}.");
        var board = new PackedBoard();
        for (int i = 0; i < SquareCount; i++)
            board.SetCode(i, SquareCodec.Encode(squares[i]));
        return board;
    }

    public static int SquareIndex(int file, int rank) => rank * Files + file;

    public static string SquareName(int index) =>
        $"{(char)('a' + index % Files)}{index / Files + 1}";

    public byte GetCode(int square)
    {
        CheckSquare(square);
        int bit = square * SquareCodec.BitsPerSquare;
        int value = 0;
        for (int i = 0; i < SquareCodec.BitsPerSquare; i++, bit++)
            value = (value << 1) | ((_bytes[bit >> 3] >> (7 - (bit & 7))) & 1);
        return (byte)value;
    }

    public void SetCode(int square, byte code)
    {
        CheckSquare(square);
        if (code >= SquareCodec.CodeCount)
            throw new ArgumentOutOfRangeException(nameof(code), "A square code is 6 bits.");
        int bit = square * SquareCodec.BitsPerSquare;
        for (int i = SquareCodec.BitsPerSquare - 1; i >= 0; i--, bit++)
        {
            int mask = 1 << (7 - (bit & 7));
            if (((code >> i) & 1) != 0)
                _bytes[bit >> 3] |= (byte)mask;
            else
                _bytes[bit >> 3] &= (byte)~mask;
        }
    }

    /// <summary>The piece on a square, or false if the square holds a meaningless code.</summary>
    public bool TryGetPiece(int square, out Piece piece) =>
        SquareCodec.TryDecode(GetCode(square), out piece);

    public void SetPiece(int square, Piece piece) => SetCode(square, SquareCodec.Encode(piece));

    public string ToHex() => Convert.ToHexString(_bytes);

    public static PackedBoard FromHex(string hex)
    {
        hex = hex.Trim();
        if (hex.Length != ByteLength * 2)
            throw new FormatException($"A packed board is {ByteLength * 2} hex digits, got {hex.Length}.");
        return new PackedBoard(Convert.FromHexString(hex));
    }

    /// <summary>An 8x8 picture, rank 8 at the top; '?' marks a meaningless code.</summary>
    public string ToDiagram()
    {
        var sb = new System.Text.StringBuilder();
        for (int rank = Ranks - 1; rank >= 0; rank--)
        {
            sb.Append(rank + 1).Append(' ');
            for (int file = 0; file < Files; file++)
            {
                int square = SquareIndex(file, rank);
                sb.Append(TryGetPiece(square, out var p) ? p.ToFenChar() : '?');
                if (file < Files - 1)
                    sb.Append(' ');
            }
            sb.AppendLine();
        }
        sb.Append("  a b c d e f g h");
        return sb.ToString();
    }

    private static void CheckSquare(int square)
    {
        if ((uint)square >= SquareCount)
            throw new ArgumentOutOfRangeException(nameof(square));
    }

    public bool Equals(PackedBoard? other) =>
        other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => Equals(obj as PackedBoard);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_bytes);
        return hash.ToHashCode();
    }

    public int CompareTo(PackedBoard? other) =>
        other is null ? 1 : _bytes.AsSpan().SequenceCompareTo(other._bytes);

    public override string ToString() => ToHex();
}
