using System.Numerics;
using System.Text;

namespace ChessBruteforcer.Core.Possibility;

/// <summary>
/// An ordered register of <see cref="BinaryPossibility"/> bits, some of
/// which may be superposed.  Port of <c>BinaryRegister</c>.
///
/// Counts come back as <see cref="BigInteger"/> because a 48-byte board
/// register has 2^384 states, far past what any fixed-width integer holds.
/// </summary>
public sealed class BinaryRegister
{
    private readonly List<BinaryPossibility> _bits;

    /// <summary>A register of <paramref name="numBits"/> bits, all superposed.</summary>
    public BinaryRegister(int numBits)
    {
        if (numBits <= 0)
            throw new ArgumentException("Number of bits must be positive.");
        _bits = Enumerable.Range(0, numBits).Select(_ => new BinaryPossibility()).ToList();
    }

    /// <summary>A register from a pattern of '0', '1' and '?' (superposed).</summary>
    public static BinaryRegister FromPattern(string pattern)
    {
        var register = new BinaryRegister(pattern.Length);
        for (int i = 0; i < pattern.Length; i++)
        {
            register.SetBit(i, pattern[i] switch
            {
                '0' => 0,
                '1' => 1,
                '?' => null,
                _ => throw new ArgumentException("Pattern may only contain '0', '1' and '?'."),
            });
        }
        return register;
    }

    public int Length => _bits.Count;

    public IReadOnlyList<BinaryPossibility> Bits => _bits;

    public void AddBit() => _bits.Add(new BinaryPossibility());

    public void RemoveBit()
    {
        if (_bits.Count == 0)
            throw new InvalidOperationException("Cannot remove bit from empty register.");
        _bits.RemoveAt(_bits.Count - 1);
    }

    public int? GetBit(int index) => _bits[CheckIndex(index)].State;

    public void SetBit(int index, int? state) => _bits[CheckIndex(index)].State = state;

    public double GetBitProbability(int index) => _bits[CheckIndex(index)].P;

    public void SetBitProbability(int index, double p) => _bits[CheckIndex(index)].P = p;

    public void SetAllProbabilities(double p)
    {
        foreach (var bit in _bits)
            bit.P = p;
    }

    public bool IsFair => _bits.All(b => b.IsFair);

    public int SuperposedCount => _bits.Count(b => b.IsSuperposition);

    /// <summary>2^(superposed bits), without iterating. Weights never change it.</summary>
    public BigInteger CalculatePossibilityCount() =>
        _bits.Count == 0 ? BigInteger.Zero : BigInteger.Pow(2, SuperposedCount);

    /// <summary>Bits of real uncertainty; equals the superposed count when every bit is fair.</summary>
    public double Entropy() => _bits.Sum(b => b.Entropy());

    public double ProbabilityOfState(string state)
    {
        if (state.Length != _bits.Count)
            throw new ArgumentException($"State '{state}' does not match register length {_bits.Count}.");
        double total = 1.0;
        for (int i = 0; i < state.Length; i++)
        {
            if (state[i] is not ('0' or '1'))
                throw new ArgumentException("State may only contain '0' and '1'.");
            total *= _bits[i].ProbabilityOf(state[i] - '0');
        }
        return total;
    }

    /// <summary>Lazily yield every possible state in numeric order, one at a time.</summary>
    public IEnumerable<string> IterStates()
    {
        if (_bits.Count == 0)
            yield break;
        var free = Enumerable.Range(0, _bits.Count).Where(i => _bits[i].IsSuperposition).ToArray();
        var chars = _bits.Select(b => b.IsSuperposition ? '0' : (char)('0' + b.State!.Value)).ToArray();
        // Odometer over the superposed positions, rightmost fastest.
        while (true)
        {
            yield return new string(chars);
            int k = free.Length - 1;
            while (k >= 0 && chars[free[k]] == '1')
                chars[free[k--]] = '0';
            if (k < 0)
                yield break;
            chars[free[k]] = '1';
        }
    }

    /// <summary>
    /// Lazily yield (state, probability) most-likely-first: A* over the
    /// possibility tree, keyed on -log2(probability so far) plus the cheapest
    /// possible finish, so the top states of a huge space come back at once.
    /// </summary>
    public IEnumerable<(string State, double Probability)> IterStatesByLikelihood()
    {
        int depth = _bits.Count;
        if (depth == 0)
            yield break;

        var remaining = new double[depth + 1];
        for (int i = depth - 1; i >= 0; i--)
        {
            var bit = _bits[i];
            double cheapest = bit.IsSuperposition
                ? Math.Min(NegLog2(bit.ProbabilityOf(0)), NegLog2(bit.ProbabilityOf(1)))
                : 0.0;
            remaining[i] = remaining[i + 1] + cheapest;
        }

        // Priority: (estimate, insertion order) so ties stay stable like the Python heap.
        long counter = 0;
        var heap = new PriorityQueue<(double Cost, int Index, string Partial), (double, long)>();
        heap.Enqueue((0.0, 0, ""), (remaining[0], counter++));
        while (heap.TryDequeue(out var node, out _))
        {
            if (node.Index == depth)
            {
                yield return (node.Partial, Math.Pow(2.0, -node.Cost));
                continue;
            }
            var bit = _bits[node.Index];
            foreach (int value in bit.IsSuperposition ? new[] { 0, 1 } : new[] { bit.State!.Value })
            {
                double cost = bit.IsSuperposition ? node.Cost + NegLog2(bit.ProbabilityOf(value)) : node.Cost;
                heap.Enqueue((cost, node.Index + 1, node.Partial + (char)('0' + value)),
                             (cost + remaining[node.Index + 1], counter++));
            }
        }
    }

    public List<string> EnumerateStates() => IterStates().ToList();

    /// <summary>Collapse every superposed bit with a weighted coin. The register is untouched.</summary>
    public string Collapse(Random rng)
    {
        var sb = new StringBuilder(_bits.Count);
        foreach (var bit in _bits)
            sb.Append((char)('0' + bit.Collapse(rng)));
        return sb.ToString();
    }

    public string Collapse(int? seed = null) => Collapse(seed is int s ? new Random(s) : new Random());

    public override string ToString() =>
        $"BinaryRegister('{new string(_bits.Select(b => b.State is int s ? (char)('0' + s) : '?').ToArray())}')";

    private int CheckIndex(int index)
    {
        if ((uint)index >= (uint)_bits.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "Invalid bit index.");
        return index;
    }

    private static double NegLog2(double x) => x <= 0.0 ? double.PositiveInfinity : -Math.Log2(x);
}

/// <summary>Several registers treated as one combined system. Port of <c>BinaryRegisterGroup</c>.</summary>
public sealed class BinaryRegisterGroup
{
    private readonly List<BinaryRegister> _registers;

    public BinaryRegisterGroup(params BinaryRegister[] registers) => _registers = registers.ToList();

    public int Count => _registers.Count;

    public IReadOnlyList<BinaryRegister> Registers => _registers;

    public void AddRegister(BinaryRegister register) => _registers.Add(register);

    /// <summary>Possibility counts multiply across registers.</summary>
    public BigInteger CalculatePossibilityCount() =>
        _registers.Aggregate(BigInteger.One, (total, r) => total * r.CalculatePossibilityCount());

    /// <summary>Entropies add across registers.</summary>
    public double Entropy() => _registers.Sum(r => r.Entropy());

    /// <summary>Every combined state: the Cartesian product of the registers' states.</summary>
    public IEnumerable<string> IterStates()
    {
        IEnumerable<string> combined = new[] { "" };
        foreach (var register in _registers)
        {
            var states = register.EnumerateStates();
            combined = combined.SelectMany(prefix => states, (prefix, s) => prefix + s);
        }
        return combined;
    }

    public List<string> EnumerateStates() => IterStates().ToList();

    public List<string> Collapse(int? seed = null)
    {
        var rng = seed is int s ? new Random(s) : new Random();
        return _registers.Select(r => r.Collapse(rng)).ToList();
    }
}
