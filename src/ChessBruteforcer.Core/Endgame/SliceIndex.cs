using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Endgame;

/// <summary>What the solver needs from a numbering of positions: <see cref="TableIndex"/> or <see cref="SliceIndex"/>.</summary>
internal interface IPositionIndex
{
    Piece[] Slots { get; }
    long Size { get; }

    /// <summary>The number of the position with these squares (in slot order), or -1 if there is none.</summary>
    long Encode(ReadOnlySpan<int> squares, Colour sideToMove);

    /// <summary>The squares and side to move behind a number; false for a hole.</summary>
    bool Decode(long index, Span<int> squares, out Colour sideToMove);
}

/// <summary>
/// One slice of a table with pawns: the pawns fixed on given squares, every
/// other piece anywhere, and the side to move, numbered with no symmetry:
///
///   index = ((square(first non-pawn slot) * 64 + ...) * 64 + ...) * 2 + side to move
///
/// A pawn placement is never its own mirror image (no square is on a middle
/// file), so a table solved one placement per mirror pair, each placement on
/// its own like this, covers every position exactly once.  A slice's arrays
/// are then the size of the slice, not the table: for five pieces with one
/// pawn, 33.5 M slots against 947 M.
/// </summary>
internal sealed class SliceIndex : IPositionIndex
{
    private readonly int[] _pawnSlots;
    private readonly int[] _pawnSquares;
    private readonly int[] _otherSlots;
    private static readonly bool[] Touching = BuildTouching();

    public Piece[] Slots { get; }
    public long Size { get; }

    /// <summary>The pawns' squares, in the order of their slots.</summary>
    public IReadOnlyList<int> PawnSquares => _pawnSquares;

    public SliceIndex(Piece[] slots, int[] pawnSlots, int[] pawnSquares)
    {
        Slots = slots;
        _pawnSlots = pawnSlots;
        _pawnSquares = pawnSquares;
        _otherSlots = Enumerable.Range(0, slots.Length).Where(s => !pawnSlots.Contains(s)).ToArray();
        Size = (1L << (6 * _otherSlots.Length)) * 2;
    }

    public long Encode(ReadOnlySpan<int> squares, Colour sideToMove)
    {
        for (int i = 0; i < _pawnSlots.Length; i++)
        {
            if (squares[_pawnSlots[i]] != _pawnSquares[i])
                return -1;   // another slice
        }
        if (Touching[squares[0] * 64 + squares[1]])
            return -1;
        long index = 0;
        foreach (int slot in _otherSlots)
            index = index * 64 + squares[slot];
        return index * 2 + (int)sideToMove;
    }

    public bool Decode(long index, Span<int> squares, out Colour sideToMove)
    {
        sideToMove = (Colour)(index & 1);
        long rest = index >> 1;
        for (int i = _otherSlots.Length - 1; i >= 0; i--)
        {
            squares[_otherSlots[i]] = (int)(rest % 64);
            rest /= 64;
        }
        for (int i = 0; i < _pawnSlots.Length; i++)
            squares[_pawnSlots[i]] = _pawnSquares[i];

        ulong occupied = 0;
        for (int slot = 0; slot < Slots.Length; slot++)
        {
            ulong bit = 1UL << squares[slot];
            if ((occupied & bit) != 0)
                return false;
            occupied |= bit;
        }
        return !Touching[squares[0] * 64 + squares[1]];
    }

    /// <summary>
    /// The slices of a table with pawns, most advanced pawns first: each a
    /// placement of the pawns (in slot order), one of each mirror pair.
    /// Pawns only move forward, so every pawn move leads to a slice earlier
    /// in this list.
    /// </summary>
    public static List<int[]> Placements(Piece[] slots, int[] pawnSlots)
    {
        var placements = new List<int[]>();
        var squares = new int[pawnSlots.Length];
        void Place(int i)
        {
            if (i == pawnSlots.Length)
            {
                // One of each mirror pair: the one whose squares read first.
                for (int j = 0; j < squares.Length; j++)
                {
                    int mirrored = squares[j] ^ 7;
                    if (squares[j] != mirrored)
                    {
                        if (squares[j] < mirrored)
                            placements.Add((int[])squares.Clone());
                        return;
                    }
                }
                return;
            }
            for (int square = 8; square < 56; square++)   // never on the first or last rank
            {
                if (Array.IndexOf(squares, square, 0, i) >= 0)
                    continue;
                squares[i] = square;
                Place(i + 1);
            }
        }
        Place(0);
        int Progress(int[] placement) => Enumerable.Range(0, pawnSlots.Length).Sum(i =>
            slots[pawnSlots[i]].Colour == Colour.White ? placement[i] / 8 : 7 - placement[i] / 8);
        return placements.OrderByDescending(Progress).ToList();
    }

    private static bool[] BuildTouching()
    {
        var touching = new bool[64 * 64];
        for (int a = 0; a < 64; a++)
        {
            touching[a * 64 + a] = true;
            foreach (int b in Attacks.King[a])
                touching[a * 64 + b] = true;
        }
        return touching;
    }
}
