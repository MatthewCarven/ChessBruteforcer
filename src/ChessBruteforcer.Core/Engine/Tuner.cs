using System.Globalization;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Engine;

/// <summary>
/// Texel tuning (Matthew, 2026-10-14): positions from real games, each with the game's result, and the
/// evaluation's numbers (<see cref="EvalParams"/>) moved until sigmoid(eval) predicts those results as well
/// as it can. The error is the mean of (result - 1 / (1 + 10^(-K eval / 400)))^2, result 1 / 0.5 / 0 for
/// white, eval for white. K is fitted once to the starting numbers, so the scale stays centipawns.
///
/// The evaluation is linear in its numbers (each piece adds its square's middlegame value times phase / 24
/// and its endgame value times the rest), so each position is kept as its pieces' parameter indexes, signs
/// and phase, and the gradient is exact. Adam, full batch, every thread.
/// </summary>
public sealed class Tuner
{
    private const int MaxPhase = 24;

    // Per position: its features at [Start[p], Start[p + 1]) (a middlegame parameter index, the endgame one
    // being 384 more, and +1 for white, -1 for black), its phase, its bishop-pair difference, its result.
    private readonly List<int> _start = new() { 0 };
    private readonly List<short> _index = new();
    private readonly List<sbyte> _sign = new();
    private readonly List<byte> _phase = new();
    private readonly List<sbyte> _pairs = new();
    private readonly List<float> _result = new();
    private readonly List<bool> _validation = new();

    // The same as arrays once the positions are in (Seal), for speed.
    private int[] _s = Array.Empty<int>();
    private short[] _i = Array.Empty<short>();
    private sbyte[] _g = Array.Empty<sbyte>();
    private byte[] _ph = Array.Empty<byte>();
    private sbyte[] _pr = Array.Empty<sbyte>();
    private float[] _r = Array.Empty<float>();
    private bool[] _v = Array.Empty<bool>();

    internal void Seal()
    {
        if (_r.Length == _result.Count)
            return;
        (_s, _i, _g, _ph, _pr, _r, _v) = (_start.ToArray(), _index.ToArray(), _sign.ToArray(), _phase.ToArray(), _pairs.ToArray(), _result.ToArray(), _validation.ToArray());
    }

    public int Count => _result.Count;

    public int TrainingCount => _validation.Count(v => !v);

    public int ValidationCount => Count - TrainingCount;

    /// <summary>The features of a position, for white, as the evaluation sees it.</summary>
    public void Add(Position position, double whiteResult, bool validation)
    {
        int phase = 0, whiteBishops = 0, blackBishops = 0;
        for (int square = 0; square < 64; square++)
        {
            var piece = position[square];
            if (piece.IsEmpty)
                continue;
            int type = (int)piece.Type;
            phase += type switch { 2 or 3 => 1, 4 => 2, 5 => 4, _ => 0 };
            bool white = piece.Colour == Colour.White;
            _index.Add((short)EvalParams.Index(true, type, white ? square ^ 56 : square));
            _sign.Add((sbyte)(white ? 1 : -1));
            if (piece.Type == PieceType.Bishop)
            {
                if (white) whiteBishops++;
                else blackBishops++;
            }
        }
        _start.Add(_index.Count);
        _phase.Add((byte)Math.Min(phase, MaxPhase));
        _pairs.Add((sbyte)((whiteBishops >= 2 ? 1 : 0) - (blackBishops >= 2 ? 1 : 0)));
        _result.Add((float)whiteResult);
        _validation.Add(validation);
    }

    /// <summary>White's evaluation of position <paramref name="p"/> with the numbers <paramref name="v"/> (not rounded).</summary>
    internal double Eval(int p, double[] v)
    {
        double mg = 0, eg = 0;
        for (int f = _s[p]; f < _s[p + 1]; f++)
        {
            mg += _g[f] * v[_i[f]];
            eg += _g[f] * v[_i[f] + 6 * 64];
        }
        mg += _pr[p] * v[EvalParams.BishopPairMgIndex];
        eg += _pr[p] * v[EvalParams.BishopPairEgIndex];
        return (mg * _ph[p] + eg * (MaxPhase - _ph[p])) / MaxPhase;
    }

    private static double Sigmoid(double k, double eval) => 1 / (1 + Math.Pow(10, -k * eval / 400));

    /// <summary>The mean squared error over the training or the validation positions.</summary>
    public double Error(double[] v, double k, bool validation)
    {
        Seal();
        var sums = new double[Environment.ProcessorCount];
        var counts = new long[sums.Length];
        Parallel.For(0, sums.Length, t =>
        {
            double sum = 0;
            long n = 0;
            for (int p = t; p < Count; p += sums.Length)
            {
                if (_v[p] != validation)
                    continue;
                double e = _r[p] - Sigmoid(k, Eval(p, v));
                sum += e * e;
                n++;
            }
            sums[t] = sum;
            counts[t] = n;
        });
        return sums.Sum() / Math.Max(1, counts.Sum());
    }

    /// <summary>The K that fits the training positions best with the numbers <paramref name="v"/> (golden section on [0.1, 4]).</summary>
    public double FitK(double[] v)
    {
        double a = 0.1, b = 4, ratio = (Math.Sqrt(5) - 1) / 2;
        double c = b - ratio * (b - a), d = a + ratio * (b - a);
        double fc = Error(v, c, false), fd = Error(v, d, false);
        for (int i = 0; i < 40; i++)
        {
            if (fc < fd) { b = d; d = c; fd = fc; c = b - ratio * (b - a); fc = Error(v, c, false); }
            else { a = c; c = d; fc = fd; d = a + ratio * (b - a); fd = Error(v, d, false); }
        }
        return (a + b) / 2;
    }

    /// <summary>The gradient of the training error with respect to every number.</summary>
    internal double[] Gradient(double[] v, double k)
    {
        Seal();
        int threads = Environment.ProcessorCount;
        var parts = new double[threads][];
        Parallel.For(0, threads, t =>
        {
            var g = new double[EvalParams.Count];
            double scale = k * Math.Log(10) / 400;
            for (int p = t; p < Count; p += threads)
            {
                if (_v[p])
                    continue;
                double s = Sigmoid(k, Eval(p, v));
                // d/dv of (r - s)^2 = -2 (r - s) s (1 - s) scale * d(eval)/dv
                double common = -2 * (_r[p] - s) * s * (1 - s) * scale;
                double mgWeight = common * _ph[p] / MaxPhase, egWeight = common * (MaxPhase - _ph[p]) / MaxPhase;
                for (int f = _s[p]; f < _s[p + 1]; f++)
                {
                    g[_i[f]] += _g[f] * mgWeight;
                    g[_i[f] + 6 * 64] += _g[f] * egWeight;
                }
                g[EvalParams.BishopPairMgIndex] += _pr[p] * mgWeight;
                g[EvalParams.BishopPairEgIndex] += _pr[p] * egWeight;
            }
            parts[t] = g;
        });
        var total = new double[EvalParams.Count];
        long n = TrainingCount;
        foreach (var g in parts)
            for (int i = 0; i < total.Length; i++)
                total[i] += g[i] / n;
        return total;
    }

    /// <summary>
    /// Adam from <paramref name="start"/> for <paramref name="epochs"/> full passes, learning rate
    /// <paramref name="rate"/> (centipawns a step, roughly). Calls <paramref name="report"/> every
    /// <paramref name="every"/> epochs with (epoch, training error, validation error) and returns the numbers
    /// with the lowest validation error seen at a report.
    /// </summary>
    public double[] Fit(double[] start, double k, int epochs, double rate, int every, Action<int, double, double> report)
    {
        var v = (double[])start.Clone();
        var m = new double[v.Length];
        var s = new double[v.Length];
        const double beta1 = 0.9, beta2 = 0.999, epsilon = 1e-8;
        var best = (double[])v.Clone();
        double bestError = Error(v, k, true);
        for (int epoch = 1; epoch <= epochs; epoch++)
        {
            var g = Gradient(v, k);
            for (int i = 0; i < v.Length; i++)
            {
                m[i] = beta1 * m[i] + (1 - beta1) * g[i];
                s[i] = beta2 * s[i] + (1 - beta2) * g[i] * g[i];
                double mHat = m[i] / (1 - Math.Pow(beta1, epoch)), sHat = s[i] / (1 - Math.Pow(beta2, epoch));
                v[i] -= rate * mHat / (Math.Sqrt(sHat) + epsilon);
            }
            if (epoch % every == 0 || epoch == epochs)
            {
                double validation = Error(v, k, true);
                report(epoch, Error(v, k, false), validation);
                if (validation < bestError)
                {
                    bestError = validation;
                    best = (double[])v.Clone();
                }
            }
        }
        return best;
    }

    /// <summary>Positions from a file of lines "FEN \t white's result \t game number" (from `tune positions`).</summary>
    public static Tuner Load(string path, int validationEvery = 10)
    {
        var tuner = new Tuner();
        foreach (string line in File.ReadLines(path))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 3 || line.StartsWith('#'))
                continue;
            long game = long.Parse(parts[2], CultureInfo.InvariantCulture);
            // Hold back whole games (positions from one game are alike), mixed so the split isn't by date.
            bool validation = (int)((ulong)(game * 2654435761L) % (ulong)validationEvery) == 0;
            tuner.Add(Position.FromFen(parts[0]), double.Parse(parts[1], CultureInfo.InvariantCulture), validation);
        }
        return tuner;
    }
}
