using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Records;

/// <summary>
/// One training position for the network (NNUE.md, "Training"), 32 bytes, little-endian, every field on
/// its own alignment so a batch can be viewed as int64 / int16 / int32 without copying:
///
/// <code>
///   offset  size  field
///        0     8  occupancy: bit s set when square s holds a piece (a1 = 0, b1 = 1, ... h8 = 63)
///        8    16  the pieces, 4 bits each, in square order: the i-th occupied square's code is in byte
///                 8 + i / 2, low half for even i, high half for odd i; unused halves are 0
///       24     1  flags: bit 0 black to move, bit 1 the score is a mate
///       25     1  the game's result for white: 0 lost, 1 drawn, 2 won (2 x white's score)
///       26     2  white's score, int16, Lichess's centipawns; a mate is +-32000 (and flagged), any
///                 other score is held to +-31999
///       28     4  the game's number, uint32: its place in the source files, counting every game read
/// </code>
///
/// A piece's code is colour x 8 + type: white pawn 1, knight 2, bishop 3, rook 4, queen 5, king 6; black
/// 9 to 14. Castling rights, the en passant square and the move clocks are not stored: the network doesn't
/// see them.
/// </summary>
public static class NnueRecord
{
    public const int Size = 32;
    public const int MaxPieces = 32;
    public const short MateScore = 32000;
    public const short MaxScore = MateScore - 1;

    public const byte BlackToMoveFlag = 1;
    public const byte MateFlag = 2;

    public const int OccupancyOffset = 0;
    public const int PiecesOffset = 8;
    public const int FlagsOffset = 24;
    public const int ResultOffset = 25;
    public const int ScoreOffset = 26;
    public const int GameOffset = 28;

    public static byte Code(Piece piece) =>
        piece.IsEmpty ? (byte)0 : (byte)((int)piece.Colour * 8 + (int)piece.Type);

    public static Piece FromCode(int code)
    {
        int type = code & 7;
        if (type is < 1 or > 6 || code > 15)
            throw new InvalidDataException($"{code} is not a piece code.");
        return new Piece((PieceType)type, (Colour)(code >> 3));
    }

    /// <summary>The result tag as stored: 2 for 1-0, 1 for a draw, 0 for 0-1; null for anything else.</summary>
    public static byte? ResultCode(string result) => result switch
    {
        "1-0" => 2,
        "1/2-1/2" => 1,
        "0-1" => 0,
        _ => null,
    };

    /// <summary>
    /// One entry of an Evals tag (scripts/eval-games.py): centipawns for white, M&lt;n&gt; or M-&lt;n&gt; for a mate
    /// in n for white or for black, ? for none. False for none, and for M0, whose side can't be told.
    /// <paramref name="clamped"/> says a number had to be held to +-31999.
    /// </summary>
    public static bool TryParseScore(string text, out short score, out bool mate, out bool clamped)
    {
        (score, mate, clamped) = (0, false, false);
        if (text.Length > 1 && text[0] == 'M')
        {
            if (!int.TryParse(text.AsSpan(1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) || n == 0)
                return false;
            (score, mate) = (n > 0 ? MateScore : (short)-MateScore, true);
            return true;
        }
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int cp))
            return false;
        clamped = Math.Abs((long)cp) > MaxScore;
        score = (short)Math.Clamp(cp, -MaxScore, MaxScore);
        return true;
    }

    /// <summary>Write the position and its labels into <paramref name="record"/> (<see cref="Size"/> bytes).</summary>
    public static void Write(Span<byte> record, Position position, short whiteScore, bool mate, byte result, uint game)
    {
        if (record.Length < Size)
            throw new ArgumentException($"A record needs {Size} bytes.");
        if (mate != (Math.Abs((int)whiteScore) == MateScore))
            throw new ArgumentException($"A score of {whiteScore} with the mate flag {(mate ? "set" : "clear")}.");
        if (result > 2)
            throw new ArgumentException($"{result} is not a result (0, 1 or 2).");
        record[..Size].Clear();
        ulong occupancy = 0;
        int count = 0;
        for (int square = 0; square < 64; square++)
        {
            var piece = position[square];
            if (piece.IsEmpty)
                continue;
            if (count == MaxPieces)
                throw new ArgumentException($"More than {MaxPieces} pieces: {position.ToFen()}");
            occupancy |= 1UL << square;
            record[PiecesOffset + count / 2] |= (byte)(Code(piece) << (count % 2 * 4));
            count++;
        }
        BinaryPrimitives.WriteUInt64LittleEndian(record[OccupancyOffset..], occupancy);
        record[FlagsOffset] = (byte)((position.SideToMove == Colour.Black ? BlackToMoveFlag : 0) | (mate ? MateFlag : 0));
        record[ResultOffset] = result;
        BinaryPrimitives.WriteInt16LittleEndian(record[ScoreOffset..], whiteScore);
        BinaryPrimitives.WriteUInt32LittleEndian(record[GameOffset..], game);
    }

    /// <summary>A record read back: the 64 squares (a1 = 0), and the labels.</summary>
    public readonly record struct Decoded(Piece[] Squares, Colour SideToMove, short WhiteScore, bool Mate, byte Result, uint Game);

    public static Decoded Read(ReadOnlySpan<byte> record)
    {
        if (record.Length < Size)
            throw new ArgumentException($"A record needs {Size} bytes.");
        ulong occupancy = BinaryPrimitives.ReadUInt64LittleEndian(record[OccupancyOffset..]);
        int count = BitOperations.PopCount(occupancy);
        if (count > MaxPieces)
            throw new InvalidDataException($"{count} squares occupied.");
        var squares = new Piece[64];
        int i = 0;
        for (ulong bits = occupancy; bits != 0; bits &= bits - 1, i++)
            squares[BitOperations.TrailingZeroCount(bits)] = FromCode((record[PiecesOffset + i / 2] >> (i % 2 * 4)) & 15);
        for (; i < MaxPieces; i++)
            if (((record[PiecesOffset + i / 2] >> (i % 2 * 4)) & 15) != 0)
                throw new InvalidDataException($"A piece code past the {count} occupied squares.");
        byte flags = record[FlagsOffset];
        if ((flags & ~(BlackToMoveFlag | MateFlag)) != 0)
            throw new InvalidDataException($"Flags {flags} has bits that mean nothing.");
        return new Decoded(squares, (flags & BlackToMoveFlag) != 0 ? Colour.Black : Colour.White,
                           BinaryPrimitives.ReadInt16LittleEndian(record[ScoreOffset..]), (flags & MateFlag) != 0,
                           record[ResultOffset], BinaryPrimitives.ReadUInt32LittleEndian(record[GameOffset..]));
    }

    /// <summary>The record's position as FEN: placement and side to move, then "- - 0 1", since the rest isn't stored.</summary>
    public static string ToFen(ReadOnlySpan<byte> record)
    {
        var decoded = Read(record);
        var text = new StringBuilder(Fen.ToPlacement(PackedBoard.FromPieces(decoded.Squares)));
        text.Append(decoded.SideToMove == Colour.White ? " w" : " b").Append(" - - 0 1");
        return text.ToString();
    }
}
