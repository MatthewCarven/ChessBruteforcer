using System.Numerics;

namespace ChessBruteforcer.Core.Possibility;

/// <summary>
/// A 48-byte board held as a 384-bit <see cref="BinaryRegister"/>: register
/// bit i is bit i of the packed stream, so square s owns bits 6s..6s+5, most
/// significant (the occupied flag) first.
///
/// Fully superposed it is every possible 48 bytes, 2^384 of them.  Collapsing
/// bits narrows that down, and <see cref="CountValidCodeCompletions"/> says how
/// many of the remaining completions put a real code on every square: the
/// first cut of the null space, done per square so it never enumerates.
/// </summary>
public sealed class SuperposedBoard
{
    private const int Bits = SquareCodec.BitsPerSquare;

    public BinaryRegister Register { get; } = new(PackedBoard.SquareCount * Bits);

    public static SuperposedBoard FromBoard(PackedBoard board)
    {
        var result = new SuperposedBoard();
        for (int square = 0; square < PackedBoard.SquareCount; square++)
            result.SetCode(square, board.GetCode(square));
        return result;
    }

    /// <summary>Collapse all six bits of a square to a piece (or empty).</summary>
    public void SetSquare(int square, Piece piece) => SetCode(square, SquareCodec.Encode(piece));

    public void SetCode(int square, byte code)
    {
        for (int i = 0; i < Bits; i++)
            Register.SetBit(square * Bits + i, (code >> (Bits - 1 - i)) & 1);
    }

    /// <summary>Put every bit of a square back into superposition.</summary>
    public void SuperposeSquare(int square)
    {
        for (int i = 0; i < Bits; i++)
            Register.SetBit(square * Bits + i, null);
    }

    /// <summary>Every bit pattern still possible: 2^(superposed bits).</summary>
    public BigInteger CountRawCompletions() => Register.CalculatePossibilityCount();

    /// <summary>The meaningful codes a square can still become, given its collapsed bits.</summary>
    public IReadOnlyList<byte> ValidCodesAt(int square)
    {
        var codes = new List<byte>();
        for (int code = 0; code < SquareCodec.CodeCount; code++)
        {
            if (SquareCodec.IsValid((byte)code) && Matches(square, code))
                codes.Add((byte)code);
        }
        return codes;
    }

    /// <summary>How many completions put a meaningful code on every square (tier 1).</summary>
    public BigInteger CountValidCodeCompletions()
    {
        BigInteger total = BigInteger.One;
        for (int square = 0; square < PackedBoard.SquareCount; square++)
            total *= ValidCodesAt(square).Count;
        return total;
    }

    /// <summary>Roll every superposed bit; the result may well be meaningless.</summary>
    public PackedBoard Collapse(Random rng)
    {
        string bits = Register.Collapse(rng);
        var board = new PackedBoard();
        for (int square = 0; square < PackedBoard.SquareCount; square++)
            board.SetCode(square, Convert.ToByte(bits.Substring(square * Bits, Bits), 2));
        return board;
    }

    /// <summary>
    /// Pick uniformly among the completions that pass tier 1, by choosing a
    /// meaningful code per square.  Ignores bit weights.
    /// </summary>
    public PackedBoard CollapseToValidCodes(Random rng)
    {
        var board = new PackedBoard();
        for (int square = 0; square < PackedBoard.SquareCount; square++)
        {
            var codes = ValidCodesAt(square);
            if (codes.Count == 0)
                throw new InvalidOperationException(
                    $"Square {PackedBoard.SquareName(square)} has no meaningful code left.");
            board.SetCode(square, codes[rng.Next(codes.Count)]);
        }
        return board;
    }

    private bool Matches(int square, int code)
    {
        for (int i = 0; i < Bits; i++)
        {
            int? fixedBit = Register.GetBit(square * Bits + i);
            if (fixedBit is int b && b != ((code >> (Bits - 1 - i)) & 1))
                return false;
        }
        return true;
    }
}
