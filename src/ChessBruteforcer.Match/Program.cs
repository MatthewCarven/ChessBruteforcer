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

    options:
      --games <n>            games to play (default 20; openings are played in colour-reversed pairs)
      --tc <base+inc>        time control in seconds, e.g. 10+0.1 (default), or movetime=<ms>
      --concurrency <n>      games played at once (default 1)
      --pgn <file>           append every game to this PGN file
      --tables <dir>         adjudicate with our endgame tables when they cover a position
      --sprt <elo0> <elo1>   stop early once an SPRT between the two hypotheses is decided
      --openings <file>      one opening per line as UCI moves (default: 16 built-in openings)
    """;

var engines = new List<EngineSpec>();
int games = 20, concurrency = 1;
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
});

Console.WriteLine($"{engines[0].Name} vs {engines[1].Name}: {games} games at {clock}, {concurrency} at a time");
var stats = runner.Run(Console.WriteLine);
Console.WriteLine();
Console.WriteLine($"final: {engines[0].Name} vs {engines[1].Name}: {stats}");
return 0;
