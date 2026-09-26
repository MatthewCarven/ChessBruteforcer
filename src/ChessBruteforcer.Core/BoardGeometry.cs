namespace ChessBruteforcer.Core;

/// <summary>
/// The shape of a board: which squares are back ranks and which are light.
///
/// The real game is always 8x8.  Other sizes exist so the checker and the
/// counter can be run on boards small enough to brute-force, which is how the
/// counter's arithmetic is tested.
/// </summary>
public sealed record BoardGeometry(int Files, int Ranks)
{
    public static readonly BoardGeometry Standard = new(8, 8);

    public int SquareCount => Files * Ranks;

    public int File(int square) => square % Files;

    public int Rank(int square) => square / Files;

    /// <summary>Rank 1 and the last rank: pawns can never stand there.</summary>
    public bool IsBackRank(int square) => Rank(square) == 0 || Rank(square) == Ranks - 1;

    /// <summary>a1 is dark, so a square is light when file + rank is odd.</summary>
    public bool IsLight(int square) => (File(square) + Rank(square)) % 2 == 1;

    public int CountSquares(bool backRank, bool light) =>
        Enumerable.Range(0, SquareCount).Count(s => IsBackRank(s) == backRank && IsLight(s) == light);
}
