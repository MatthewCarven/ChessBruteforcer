using System.Numerics;

namespace ChessBruteforcer.Core;

/// <summary>
/// Counts, exactly, how many boards survive each tier of <see cref="BoardChecker"/>,
/// without enumerating any of them.
///
/// Tiers 1-3 have closed forms.  Tier 4 (material) is a sum over every
/// "material vector" a side could have: for each pair of vectors, count the
/// ways to lay those pieces out, keeping pawns off the back ranks and
/// bishops on the colour they were counted as.  The per-side sums factor
/// apart, so the work is roughly (number of distinct side summaries)^2
/// BigInteger products rather than anything to do with 10^46 boards.
/// </summary>
public sealed class RangeCounter
{
    /// <summary>Empty plus the 12 pieces.</summary>
    public const int SquareStates = 13;

    private readonly BoardGeometry _geometry;
    private readonly MaterialRules _rules;
    private readonly BigInteger[] _factorial;

    public RangeCounter(BoardGeometry geometry, MaterialRules rules)
    {
        if (rules.Kings != 1)
            throw new ArgumentException("Counting assumes exactly one king per side.");
        _geometry = geometry;
        _rules = rules;
        _factorial = new BigInteger[geometry.SquareCount + 2 * MaxOfKind() + 1];
        _factorial[0] = BigInteger.One;
        for (int i = 1; i < _factorial.Length; i++)
            _factorial[i] = _factorial[i - 1] * i;
    }

    public static RangeCounter Standard { get; } = new(BoardGeometry.Standard, MaterialRules.Standard);

    /// <summary>Every bit pattern at all: 2^(bits x squares).</summary>
    public BigInteger RawBoards(int bitsPerSquare = SquareCodec.BitsPerSquare) =>
        BigInteger.Pow(2, bitsPerSquare * _geometry.SquareCount);

    /// <summary>Tier 1: 13^squares.</summary>
    public BigInteger ValidCodeBoards() => BigInteger.Pow(SquareStates, _geometry.SquareCount);

    /// <summary>Tier 2: place two kings, then 11 choices (empty or a non-king) everywhere else.</summary>
    public BigInteger KingBoards()
    {
        int n = _geometry.SquareCount;
        return n < 2 ? 0 : (BigInteger)n * (n - 1) * BigInteger.Pow(SquareStates - 2, n - 2);
    }

    /// <summary>
    /// Tier 3: as tier 2, but back-rank squares only have 9 choices (no pawns).
    /// Split by how many of the two kings stand on a back rank.
    /// </summary>
    public BigInteger PawnRankBoards()
    {
        int back = _geometry.CountSquares(true, true) + _geometry.CountSquares(true, false);
        int middle = _geometry.SquareCount - back;
        const int backChoices = SquareStates - 2 - 2;   // no kings, no pawns
        const int middleChoices = SquareStates - 2;     // no kings

        BigInteger total = 0;
        for (int kingsOnBack = 0; kingsOnBack <= 2; kingsOnBack++)
        {
            int kingsInMiddle = 2 - kingsOnBack;
            if (kingsOnBack > back || kingsInMiddle > middle)
                continue;
            // Ordered placements of (white king, black king).
            BigInteger kingWays = kingsOnBack switch
            {
                2 => (BigInteger)back * (back - 1),
                1 => 2 * (BigInteger)back * middle,
                _ => (BigInteger)middle * (middle - 1),
            };
            total += kingWays
                     * BigInteger.Pow(backChoices, back - kingsOnBack)
                     * BigInteger.Pow(middleChoices, middle - kingsInMiddle);
        }
        return total;
    }

    /// <summary>Tier 4: boards whose material both sides could explain.</summary>
    public BigInteger MaterialBoards()
    {
        // Each side's vectors, grouped by what the layout count depends on:
        // (pawns, light bishops, dark bishops, everything else).  The value
        // is sum(1 / prod(count!)), scaled by `scale` to stay integral.
        BigInteger scale = BigInteger.One;
        int maxPawns = _rules.Pawns;
        int maxPromoted = _rules.AllowPromotions ? _rules.Pawns : 0;
        foreach (int max in new[]
                 {
                     maxPawns, _rules.Kings, _rules.Queens + maxPromoted, _rules.Rooks + maxPromoted,
                     _rules.Knights + maxPromoted, _rules.LightBishops + maxPromoted,
                     _rules.DarkBishops + maxPromoted,
                 })
            scale *= _factorial[max];

        var side = new Dictionary<(int P, int L, int D, int O), BigInteger>();
        foreach (var m in SideVectors())
        {
            var key = (m.Pawns, m.LightBishops, m.DarkBishops, m.Kings + m.Queens + m.Rooks + m.Knights);
            BigInteger weight = scale / (_factorial[m.Pawns] * _factorial[m.LightBishops]
                                         * _factorial[m.DarkBishops] * _factorial[m.Kings]
                                         * _factorial[m.Queens] * _factorial[m.Rooks]
                                         * _factorial[m.Knights]);
            side[key] = side.GetValueOrDefault(key) + weight;
        }

        var both = new Dictionary<(int P, int L, int D, int O), BigInteger>();
        foreach (var (w, ww) in side)
        foreach (var (b, bw) in side)
        {
            var key = (w.P + b.P, w.L + b.L, w.D + b.D, w.O + b.O);
            both[key] = both.GetValueOrDefault(key) + ww * bw;
        }

        BigInteger scaledTotal = 0;
        foreach (var ((p, l, d, o), weight) in both)
        {
            BigInteger layouts = Layouts(p, l, d, o);
            if (!layouts.IsZero)
                scaledTotal += weight * layouts * _factorial[p] * _factorial[l] * _factorial[d];
        }

        BigInteger total = BigInteger.DivRem(scaledTotal, scale * scale, out var remainder);
        if (!remainder.IsZero)
            throw new InvalidOperationException("Material count did not divide exactly; the counting is wrong.");
        return total;
    }

    /// <summary>The count for a tier, with tier 0 meaning raw 6-bit boards.</summary>
    public BigInteger BoardsAtTier(int tier) => tier switch
    {
        Tiers.Raw => RawBoards(),
        Tiers.ValidCodes => ValidCodeBoards(),
        Tiers.Kings => KingBoards(),
        Tiers.PawnRanks => PawnRankBoards(),
        Tiers.Material => MaterialBoards(),
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

    /// <summary>Every piece count one side could have under the rules.</summary>
    public IEnumerable<SideMaterial> SideVectors()
    {
        for (int pawns = 0; pawns <= _rules.Pawns; pawns++)
        {
            var probe = new SideMaterial { Pawns = pawns };
            int budget = probe.PromotionsAvailable(_rules);
            for (int q = 0; q <= _rules.Queens + budget; q++)
            for (int r = 0; r <= _rules.Rooks + budget; r++)
            for (int n = 0; n <= _rules.Knights + budget; n++)
            for (int lb = 0; lb <= _rules.LightBishops + budget; lb++)
            for (int db = 0; db <= _rules.DarkBishops + budget; db++)
            {
                var m = new SideMaterial(_rules.Kings, pawns, n, lb, db, r, q);
                if (m.PromotionsNeeded(_rules) <= budget)
                    yield return m;
            }
        }
    }

    /// <summary>
    /// Ways to choose the squares for p pawns (middle ranks only), l light
    /// bishops and d dark bishops as unlabelled sets, times the ways to lay o
    /// other pieces out in order on what is left, summed over how the pawns
    /// split between light and dark squares.  The caller multiplies by p! l! d!
    /// and divides by every count! to say which piece is which.
    /// </summary>
    private BigInteger Layouts(int p, int l, int d, int o)
    {
        int middleLight = _geometry.CountSquares(false, true);
        int middleDark = _geometry.CountSquares(false, false);
        int light = middleLight + _geometry.CountSquares(true, true);
        int dark = middleDark + _geometry.CountSquares(true, false);
        int rest = _geometry.SquareCount - p - l - d;
        if (rest < o)
            return 0;

        BigInteger others = _factorial[rest] / _factorial[rest - o];
        BigInteger total = 0;
        for (int pawnsOnLight = 0; pawnsOnLight <= p; pawnsOnLight++)
        {
            int pawnsOnDark = p - pawnsOnLight;
            total += Choose(middleLight, pawnsOnLight) * Choose(middleDark, pawnsOnDark)
                     * Choose(light - pawnsOnLight, l) * Choose(dark - pawnsOnDark, d);
        }
        return total * others;
    }

    private BigInteger Choose(int n, int k) =>
        k < 0 || n < 0 || k > n ? BigInteger.Zero : _factorial[n] / (_factorial[k] * _factorial[n - k]);

    /// <summary>Generous bound on any count or combined count the sums can index.</summary>
    private int MaxOfKind() =>
        _rules.Kings + _rules.Pawns + _rules.Queens + _rules.Rooks + _rules.Knights
        + _rules.LightBishops + _rules.DarkBishops + 5 * _rules.Pawns;
}
