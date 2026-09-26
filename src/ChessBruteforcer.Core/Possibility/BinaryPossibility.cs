namespace ChessBruteforcer.Core.Possibility;

/// <summary>
/// A single bit whose state is 0, 1, or null (superposition: both remain
/// possible).  C# port of <c>BinaryPossibility</c> from
/// https://github.com/MatthewCarven/PythonBinaryPossibility.
///
/// A superposed bit carries a probability <see cref="P"/> that it collapses
/// to 1, fair (0.5) by default.  The probability is remembered while the bit
/// is collapsed, so re-superposing restores its bias.  A biased coin, not a
/// qubit: no amplitudes, no interference.
/// </summary>
public sealed class BinaryPossibility
{
    private int? _state;
    private double _p;

    public BinaryPossibility(int? state = null, double p = 0.5)
    {
        State = state;
        P = p;
    }

    /// <summary>0, 1, or null for superposition.</summary>
    public int? State
    {
        get => _state;
        set
        {
            if (value is not (null or 0 or 1))
                throw new ArgumentException("Invalid state. Must be 0, 1, or null.");
            _state = value;
        }
    }

    /// <summary>Probability this bit collapses to 1 while superposed, in [0, 1].</summary>
    public double P
    {
        get => _p;
        set
        {
            if (double.IsNaN(value) || value < 0.0 || value > 1.0)
                throw new ArgumentException("Probability must be between 0.0 and 1.0.");
            _p = value;
        }
    }

    public bool IsSuperposition => _state is null;

    public bool IsFair => _p == 0.5;

    /// <summary>Probability of yielding <paramref name="value"/> on collapse; certain once collapsed.</summary>
    public double ProbabilityOf(int value)
    {
        if (value is not (0 or 1))
            throw new ArgumentException("Value must be 0 or 1.");
        if (_state is int s)
            return s == value ? 1.0 : 0.0;
        return value == 1 ? _p : 1.0 - _p;
    }

    /// <summary>Bits of uncertainty: 1.0 for a fair superposed bit, 0.0 once collapsed.</summary>
    public double Entropy() => _state is null ? BinaryEntropy(_p) : 0.0;

    /// <summary>Roll a concrete 0 or 1, honouring the odds. Does not change the bit.</summary>
    public int Collapse(Random rng) => _state ?? (rng.NextDouble() < _p ? 1 : 0);

    public BinaryPossibility Clone() => new(_state, _p);

    public override string ToString() => _state switch
    {
        null when IsFair => "Possibility: (0 & 1)",
        null => $"Possibility: (0 & 1) p={_p:0.00}",
        _ => $"Possibility: {_state}",
    };

    internal static double BinaryEntropy(double p) =>
        p <= 0.0 || p >= 1.0 ? 0.0 : -(p * Math.Log2(p) + (1.0 - p) * Math.Log2(1.0 - p));
}
