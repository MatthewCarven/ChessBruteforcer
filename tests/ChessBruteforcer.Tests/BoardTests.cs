using ChessBruteforcer.Core;

namespace ChessBruteforcer.Tests;

public class BoardTests
{
    [Fact]
    public void ThirteenOfSixtyFourCodesMeanSomething()
    {
        Assert.Equal(13, SquareCodec.ValidCodeCount);
    }

    [Fact]
    public void EveryPieceRoundTripsThroughItsCode()
    {
        foreach (var piece in Piece.All.Append(Piece.Empty))
        {
            Assert.True(SquareCodec.TryDecode(SquareCodec.Encode(piece), out var decoded));
            Assert.Equal(piece, decoded);
        }
    }

    [Theory]
    [InlineData(0b000001)] // empty with the spare bit set
    [InlineData(0b010000)] // empty with the colour bit set
    [InlineData(0b100000)] // occupied, type 0
    [InlineData(0b101110)] // occupied, type 7
    [InlineData(0b101011)] // queen with the reserved bit set
    public void NonsenseCodesAreRejected(int code)
    {
        Assert.False(SquareCodec.IsValid((byte)code));
    }

    [Fact]
    public void PackedBoardIsFortyEightBytes()
    {
        Assert.Equal(48, PackedBoard.ByteLength);
        Assert.Equal(48, new PackedBoard().Bytes.Length);
    }

    [Fact]
    public void SettingOneSquareNeverDisturbsItsNeighbours()
    {
        var rng = new Random(1);
        var board = new PackedBoard();
        var expected = new byte[64];
        for (int round = 0; round < 2000; round++)
        {
            int square = rng.Next(64);
            byte code = (byte)rng.Next(64);
            board.SetCode(square, code);
            expected[square] = code;
        }
        for (int square = 0; square < 64; square++)
            Assert.Equal(expected[square], board.GetCode(square));
    }

    [Fact]
    public void HexRoundTrips()
    {
        var board = Fen.Parse(Fen.StartPosition);
        string hex = board.ToHex();
        Assert.Equal(96, hex.Length);
        Assert.Equal(board, PackedBoard.FromHex(hex));
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR")]
    [InlineData("8/8/8/4k3/8/8/8/4K2Q")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R")]
    public void FenRoundTrips(string placement)
    {
        Assert.Equal(placement, Fen.ToPlacement(Fen.Parse(placement + " w - - 0 1")));
    }

    [Fact]
    public void FenPutsA1AtSquareZero()
    {
        var board = Fen.Parse(Fen.StartPosition);
        Assert.True(board.TryGetPiece(0, out var a1));
        Assert.Equal(new Piece(PieceType.Rook, Colour.White), a1);
        Assert.True(board.TryGetPiece(60, out var e8));
        Assert.Equal(new Piece(PieceType.King, Colour.Black), e8);
    }

    [Theory]
    [InlineData("8/8/8/8/8/8/8")]
    [InlineData("9/8/8/8/8/8/8/8")]
    [InlineData("ppppppppp/8/8/8/8/8/8/8")]
    [InlineData("x7/8/8/8/8/8/8/8")]
    public void BadFenIsRejected(string fen)
    {
        Assert.Throws<FormatException>(() => Fen.Parse(fen));
    }

    [Fact]
    public void MeaninglessCodesHaveNoFen()
    {
        var board = new PackedBoard();
        board.SetCode(10, 0b111111);
        Assert.Null(Fen.ToPlacement(board));
    }
}
