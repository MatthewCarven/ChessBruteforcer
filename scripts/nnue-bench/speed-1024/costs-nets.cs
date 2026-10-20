#:project ../../../src/ChessBruteforcer.Core/ChessBruteforcer.Core.csproj
#:property TargetFramework=net8.0
#:property PublishAot=false
// scripts/nnue-bench/costs.cs with trained nets as the arms instead of piece-square nets of 128/256/512:
// the accumulator update a move (Kiwipete perft 4 by make, with a net attached minus without) and one
// evaluation (64 positions x 5,000, as costs.cs; and 8 positions, whose accumulators fit in L1). Arms
// rotate round by round. Below-normal priority (the bot plays on this laptop).
//   dotnet run -c Release costs-nets.cs <numbers .txt> <rounds> <name>=<net .nnue> ...
using System.Diagnostics;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Engine.Nnue;
using ChessBruteforcer.Core.Game;

Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
var numbers = EvalParams.Load(args[0]);
int rounds = int.Parse(args[1]);
var nets = args[2..].Select(a => a.Split('=', 2)).Select(p => (Name: p[0], Net: (Network?)Network.Load(p[1]))).ToArray();
var arms = new[] { (Name: "none", Net: (Network?)null) }.Concat(nets).ToArray();
const string kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";

var rng = new Random(1);
var fens = new List<string>();
while (fens.Count < 64)
{
    var p = Position.FromFen(kiwipete);
    for (int ply = rng.Next(0, 40); ply > 0; ply--)
    {
        var moves = MoveGenerator.Legal(p);
        if (moves.Count == 0) break;
        p.MakeMove(moves[rng.Next(moves.Count)]);
    }
    fens.Add(p.ToFen());
}

long MakesIn(Position p, int depth)   // perft that counts every make, legality checks included
{
    long makes = 0;
    var list = new List<Move>(64);
    MoveGenerator.Pseudo(p, list);
    foreach (var move in list.ToArray())
    {
        var undo = p.MakeMove(move);
        makes++;
        if (!p.InCheck(Position.Opponent(p.SideToMove)) && depth > 1)
            makes += MakesIn(p, depth - 1);
        p.UnmakeMove(move, undo);
    }
    return makes;
}

double EvalNs(Network? net, int count, int repeats, ref int sink)
{
    var positions = fens.Take(count).Select(f => Position.FromFen(f, net)).ToArray();
    var clock = Stopwatch.StartNew();
    for (int repeat = 0; repeat < repeats; repeat++)
        foreach (var position in positions)
            sink += net is null ? Evaluation.Evaluate(position, numbers) : Evaluation.Evaluate(position);
    return clock.Elapsed.TotalNanoseconds / ((double)repeats * positions.Length);
}

var perft = arms.ToDictionary(a => a.Name, _ => new List<double>());
var eval64 = arms.ToDictionary(a => a.Name, _ => new List<double>());
var eval8 = arms.ToDictionary(a => a.Name, _ => new List<double>());
long makesCount = 0;
int sink = 0;
for (int round = 0; round < rounds; round++)
{
    var order = Enumerable.Range(0, arms.Length).Select(i => arms[(i + round) % arms.Length]).ToArray();
    foreach (var arm in order)
    {
        var p = Position.FromFen(kiwipete, arm.Net);
        var clock = Stopwatch.StartNew();
        makesCount = MakesIn(p, 4);
        perft[arm.Name].Add(clock.Elapsed.TotalNanoseconds / makesCount);
    }
    foreach (var arm in order.Reverse())
    {
        eval64[arm.Name].Add(EvalNs(arm.Net, 64, 5_000, ref sink));
        eval8[arm.Name].Add(EvalNs(arm.Net, 8, 40_000, ref sink));
    }
    Console.Error.WriteLine($"round {round + 1} done {DateTime.Now:HH:mm:ss}");
}

double Median(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2; }
Console.WriteLine($"Kiwipete perft 4 by make: {makesCount:N0} makes (legality checks included); {rounds} rounds, medians; sink {sink}");
Console.WriteLine("arm      make+unmake ns   accumulator update ns   evaluation ns (64 pos)   evaluation ns (8 pos)");
foreach (var arm in arms)
{
    double make = Median(perft[arm.Name]), update = make - Median(perft["none"]);
    Console.WriteLine($"{arm.Name,-8} {make,15:0.0} {(arm.Net is null ? "" : update.ToString("0.0")),23} {Median(eval64[arm.Name]),24:0.0} {Median(eval8[arm.Name]),23:0.0}");
}
Console.WriteLine("every round, make+unmake ns:");
foreach (var arm in arms)
    Console.WriteLine($"  {arm.Name,-8} {string.Join(' ', perft[arm.Name].Select(x => x.ToString("0.0")))}");
Console.WriteLine("every round, evaluation ns (64 pos):");
foreach (var arm in arms)
    Console.WriteLine($"  {arm.Name,-8} {string.Join(' ', eval64[arm.Name].Select(x => x.ToString("0.0")))}");
