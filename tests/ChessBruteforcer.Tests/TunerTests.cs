using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Tests;

/// <summary>Texel tuning (2026-10-14): the evaluation's numbers as a set, and the tuner's arithmetic.</summary>
public class TunerTests
{
    /// <summary>Positions from random play (seeded), not over, for the evaluation to judge.</summary>
    private static List<Position> RandomPositions(int count, int seed)
    {
        var rng = new Random(seed);
        var positions = new List<Position>();
        while (positions.Count < count)
        {
            var position = Position.Start();
            int plies = rng.Next(4, 100);
            for (int ply = 0; ply < plies; ply++)
            {
                var moves = MoveGenerator.Legal(position);
                if (moves.Count == 0)
                    break;
                position.MakeMove(moves[rng.Next(moves.Count)]);
            }
            if (MoveGenerator.HasLegalMove(position))
                positions.Add(Position.FromFen(position.ToFen()));
        }
        return positions;
    }

    [Fact]
    public void NumbersSurviveTextAndTheVector()
    {
        var original = EvalParams.Original();
        string path = Path.GetTempFileName();
        try
        {
            original.Save(path, "a test\nof two lines");
            Assert.Equal(original.ToVector(), EvalParams.Load(path).ToVector());
        }
        finally
        {
            File.Delete(path);
        }
        Assert.Equal(original.ToVector(), EvalParams.FromVector(original.ToVector()).ToVector());
    }

    [Fact]
    public void ALoadedSetIsTheOneUsed()
    {
        var position = Position.FromFen("4k3/8/8/8/4N3/8/8/4K3 w - - 0 1");   // a white knight on e4
        int before = Evaluation.Evaluate(position);
        var changed = EvalParams.Original();
        changed.Mg[2][4 * 8 + 4] += 50;   // e4, as seen from white with rank 8 first: rank 4 is row 4
        changed.Eg[2][4 * 8 + 4] += 50;
        Assert.Equal(before + 50, Evaluation.Evaluate(position, changed));
        // (Evaluation.Parameters, the process-wide set, isn't swapped here: tests run side by side.)
    }

    [Fact]
    public void TheTunersEvaluationIsTheEnginesForWhite()
    {
        var tuner = new Tuner();
        var positions = RandomPositions(300, 11);
        foreach (var position in positions)
            tuner.Add(position, 0.5, false);
        tuner.Seal();
        var v = EvalParams.Original().ToVector();
        for (int p = 0; p < positions.Count; p++)
        {
            int engine = Evaluation.Evaluate(positions[p]);
            int forWhite = positions[p].SideToMove == Colour.White ? engine : -engine;
            Assert.InRange(tuner.Eval(p, v) - forWhite, -1.0, 1.0);   // the engine rounds its blend to whole centipawns
        }
    }

    [Fact]
    public void TheGradientMatchesFiniteDifferences()
    {
        var rng = new Random(3);
        var tuner = new Tuner();
        foreach (var position in RandomPositions(200, 12))
            tuner.Add(position, rng.Next(3) / 2.0, false);
        var v = EvalParams.Original().ToVector();
        const double k = 1.1;
        var gradient = tuner.Gradient(v, k);
        int checkedCount = 0;
        foreach (int i in Enumerable.Range(0, EvalParams.Count).Where(i => Math.Abs(gradient[i]) > 1e-7).Take(40))
        {
            const double h = 0.01;
            var up = (double[])v.Clone();
            var down = (double[])v.Clone();
            up[i] += h;
            down[i] -= h;
            double numeric = (tuner.Error(up, k, false) - tuner.Error(down, k, false)) / (2 * h);
            Assert.InRange(numeric, gradient[i] - 1e-6 - Math.Abs(gradient[i]) * 1e-3, gradient[i] + 1e-6 + Math.Abs(gradient[i]) * 1e-3);
            checkedCount++;
        }
        Assert.True(checkedCount >= 20, $"only {checkedCount} numbers with a gradient");
    }

    [Fact]
    public void FittingFindsAKnightWorthMore()
    {
        // Expected scores from an evaluation where knights are worth 200 more: tuning from the original numbers
        // should raise the knights' values and lower the error. (Scores, not results drawn at random: 3,000
        // positions of 0s and 1s are too few for 770 numbers, and the tuner rightly keeps where it started.)
        var truth = EvalParams.Original();
        for (int i = 0; i < 64; i++)
        {
            truth.Mg[2][i] += 200;
            truth.Eg[2][i] += 200;
        }
        var rng = new Random(4);
        var tuner = new Tuner();
        foreach (var position in RandomPositions(3000, 13))
        {
            int score = Evaluation.Evaluate(position, truth);
            double forWhite = position.SideToMove == Colour.White ? score : -score;
            double expected = 1 / (1 + Math.Pow(10, -forWhite / 400));
            tuner.Add(position, expected, rng.Next(10) == 0);
        }
        var start = EvalParams.Original().ToVector();
        double before = tuner.Error(start, 1, false);
        var log = new List<string>();
        var fitted = EvalParams.FromVector(tuner.Fit(start, 1, 300, 2.0, 50, (e, t, v) => log.Add($"{e}: {t:0.0000} / {v:0.0000}")));
        double after = tuner.Error(fitted.ToVector(), 1, false);
        Assert.True(after < before, $"error {before:0.0000} -> {after:0.0000}; " + string.Join(", ", log));
        double knight(EvalParams p) => Enumerable.Range(0, 64).Average(i => p.Mg[2][i] + p.Eg[2][i]) / 2;
        Assert.True(knight(fitted) > knight(EvalParams.Original()) + 50, $"knights {knight(EvalParams.Original()):0} -> {knight(fitted):0}");
    }
}
