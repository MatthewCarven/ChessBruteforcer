using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Endgame;

/// <summary>
/// Numbers the positions of one material set, one number per position up to
/// symmetry.
///
/// Without pawns the board has 8 symmetries (rotations and reflections) and
/// none changes a result.  With pawns only the left-right mirror is safe,
/// since pawns can't turn around.  Of all the images of a position, the one
/// kept is the one whose squares, read in slot order (white king, black king,
/// then the rest), come first; that puts the white king in the a1-d1-d4
/// triangle without pawns, or on files a-d with them.
///
///   index = ((pair * 64 + square(slot 2)) * 64 + ...) * 2 + side to move
///
/// where pair numbers the placements of the two kings that can come first:
/// 462 without pawns, 1,806 with (adjacent kings are left out).  Placements
/// that aren't the first image of their position, or put two pieces on one
/// square, or a pawn on the first or last rank, are holes.
/// </summary>
public sealed class TableIndex : IPositionIndex
{
    private readonly int[][] _maps;              // square -> square, per symmetry
    private readonly int[] _pairOf = new int[64 * 64];
    private readonly (int White, int Black)[] _pairs;
    private readonly long _rest;                 // 64^(pieces - 2)

    public Material Material { get; }
    public Piece[] Slots { get; }

    /// <summary>How many symmetries the board has for this material: 8, or 2 with pawns.</summary>
    public int Symmetries => _maps.Length;

    public int KingPairs => _pairs.Length;

    public long Size { get; }

    public TableIndex(Material material)
    {
        Material = material;
        Slots = EndgameTable.Layout(material);
        _maps = material.HasPawns ? MirrorOnly : AllEight;
        _rest = 1L << (6 * (Slots.Length - 2));

        Array.Fill(_pairOf, -1);
        var pairs = new List<(int, int)>();
        var two = new int[2];
        for (int white = 0; white < 64; white++)
        {
            for (int black = 0; black < 64; black++)
            {
                if (white == black || Attacks.King[white].Contains(black))
                    continue;
                two[0] = white;
                two[1] = black;
                if (!IsFirstImage(two))
                    continue;
                _pairOf[white * 64 + black] = pairs.Count;
                pairs.Add((white, black));
            }
        }
        _pairs = pairs.ToArray();
        Size = _pairs.Length * _rest * 2;
    }

    /// <summary>
    /// The index of the position with these squares (in slot order), or -1
    /// if the kings stand on the same or neighbouring squares.  The squares
    /// are left as they were; <paramref name="symmetry"/> says which image was taken.
    /// </summary>
    public long Encode(ReadOnlySpan<int> squares, Colour sideToMove, out int symmetry)
    {
        symmetry = FirstImage(squares);
        var map = _maps[symmetry];
        int pair = _pairOf[map[squares[0]] * 64 + map[squares[1]]];
        if (pair < 0)
            return -1;
        long index = pair;
        for (int slot = 2; slot < squares.Length; slot++)
            index = index * 64 + map[squares[slot]];
        return index * 2 + (int)sideToMove;
    }

    public long Encode(ReadOnlySpan<int> squares, Colour sideToMove) => Encode(squares, sideToMove, out _);

    /// <summary>Where a square goes under symmetry number <paramref name="symmetry"/> (as returned by <see cref="Encode(ReadOnlySpan{int}, Colour, out int)"/>).</summary>
    public int Map(int symmetry, int square) => _maps[symmetry][square];

    /// <summary>
    /// The squares (in slot order) and side to move behind an index.  False
    /// for a hole: not the first image of its position, two pieces on one
    /// square, or a pawn on the first or last rank.
    /// </summary>
    public bool Decode(long index, Span<int> squares, out Colour sideToMove)
    {
        sideToMove = (Colour)(index & 1);
        long rest = index >> 1;
        for (int slot = Slots.Length - 1; slot >= 2; slot--)
        {
            squares[slot] = (int)(rest % 64);
            rest /= 64;
        }
        (squares[0], squares[1]) = _pairs[rest];

        ulong occupied = 0;
        for (int slot = 0; slot < Slots.Length; slot++)
        {
            ulong bit = 1UL << squares[slot];
            if ((occupied & bit) != 0)
                return false;
            occupied |= bit;
            if (Slots[slot].Type == PieceType.Pawn && squares[slot] / 8 is 0 or 7)
                return false;
        }
        return IsFirstImage(squares);
    }

    /// <summary>
    /// How many placements (in slot order) this one stands for: the number of
    /// symmetries, divided by how many of them leave it unchanged.
    /// </summary>
    public int Weight(ReadOnlySpan<int> squares)
    {
        int unchanged = 0;
        foreach (var map in _maps)
        {
            bool same = true;
            for (int slot = 0; slot < squares.Length && same; slot++)
                same = map[squares[slot]] == squares[slot];
            if (same)
                unchanged++;
        }
        return _maps.Length / unchanged;
    }

    /// <summary>No image reads earlier than the squares as they are (FirstImage only moves off 0 for a strictly earlier one).</summary>
    private bool IsFirstImage(ReadOnlySpan<int> squares) => FirstImage(squares) == 0;

    /// <summary>The symmetry whose image of these squares reads first (the lowest number, 0 = as they are, on a tie).</summary>
    private int FirstImage(ReadOnlySpan<int> squares)
    {
        int best = 0;
        for (int t = 1; t < _maps.Length; t++)
        {
            var candidate = _maps[t];
            var current = _maps[best];
            for (int slot = 0; slot < squares.Length; slot++)
            {
                int a = candidate[squares[slot]], b = current[squares[slot]];
                if (a == b)
                    continue;
                if (a < b)
                    best = t;
                break;
            }
        }
        return best;
    }

    private static int[] Build(Func<int, int, (int File, int Rank)> f)
    {
        var map = new int[64];
        for (int square = 0; square < 64; square++)
        {
            var (file, rank) = f(square % 8, square / 8);
            map[square] = rank * 8 + file;
        }
        return map;
    }

    private static readonly int[][] MirrorOnly =
    {
        Build((f, r) => (f, r)),
        Build((f, r) => (7 - f, r)),
    };

    private static readonly int[][] AllEight =
    {
        Build((f, r) => (f, r)),
        Build((f, r) => (7 - f, r)),
        Build((f, r) => (f, 7 - r)),
        Build((f, r) => (7 - f, 7 - r)),
        Build((f, r) => (r, f)),
        Build((f, r) => (7 - r, f)),
        Build((f, r) => (r, 7 - f)),
        Build((f, r) => (7 - r, 7 - f)),
    };
}
