namespace ChessBruteforcer.Core;

/// <summary>
/// The starting army of one side, and whether pawns may promote.
///
/// A side can always have fewer pieces than it started with (captures).  It
/// can only have <em>more</em> of a kind by promoting pawns, and every
/// promoted piece is a pawn that is no longer on the board.  So for each side:
///
///   promotions needed = sum over kinds of max(0, count - starting count)
///   promotions needed &lt;= starting pawns - pawns on board
///
/// Bishops are split by square colour: a side starts with one light and one
/// dark bishop, so two light-squared bishops already mean one was promoted.
/// </summary>
public sealed record MaterialRules
{
    public int Kings { get; init; } = 1;
    public int Pawns { get; init; } = 8;
    public int Knights { get; init; } = 2;
    public int LightBishops { get; init; } = 1;
    public int DarkBishops { get; init; } = 1;
    public int Rooks { get; init; } = 2;
    public int Queens { get; init; } = 1;
    public bool AllowPromotions { get; init; } = true;

    public static readonly MaterialRules Standard = new();

    /// <summary>
    /// The intuitive but wrong rule "never more than you started with".  Kept
    /// so the ranges can show how much promotions add back.
    /// </summary>
    public static readonly MaterialRules StandardNoPromotions = new() { AllowPromotions = false };
}

/// <summary>How much of each kind one side has on the board.</summary>
public record struct SideMaterial(
    int Kings, int Pawns, int Knights, int LightBishops, int DarkBishops, int Rooks, int Queens)
{
    public readonly int Bishops => LightBishops + DarkBishops;

    public readonly int Total => Kings + Pawns + Knights + Bishops + Rooks + Queens;

    /// <summary>How many of these pieces can only be explained by promotion.</summary>
    public readonly int PromotionsNeeded(MaterialRules rules) =>
        Excess(Knights, rules.Knights) + Excess(LightBishops, rules.LightBishops)
        + Excess(DarkBishops, rules.DarkBishops) + Excess(Rooks, rules.Rooks)
        + Excess(Queens, rules.Queens);

    /// <summary>Pawns missing from the board, each of which could have promoted.</summary>
    public readonly int PromotionsAvailable(MaterialRules rules) =>
        rules.AllowPromotions ? Math.Max(0, rules.Pawns - Pawns) : 0;

    private static int Excess(int count, int start) => Math.Max(0, count - start);

    public override readonly string ToString() =>
        $"K{Kings} Q{Queens} R{Rooks} B{LightBishops}+{DarkBishops} N{Knights} P{Pawns}";
}
