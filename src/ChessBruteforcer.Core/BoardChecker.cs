namespace ChessBruteforcer.Core;

/// <summary>Every reason a board can be rejected. A board may break several at once.</summary>
[Flags]
public enum Violation
{
    None = 0,

    /// <summary>Tier 1: a square holds one of the 51 meaningless 6-bit codes.</summary>
    InvalidCode = 1 << 0,

    /// <summary>Tier 2: a side does not have exactly one king.</summary>
    KingCount = 1 << 1,

    /// <summary>Tier 3: a pawn stands on the first or last rank.</summary>
    PawnOnBackRank = 1 << 2,

    /// <summary>Tier 4: a side has more pawns than it started with.</summary>
    TooManyPawns = 1 << 3,

    /// <summary>Tier 4: a side has more promoted pieces than it has missing pawns.</summary>
    ExcessMaterial = 1 << 4,
}

/// <summary>
/// The filters, in order.  Each tier keeps everything the earlier tiers kept
/// and throws some more away; <see cref="RangeCounter"/> counts what survives
/// each one.
/// </summary>
public static class Tiers
{
    public const int Raw = 0;
    public const int ValidCodes = 1;
    public const int Kings = 2;
    public const int PawnRanks = 3;
    public const int Material = 4;

    public static readonly IReadOnlyList<string> Names = new[]
    {
        "raw bits",
        "every square holds a real code",
        "exactly one king per side",
        "no pawns on the back ranks",
        "material explainable (promotions paid for by missing pawns)",
    };

    public static Violation ViolationsFor(int tier) => tier switch
    {
        ValidCodes => Violation.InvalidCode,
        Kings => Violation.KingCount,
        PawnRanks => Violation.PawnOnBackRank,
        Material => Violation.TooManyPawns | Violation.ExcessMaterial,
        _ => Violation.None,
    };

    /// <summary>The highest tier whose filter, and every earlier one, the board passes.</summary>
    public static int HighestPassed(Violation violations)
    {
        int tier = Raw;
        while (tier < Material && (violations & ViolationsFor(tier + 1)) == 0)
            tier++;
        return tier;
    }
}

public sealed record CheckResult(
    Violation Violations,
    IReadOnlyList<string> Problems,
    SideMaterial White,
    SideMaterial Black)
{
    public bool IsValid => Violations == Violation.None;

    public int HighestTierPassed => Tiers.HighestPassed(Violations);
}

/// <summary>
/// Cheap, position-only checks that throw away boards which could never
/// occur in a game.  None of them look at whose move it is or at checks;
/// every rule here is necessary for a legal position, none is sufficient.
/// </summary>
public sealed class BoardChecker
{
    public static readonly BoardChecker Standard = new(MaterialRules.Standard, BoardGeometry.Standard);

    public MaterialRules Rules { get; }
    public BoardGeometry Geometry { get; }

    public BoardChecker(MaterialRules rules, BoardGeometry geometry)
    {
        Rules = rules;
        Geometry = geometry;
    }

    public CheckResult Check(PackedBoard board)
    {
        if (Geometry != BoardGeometry.Standard)
            throw new InvalidOperationException("Packed boards are always 8x8.");

        var squares = new Piece[PackedBoard.SquareCount];
        var invalid = new List<string>();
        for (int i = 0; i < squares.Length; i++)
        {
            if (!board.TryGetPiece(i, out squares[i]))
                invalid.Add($"{PackedBoard.SquareName(i)}=0b{Convert.ToString(board.GetCode(i), 2).PadLeft(6, '0')}");
        }

        var result = Check(squares);
        if (invalid.Count == 0)
            return result;

        const int shown = 8;
        string listed = string.Join(", ", invalid.Take(shown))
                        + (invalid.Count > shown ? $" and {invalid.Count - shown} more" : "");
        var problems = new List<string> { $"{invalid.Count} meaningless square codes: {listed}." };
        problems.AddRange(result.Problems);
        return result with { Violations = result.Violations | Violation.InvalidCode, Problems = problems };
    }

    /// <summary>Check decoded squares, indexed rank * files + file from a1.</summary>
    public CheckResult Check(ReadOnlySpan<Piece> squares)
    {
        if (squares.Length != Geometry.SquareCount)
            throw new ArgumentException($"Expected {Geometry.SquareCount} squares, got {squares.Length}.");

        var violations = Violation.None;
        var problems = new List<string>();
        var white = new SideMaterial();
        var black = new SideMaterial();

        for (int i = 0; i < squares.Length; i++)
        {
            var piece = squares[i];
            if (piece.IsEmpty)
                continue;
            ref var side = ref piece.Colour == Colour.White ? ref white : ref black;
            switch (piece.Type)
            {
                case PieceType.King: side.Kings++; break;
                case PieceType.Queen: side.Queens++; break;
                case PieceType.Rook: side.Rooks++; break;
                case PieceType.Knight: side.Knights++; break;
                case PieceType.Bishop:
                    if (Geometry.IsLight(i)) side.LightBishops++;
                    else side.DarkBishops++;
                    break;
                case PieceType.Pawn:
                    side.Pawns++;
                    if (Geometry.IsBackRank(i))
                    {
                        violations |= Violation.PawnOnBackRank;
                        problems.Add($"{piece.Colour} pawn on back rank square {SquareName(i)}.");
                    }
                    break;
            }
        }

        CheckSide(Colour.White, white, ref violations, problems);
        CheckSide(Colour.Black, black, ref violations, problems);
        return new CheckResult(violations, problems, white, black);
    }

    private void CheckSide(Colour colour, SideMaterial side, ref Violation violations, List<string> problems)
    {
        if (side.Kings != Rules.Kings)
        {
            violations |= Violation.KingCount;
            problems.Add($"{colour} has {side.Kings} kings.");
        }
        if (side.Pawns > Rules.Pawns)
        {
            violations |= Violation.TooManyPawns;
            problems.Add($"{colour} has {side.Pawns} pawns (at most {Rules.Pawns}).");
        }
        int needed = side.PromotionsNeeded(Rules);
        int available = side.PromotionsAvailable(Rules);
        if (needed > available)
        {
            violations |= Violation.ExcessMaterial;
            problems.Add(Rules.AllowPromotions
                ? $"{colour} ({side}) needs {needed} promoted piece(s) but is only missing {available} pawn(s)."
                : $"{colour} ({side}) has more than its starting material.");
        }
    }

    private string SquareName(int square) =>
        Geometry == BoardGeometry.Standard
            ? PackedBoard.SquareName(square)
            : $"#{square}";
}
