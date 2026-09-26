namespace ChessBruteforcer.Core.Endgame;

public enum OutcomeKind : byte
{
    Draw,
    Win,
    Loss,
}

/// <summary>
/// A solved result from the point of view of the side to move, with the
/// distance to mate in plies (half-moves) under best play by both sides:
/// the winner mates as fast as possible and the loser holds out as long as
/// possible.  Loss(0) means "checkmated right now".
/// </summary>
public readonly record struct Outcome(OutcomeKind Kind, int Plies)
{
    public static readonly Outcome Draw = new(OutcomeKind.Draw, 0);

    public static Outcome Win(int plies) => new(OutcomeKind.Win, plies);

    public static Outcome Loss(int plies) => new(OutcomeKind.Loss, plies);

    /// <summary>The same result seen by the player who made the move to reach it.</summary>
    public Outcome ForPreviousMover() => Kind switch
    {
        OutcomeKind.Win => Loss(Plies + 1),
        OutcomeKind.Loss => Win(Plies + 1),
        _ => Draw,
    };

    /// <summary>
    /// Higher is better for the side to move: fast wins, then draws, then
    /// slow losses.  Sorting moves by this is "sorting the tree by
    /// favourable outcome".
    /// </summary>
    public int Score => Kind switch
    {
        OutcomeKind.Win => 100_000 - Plies,
        OutcomeKind.Loss => -100_000 + Plies,
        _ => 0,
    };

    /// <summary>Full moves until mate (a win in 1 ply is mate in 1).</summary>
    public int MovesToMate => Kind == OutcomeKind.Win ? (Plies + 1) / 2 : Plies / 2;

    public override string ToString() => Kind switch
    {
        OutcomeKind.Win => $"win, mate in {MovesToMate} ({Plies} plies)",
        OutcomeKind.Loss when Plies == 0 => "loss, checkmated",
        OutcomeKind.Loss => $"loss, mated in {MovesToMate} ({Plies} plies)",
        _ => "draw",
    };
}
