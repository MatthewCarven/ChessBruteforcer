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
///
/// Identical pieces (two white rooks, say) are one position whichever of them
/// stands where, so a run of them is stored once, as the set of their squares:
/// one digit of C(64, n) (2,016 for a pair, 41,664 for three) in place of n
/// digits of 64, and every image is read with each run's squares in order.
/// That halves a table with a pair and takes five sixths off one with three.
/// Tables numbered the first way, every order of every run ("ordered"), still
/// load: they are renumbered as they are read.
/// </summary>
public sealed class TableIndex : IPositionIndex
{
    private readonly int[][] _maps;              // square -> square, per symmetry
    private readonly int[] _pairOf = new int[64 * 64];
    private readonly (int White, int Black)[] _pairs;
    private readonly long _rest;                 // the digits after the king pair, multiplied out
    // The slots after the kings in runs of identical pieces: each run's first slot and length.
    private readonly (int Start, int Length)[] _runs;
    private readonly bool _identical;            // some run has two or more pieces

    public Material Material { get; }
    public Piece[] Slots { get; }

    /// <summary>How many symmetries the board has for this material: 8, or 2 with pawns.</summary>
    public int Symmetries => _maps.Length;

    public int KingPairs => _pairs.Length;

    public long Size { get; }

    /// <param name="ordered">Number identical pieces in every order, one digit each (the first way, before 2026-09-28).</param>
    public TableIndex(Material material, bool ordered = false)
    {
        Material = material;
        Slots = EndgameTable.Layout(material);
        _maps = material.HasPawns ? MirrorOnly : AllEight;
        _runs = Runs(Slots, ordered);
        _identical = _runs.Any(run => run.Length > 1);
        _rest = _runs.Aggregate(1L, (product, run) => product * Base(run.Length));

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
                if (FirstImagePlain(two) != 0)
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
        if (!_identical)
        {
            symmetry = FirstImagePlain(squares);
            var map = _maps[symmetry];
            int pair = _pairOf[map[squares[0]] * 64 + map[squares[1]]];
            if (pair < 0)
                return -1;
            long index = pair;
            for (int slot = 2; slot < squares.Length; slot++)
                index = index * 64 + map[squares[slot]];
            return index * 2 + (int)sideToMove;
        }

        Span<int> best = stackalloc int[squares.Length];
        Span<int> candidate = stackalloc int[squares.Length];
        symmetry = FirstImageSorted(squares, best, candidate);
        int kings = _pairOf[best[0] * 64 + best[1]];
        if (kings < 0)
            return -1;
        long result = kings;
        foreach (var (start, length) in _runs)
            result = result * Base(length) + (length == 1 ? best[start] : Rank(best.Slice(start, length)));
        return result * 2 + (int)sideToMove;
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
        for (int r = _runs.Length - 1; r >= 0; r--)
        {
            var (start, length) = _runs[r];
            long digitBase = Base(length);
            long digit = rest % digitBase;
            rest /= digitBase;
            if (length == 1)
                squares[start] = (int)digit;
            else
                Unrank(digit, squares.Slice(start, length));
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
        Span<int> original = stackalloc int[squares.Length];
        Span<int> image = stackalloc int[squares.Length];
        Image(0, squares, original);
        int unchanged = 0;
        for (int t = 0; t < _maps.Length; t++)
        {
            Image(t, squares, image);
            if (image.SequenceEqual(original))
                unchanged++;
        }
        return _maps.Length / unchanged;
    }

    /// <summary>No image reads earlier than the squares as they are (the symmetry only moves off 0 for a strictly earlier one).</summary>
    private bool IsFirstImage(ReadOnlySpan<int> squares)
    {
        if (!_identical)
            return FirstImagePlain(squares) == 0;
        Span<int> best = stackalloc int[squares.Length];
        Span<int> candidate = stackalloc int[squares.Length];
        return FirstImageSorted(squares, best, candidate) == 0 && best.SequenceEqual(squares);
    }

    /// <summary>
    /// With identical pieces: the symmetry whose image reads first once each
    /// run's squares are put in order, and that image in <paramref name="best"/>.
    /// </summary>
    private int FirstImageSorted(ReadOnlySpan<int> squares, Span<int> best, Span<int> candidate)
    {
        int symmetry = 0;
        Image(0, squares, best);
        for (int t = 1; t < _maps.Length; t++)
        {
            Image(t, squares, candidate);
            if (candidate.SequenceCompareTo(best) < 0)
            {
                candidate.CopyTo(best);
                symmetry = t;
            }
        }
        return symmetry;
    }

    /// <summary>The squares under symmetry <paramref name="t"/>, each run of identical pieces in order.</summary>
    private void Image(int t, ReadOnlySpan<int> squares, Span<int> into)
    {
        var map = _maps[t];
        for (int slot = 0; slot < squares.Length; slot++)
            into[slot] = map[squares[slot]];
        foreach (var (start, length) in _runs)
        {
            for (int i = start + 1; i < start + length; i++)   // insertion sort: runs are two or three long
            {
                int square = into[i];
                int j = i - 1;
                for (; j >= start && into[j] > square; j--)
                    into[j + 1] = into[j];
                into[j + 1] = square;
            }
        }
    }

    /// <summary>The slots after the kings, grouped into runs of identical pieces (all runs of one if ordered).</summary>
    private static (int Start, int Length)[] Runs(Piece[] slots, bool ordered)
    {
        var runs = new List<(int, int)>();
        for (int slot = 2; slot < slots.Length;)
        {
            int length = 1;
            while (!ordered && slot + length < slots.Length && slots[slot + length] == slots[slot])
                length++;
            runs.Add((slot, length));
            slot += length;
        }
        return runs.ToArray();
    }

    // Binomials up to C(64, 8), for runs of up to eight identical pieces.
    private static readonly long[,] Choose = BuildChoose();

    private static long[,] BuildChoose()
    {
        var choose = new long[65, 9];
        for (int n = 0; n <= 64; n++)
        {
            choose[n, 0] = 1;
            for (int k = 1; k <= Math.Min(n, 8); k++)
                choose[n, k] = choose[n - 1, k - 1] + (k <= n - 1 ? choose[n - 1, k] : 0);
        }
        return choose;
    }

    /// <summary>How many values one run's digit takes: 64 squares, or C(64, n) sets of n.</summary>
    private static long Base(int length) => length == 1 ? 64 : Choose[64, length];

    /// <summary>The rank of a set of squares in increasing order: sum of C(square_i, i + 1).</summary>
    private static long Rank(ReadOnlySpan<int> sorted)
    {
        long rank = 0;
        for (int i = 0; i < sorted.Length; i++)
            rank += Choose[sorted[i], i + 1];
        return rank;
    }

    /// <summary>The set of squares (in increasing order) with this rank.</summary>
    private static void Unrank(long rank, Span<int> into)
    {
        for (int i = into.Length - 1; i >= 0; i--)
        {
            int square = 63;
            while (Choose[square, i + 1] > rank)
                square--;
            into[i] = square;
            rank -= Choose[square, i + 1];
        }
    }

    /// <summary>The symmetry whose image of these squares reads first (the lowest number, 0 = as they are, on a tie).</summary>
    private int FirstImagePlain(ReadOnlySpan<int> squares)
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
