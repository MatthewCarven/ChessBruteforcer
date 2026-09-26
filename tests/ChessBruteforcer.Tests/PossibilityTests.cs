using System.Numerics;
using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Possibility;

namespace ChessBruteforcer.Tests;

public class PossibilityTests
{
    [Fact]
    public void SuperposedBitsDoubleTheSpace()
    {
        var reg = new BinaryRegister(3);
        Assert.Equal(8, reg.CalculatePossibilityCount());
        reg.SetBit(0, 1);
        Assert.Equal(4, reg.CalculatePossibilityCount());
        Assert.Equal(new[] { "100", "101", "110", "111" }, reg.EnumerateStates());
    }

    [Fact]
    public void GroupsMultiply()
    {
        var reg = BinaryRegister.FromPattern("1??");
        var group = new BinaryRegisterGroup(reg, new BinaryRegister(2));
        Assert.Equal(16, group.CalculatePossibilityCount());
        Assert.Equal(16, group.EnumerateStates().Distinct().Count());
        Assert.Equal(4.0, group.Entropy(), 9);
    }

    [Fact]
    public void WeightsLowerEntropyButNotTheCount()
    {
        var reg = new BinaryRegister(3);
        Assert.Equal(3.0, reg.Entropy(), 9);
        reg.SetBitProbability(0, 0.95);
        Assert.Equal(8, reg.CalculatePossibilityCount());
        Assert.Equal(2.286, reg.Entropy(), 3);
    }

    [Fact]
    public void LikelihoodOrderComesOutMostLikelyFirst()
    {
        var reg = new BinaryRegister(3);
        reg.SetBitProbability(0, 0.95);
        var ordered = reg.IterStatesByLikelihood().ToList();
        Assert.Equal(8, ordered.Count);
        Assert.StartsWith("1", ordered[0].State);
        Assert.Equal(0.95 * 0.25, ordered[0].Probability, 9);
        for (int i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i - 1].Probability >= ordered[i].Probability);
        Assert.Equal(1.0, ordered.Sum(s => s.Probability), 9);
        foreach (var (state, probability) in ordered)
            Assert.Equal(reg.ProbabilityOfState(state), probability, 9);
    }

    [Fact]
    public void LikelihoodOrderIsLazyOnHugeRegisters()
    {
        var reg = new BinaryRegister(500);
        reg.SetAllProbabilities(0.9);
        var top = reg.IterStatesByLikelihood().First();
        Assert.Equal(new string('1', 500), top.State);
    }

    [Fact]
    public void CollapseIsReproducibleAndLeavesTheRegisterAlone()
    {
        var reg = BinaryRegister.FromPattern("1?0?");
        Assert.Equal(reg.Collapse(42), reg.Collapse(42));
        Assert.Matches("^1[01]0[01]$", reg.Collapse(7));
        Assert.Equal("BinaryRegister('1?0?')", reg.ToString());
    }

    [Fact]
    public void BadInputIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new BinaryPossibility(2));
        Assert.Throws<ArgumentException>(() => new BinaryPossibility(null, 1.5));
        Assert.Throws<ArgumentException>(() => new BinaryRegister(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BinaryRegister(2).SetBit(2, 0));
    }

    [Fact]
    public void FullySuperposedBoardIsEvery48Bytes()
    {
        var board = new SuperposedBoard();
        Assert.Equal(BigInteger.Pow(2, 384), board.CountRawCompletions());
        Assert.Equal(BigInteger.Pow(13, 64), board.CountValidCodeCompletions());
        Assert.Equal(RangeCounter.Standard.ValidCodeBoards(), board.CountValidCodeCompletions());
    }

    [Fact]
    public void CollapsingBitsNarrowsEachSquare()
    {
        var board = new SuperposedBoard();
        // Occupied flag of a1 forced to 0: only the canonical empty code is left there.
        board.Register.SetBit(0, 0);
        Assert.Equal(new byte[] { SquareCodec.EmptyCode }, board.ValidCodesAt(0));
        // Occupied flag of b1 forced to 1: the 12 pieces are left.
        board.Register.SetBit(6, 1);
        Assert.Equal(12, board.ValidCodesAt(1).Count);
        Assert.Equal(BigInteger.Pow(13, 62) * 12, board.CountValidCodeCompletions());

        // Spare bit set on c1: nothing meaningful is possible any more.
        board.Register.SetBit(2 * 6 + 5, 1);
        Assert.Equal(0, board.CountValidCodeCompletions());
    }

    [Fact]
    public void ACollapsedBoardRoundTrips()
    {
        var packed = Fen.Parse(Fen.StartPosition);
        var superposed = SuperposedBoard.FromBoard(packed);
        Assert.Equal(1, superposed.CountRawCompletions());
        Assert.Equal(packed, superposed.Collapse(new Random(0)));
    }

    [Fact]
    public void CollapsingAmongValidCodesAlwaysPassesTierOne()
    {
        var board = new SuperposedBoard();
        var rng = new Random(3);
        for (int i = 0; i < 200; i++)
        {
            var result = BoardChecker.Standard.Check(board.CollapseToValidCodes(rng));
            Assert.False(result.Violations.HasFlag(Violation.InvalidCode));
        }
    }
}
