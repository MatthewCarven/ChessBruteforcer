using ChessBruteforcer.Core.Endgame;

namespace ChessBruteforcer.Core.Match;

public sealed record MatchSettings
{
    public required int Games { get; init; }
    public required TimeControl Clock { get; init; }
    public int Concurrency { get; init; } = 1;
    public IReadOnlyList<string[]> Openings { get; init; } = MatchRunner.DefaultOpenings;
    public string? PgnPath { get; init; }
    public string? TablePath { get; init; }
    public (double Elo0, double Elo1)? Sprt { get; init; }
    public string EventName { get; init; } = "ChessBruteforcer match";
}

/// <summary>
/// Plays a match between engine A and engine B.  Each opening is played
/// twice with colours reversed, so neither engine gets the better side of an
/// opening.  Games run on several workers at once, each with its own pair of
/// engine processes.  Results are reported from A's point of view.
/// </summary>
public sealed class MatchRunner
{
    /// <summary>Short, balanced, mainstream openings (UCI moves), so games don't all repeat.</summary>
    public static readonly IReadOnlyList<string[]> DefaultOpenings = new[]
    {
        "e2e4 e7e5 g1f3 b8c6 f1b5 a7a6",      // Ruy Lopez
        "e2e4 e7e5 g1f3 b8c6 f1c4 f8c5",      // Italian
        "e2e4 c7c5 g1f3 d7d6 d2d4 c5d4",      // Open Sicilian
        "e2e4 c7c5 b1c3 b8c6 g2g3 g7g6",      // Closed Sicilian
        "e2e4 e7e6 d2d4 d7d5 b1c3 g8f6",      // French
        "e2e4 c7c6 d2d4 d7d5 e4e5 c8f5",      // Caro-Kann, advance
        "d2d4 d7d5 c2c4 e7e6 b1c3 g8f6",      // Queen's Gambit Declined
        "d2d4 d7d5 c2c4 c7c6 g1f3 g8f6",      // Slav
        "d2d4 g8f6 c2c4 g7g6 b1c3 f8g7",      // King's Indian
        "d2d4 g8f6 c2c4 e7e6 b1c3 f8b4",      // Nimzo-Indian
        "c2c4 e7e5 b1c3 g8f6 g2g3 d7d5",      // English
        "g1f3 d7d5 g2g3 g8f6 f1g2 e7e6",      // Reti
        "e2e4 d7d5 e4d5 d8d5 b1c3 d5a5",      // Scandinavian
        "e2e4 g8f6 e4e5 f6d5 d2d4 d7d6",      // Alekhine
        "d2d4 f7f5 g2g3 g8f6 f1g2 e7e6",      // Dutch
        "e2e4 e7e5 b1c3 g8f6 f2f4 d7d5",      // Vienna
    }.Select(line => line.Split(' ')).ToArray();

    private readonly EngineSpec _a;
    private readonly EngineSpec _b;
    private readonly MatchSettings _settings;
    private readonly object _lock = new();
    private MatchStats _stats = new(0, 0, 0);
    private int _nextGame;
    private bool _stopRequested;

    public MatchRunner(EngineSpec a, EngineSpec b, MatchSettings settings)
    {
        _a = a;
        _b = b;
        _settings = settings;
    }

    /// <summary>Play the match; <paramref name="log"/> receives a line per game and progress summaries.</summary>
    public MatchStats Run(Action<string> log)
    {
        var workers = Enumerable.Range(0, Math.Max(1, _settings.Concurrency))
            .Select(_ => Task.Run(() => Worker(log)))
            .ToArray();
        Task.WaitAll(workers);
        return _stats;
    }

    private void Worker(Action<string> log)
    {
        using var a = new UciPlayer(_a);
        using var b = new UciPlayer(_b);
        var adjudicator = _settings.TablePath is null ? null : new Tablebase(_settings.TablePath) { SolveMissing = false };

        while (true)
        {
            int game;
            lock (_lock)
            {
                if (_stopRequested || _nextGame >= _settings.Games)
                    return;
                game = _nextGame++;
            }

            // Games 2k and 2k+1 share an opening with colours reversed.
            var opening = _settings.Openings[(game / 2) % _settings.Openings.Count];
            bool aIsWhite = game % 2 == 0;
            var record = GamePlayer.Play(aIsWhite ? a : b, aIsWhite ? b : a, opening, _settings.Clock,
                                         game + 1, adjudicator);

            double scoreForA = record.Result switch
            {
                GameResult.WhiteWins => aIsWhite ? 1 : 0,
                GameResult.BlackWins => aIsWhite ? 0 : 1,
                _ => 0.5,
            };

            lock (_lock)
            {
                _stats = _stats.Add(scoreForA);
                if (_settings.PgnPath is not null)
                    File.AppendAllText(_settings.PgnPath, record.ToPgn(_settings.EventName, DateTime.Now));
                log($"game {game + 1,4}: {record.White} vs {record.Black}  {record.ResultText,-7} " +
                    $"{record.Termination}, {record.Moves.Count} plies");
                log($"           {_a.Name} vs {_b.Name}: {_stats}");
                if (_settings.Sprt is var (elo0, elo1))
                {
                    double llr = _stats.LogLikelihoodRatio(elo0, elo1);
                    var (lower, upper) = MatchStats.SprtBounds();
                    log($"           SPRT [{elo0}, {elo1}]: LLR {llr:0.00} (bounds {lower:0.00}, {upper:0.00})");
                    if (llr >= upper || llr <= lower)
                    {
                        _stopRequested = true;
                        log(llr >= upper
                            ? $"SPRT accepts H1: {_a.Name} is at least ~{elo1} Elo better"
                            : $"SPRT accepts H0: {_a.Name} is not {elo1} Elo better");
                    }
                }
            }
        }
    }
}
