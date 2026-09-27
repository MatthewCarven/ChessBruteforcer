namespace ChessBruteforcer.Core.Match;

/// <summary>
/// What a set of games says about engine A against engine B.
///
/// * Score: (wins + draws / 2) / games.
/// * Elo difference: the logistic rating gap that score implies,
///   -400 log10(1 / score - 1), with a 95% interval from the spread of the
///   per-game results.
/// * LOS (likelihood of superiority): the chance A really is stronger, from
///   wins and losses (draws say nothing about who is better).
/// * SPRT: a sequential test of "A is elo0 better" against "A is elo1 better",
///   which can stop a match as soon as the evidence is decisive.
/// </summary>
public sealed record MatchStats(int Wins, int Losses, int Draws)
{
    public int Games => Wins + Losses + Draws;

    public double Score => Games == 0 ? 0.5 : (Wins + Draws / 2.0) / Games;

    public static double EloFromScore(double score)
    {
        score = Math.Clamp(score, 1e-6, 1 - 1e-6);
        return -400 * Math.Log10(1 / score - 1);
    }

    public static double ScoreFromElo(double elo) => 1 / (1 + Math.Pow(10, -elo / 400));

    public double Elo => EloFromScore(Score);

    /// <summary>Variance of a single game's score (0, 0.5 or 1) around the mean.</summary>
    public double Variance
    {
        get
        {
            if (Games == 0) return 0;
            double s = Score;
            return (Wins * Math.Pow(1 - s, 2) + Draws * Math.Pow(0.5 - s, 2) + Losses * Math.Pow(s, 2)) / Games;
        }
    }

    /// <summary>The 95% confidence interval of the Elo difference.</summary>
    public (double Low, double High) EloInterval
    {
        get
        {
            if (Games == 0) return (double.NegativeInfinity, double.PositiveInfinity);
            double margin = 1.96 * Math.Sqrt(Variance / Games);
            return (EloFromScore(Score - margin), EloFromScore(Score + margin));
        }
    }

    public double LikelihoodOfSuperiority =>
        Wins + Losses == 0 ? 0.5 : 0.5 * (1 + Erf((Wins - Losses) / Math.Sqrt(2.0 * (Wins + Losses))));

    /// <summary>Log-likelihood ratio of elo1 over elo0 (normal approximation, as fishtest's simple form).</summary>
    public double LogLikelihoodRatio(double elo0, double elo1)
    {
        if (Games == 0 || Variance == 0) return 0;
        double s0 = ScoreFromElo(elo0), s1 = ScoreFromElo(elo1);
        return Games * (s1 - s0) * (2 * Score - s0 - s1) / (2 * Variance);
    }

    /// <summary>The SPRT stopping bounds for error rates alpha (false positive) and beta (false negative).</summary>
    public static (double Lower, double Upper) SprtBounds(double alpha = 0.05, double beta = 0.05) =>
        (Math.Log(beta / (1 - alpha)), Math.Log((1 - beta) / alpha));

    public MatchStats Add(double scoreForA) => scoreForA switch
    {
        1 => this with { Wins = Wins + 1 },
        0 => this with { Losses = Losses + 1 },
        _ => this with { Draws = Draws + 1 },
    };

    public override string ToString()
    {
        var (low, high) = EloInterval;
        return $"+{Wins} -{Losses} ={Draws}  score {Score:P1}  Elo {Elo:+0;-0;0} [{low:+0;-0;0}, {high:+0;-0;0}]  " +
               $"LOS {LikelihoodOfSuperiority:P1}";
    }

    /// <summary>Abramowitz and Stegun 7.1.26 (error below 1.5e-7), since .NET has no erf.</summary>
    private static double Erf(double x)
    {
        double sign = Math.Sign(x);
        x = Math.Abs(x);
        double t = 1 / (1 + 0.3275911 * x);
        double y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592)
                   * t * Math.Exp(-x * x);
        return sign * y;
    }
}
