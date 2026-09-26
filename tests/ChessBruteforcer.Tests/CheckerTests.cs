using ChessBruteforcer.Core;

namespace ChessBruteforcer.Tests;

public class CheckerTests
{
    private static CheckResult Check(string fen) => BoardChecker.Standard.Check(Fen.Parse(fen));

    [Fact]
    public void StartPositionPasses()
    {
        var result = Check(Fen.StartPosition);
        Assert.True(result.IsValid);
        Assert.Equal(Tiers.Material, result.HighestTierPassed);
        Assert.Equal(16, result.White.Total);
    }

    [Fact]
    public void TwoQueensNeedAMissingPawn()
    {
        // Eight pawns and two queens: the second queen came from nowhere.
        var result = Check("4k3/8/8/8/8/8/PPPPPPPP/QQ2K3");
        Assert.Equal(Violation.ExcessMaterial, result.Violations);

        // Seven pawns: the eighth could have promoted.
        Assert.True(Check("4k3/8/8/8/8/8/1PPPPPPP/QQ2K3").IsValid);
    }

    [Fact]
    public void NineQueensIsFineWithNoPawnsLeft()
    {
        Assert.True(Check("4k3/8/8/8/8/8/QQQQQQQQ/Q3K3").IsValid);
        Assert.False(Check("4k3/8/8/8/8/Q7/QQQQQQQQ/Q3K3").IsValid);
    }

    [Fact]
    public void BishopsAreCountedBySquareColour()
    {
        // c1 and f1 are opposite colours: the normal pair.
        Assert.True(Check("4k3/8/8/8/8/8/PPPPPPPP/2B1KB2").IsValid);
        // c1 and e3 are both dark: one of them was promoted, and all 8 pawns are still here.
        var result = Check("4k3/8/8/8/8/4B3/PPPPPPPP/2B1K3");
        Assert.Equal(Violation.ExcessMaterial, result.Violations);
        Assert.Equal(2, result.White.DarkBishops);
    }

    [Fact]
    public void PawnsCannotStandOnTheBackRanks()
    {
        var result = Check("4k2P/8/8/8/8/8/8/4K3");
        Assert.Equal(Violation.PawnOnBackRank, result.Violations);
        Assert.Equal(Tiers.Kings, result.HighestTierPassed);
    }

    [Fact]
    public void EachSideNeedsExactlyOneKing()
    {
        Assert.Equal(Violation.KingCount, Check("8/8/8/8/8/8/8/4K3").Violations);
        Assert.Equal(Violation.KingCount, Check("4k3/8/8/8/8/8/8/K3K3").Violations);
        Assert.Equal(Tiers.ValidCodes, Check("8/8/8/8/8/8/8/8").HighestTierPassed);
    }

    [Fact]
    public void NinePawnsIsTooMany()
    {
        var result = Check("4k3/8/8/8/8/P7/PPPPPPPP/4K3");
        Assert.True(result.Violations.HasFlag(Violation.TooManyPawns));
    }

    [Fact]
    public void MeaninglessCodesStopAtTierZero()
    {
        var board = Fen.Parse(Fen.StartPosition);
        board.SetCode(PackedBoard.SquareIndex(4, 3), 0b100001);
        var result = BoardChecker.Standard.Check(board);
        Assert.Equal(Violation.InvalidCode, result.Violations);
        Assert.Equal(Tiers.Raw, result.HighestTierPassed);
        Assert.Contains("e4", result.Problems[0]);
    }

    [Fact]
    public void WithoutPromotionsAnyExtraPieceFails()
    {
        var checker = new BoardChecker(MaterialRules.StandardNoPromotions, BoardGeometry.Standard);
        Assert.False(checker.Check(Fen.Parse("4k3/8/8/8/8/8/8/QQ2K3")).IsValid);
        Assert.True(checker.Check(Fen.Parse(Fen.StartPosition)).IsValid);
    }

    [Fact]
    public void SeveralProblemsAreAllReported()
    {
        var result = Check("P7/8/8/8/8/8/8/QQ6");
        Assert.True(result.Violations.HasFlag(Violation.KingCount));
        Assert.True(result.Violations.HasFlag(Violation.PawnOnBackRank));
        Assert.False(result.Violations.HasFlag(Violation.ExcessMaterial));
    }
}
