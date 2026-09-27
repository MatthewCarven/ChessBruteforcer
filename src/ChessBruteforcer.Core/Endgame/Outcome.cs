namespace ChessBruteforcer.Core.Endgame;

public enum OutcomeKind : byte
{
    Draw,
    Win,
    Loss,

    /// <summary>Not settled within <see cref="Outcome.Plies"/> plies: a longer win or loss, or a draw (a capped table).</summary>
    Beyond,
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

    /// <summary>What a table solved only to <paramref name="plies"/> says of a position it hasn't settled.</summary>
    public static Outcome Beyond(int plies) => new(OutcomeKind.Beyond, plies);

    /// <summary>
    /// The better of two results for the side to move.  A result beyond N
    /// might be a slow win, a draw or a slow loss, so it only loses to a
    /// win within N; against anything else the answer is beyond N too.
    /// </summary>
    public static Outcome Better(Outcome a, Outcome b)
    {
        if (a.Kind == OutcomeKind.Beyond && b.Kind == OutcomeKind.Beyond)
            return Beyond(Math.Min(a.Plies, b.Plies));
        if (a.Kind == OutcomeKind.Beyond)
            (a, b) = (b, a);
        if (b.Kind == OutcomeKind.Beyond)
            return a.Kind == OutcomeKind.Win && a.Plies <= b.Plies ? a : b;
        return a.Score >= b.Score ? a : b;
    }

    /// <summary>The same result seen by the player who made the move to reach it.</summary>
    public Outcome ForPreviousMover() => Kind switch
    {
        OutcomeKind.Win => Loss(Plies + 1),
        OutcomeKind.Loss => Win(Plies + 1),
        OutcomeKind.Beyond => Beyond(Plies + 1),
        _ => Draw,
    };

    /// <summary>
    /// Higher is better for the side to move: fast wins, then draws, then
    /// slow losses.  Sorting moves by this is "sorting the tree by
    /// favourable outcome".  Beyond N scores as a draw: it might be one.
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
        OutcomeKind.Beyond => $"not settled within {Plies} plies (a longer win or loss, or a draw)",
        _ => "draw",
    };
}
