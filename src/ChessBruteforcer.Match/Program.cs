using System.Globalization;
using ChessBruteforcer.Core.Match;

// Local engine-vs-engine matches.  Example:
//
//   match --engine name=new cmd="dotnet engines/new/ChessBruteforcer.Engine.dll" \
//         --engine name=sf1500 cmd=/usr/games/stockfish option.UCI_LimitStrength=true option.UCI_Elo=1500 \
//         --games 40 --tc 10+0.1 --concurrency 2 --pgn games.pgn
//
// Results are from the first engine's point of view.

const string Usage = """
    usage: match --engine <settings...> --engine <settings...> [options]

    engine settings:  name=<label>  cmd="<command line>"  option.<UCI option>=<value> ...
                      tc=<time control>  this engine's own, in place of --tc (time odds, or depth=<n> against depth=<m>)

    options:
      --games <n>            games to play (default 20; openings are played in colour-reversed pairs)
      --tc <base+inc>        time control in seconds, e.g. 10+0.1 (default), or movetime=<ms>, or depth=<plies> (no clock)
      --concurrency <n>      games played at once (default 1)
      --pgn <file>           append every game to this PGN file
      --tables <dir>         adjudicate with our endgame tables when they cover a position
      --sprt <elo0> <elo1>   stop early once an SPRT between the two hypotheses is decided
      --openings <file>      one opening per line as UCI moves (default: 16 built-in openings)
      --lag <ms>             charge every move this much more than the engine took, as a server far away does
    """;

var engines = new List<EngineSpec>();
int games = 20, concurrency = 1, lag = 0;
var clock = TimeControl.Parse("10+0.1");
string? pgn = null, tables = null;
(double, double)? sprt = null;
IReadOnlyList<string[]> openings = MatchRunner.DefaultOpenings;

try
{
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--engine":
                var settings = new List<string>();
                while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    settings.Add(args[++i]);
                engines.Add(EngineSpec.Parse(settings));
                break;
            case "--games": games = int.Parse(args[++i]); break;
            case "--tc": clock = TimeControl.Parse(args[++i]); break;
            case "--concurrency": concurrency = int.Parse(args[++i]); break;
            case "--pgn": pgn = args[++i]; break;
            case "--tables": tables = args[++i]; break;
            case "--lag": lag = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--sprt":
                sprt = (double.Parse(args[++i], CultureInfo.InvariantCulture),
                        double.Parse(args[++i], CultureInfo.InvariantCulture));
                break;
            case "--openings":
                openings = File.ReadLines(args[++i])
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith('#'))
                    .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .ToArray();
                break;
            default:
                throw new FormatException($"Unknown argument {args[i]}.");
        }
    }
    if (engines.Count != 2)
        throw new FormatException("Give exactly two --engine entries.");
}
catch (Exception e) when (e is FormatException or IndexOutOfRangeException or IOException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    Console.Error.WriteLine(Usage);
    return 1;
}

var runner = new MatchRunner(engines[0], engines[1], new MatchSettings
{
    Games = games,
    Clock = clock,
    Concurrency = concurrency,
    Openings = openings,
    PgnPath = pgn,
    TablePath = tables,
    Sprt = sprt,
    EventName = $"{engines[0].Name} vs {engines[1].Name}",
    LagMs = lag,
});

var (clockA, clockB) = (engines[0].Clock ?? clock, engines[1].Clock ?? clock);
Console.WriteLine($"{engines[0].Name} vs {engines[1].Name}: {games} games at " +
                  $"{(clockA == clockB ? clockA : $"{clockA} v {clockB}")}, {concurrency} at a time" +
                  (lag > 0 ? $", {lag} ms lag a move" : ""));
var stats = runner.Run(Console.WriteLine);
Console.WriteLine();
Console.WriteLine($"final: {engines[0].Name} vs {engines[1].Name}: {stats}");
Console.WriteLine($"searches: {engines[0].Name} {runner.SearchesA}; {engines[1].Name} {runner.SearchesB}");
return 0;
