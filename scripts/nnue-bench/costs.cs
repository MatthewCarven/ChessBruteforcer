#:project ../../src/ChessBruteforcer.Core/ChessBruteforcer.Core.csproj
#:property TargetFramework=net8.0
#:property PublishAot=false
// What the network costs, apart from any search tree: the accumulator update a move (perft on Kiwipete
// with and without a net attached) and an evaluation (the forward pass against today's evaluation, on
// the same positions). Arms alternate round by round (this laptop changes speed minute to minute).
//   dotnet run -c Release costs.cs <numbers .txt> [rounds]
using System.Diagnostics;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Engine.Nnue;
using ChessBruteforcer.Core.Game;

var numbers = EvalParams.Load(args[0]);
int rounds = args.Length > 1 ? int.Parse(args[1]) : 5;
const string kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
var nets = new[] { 128, 256, 512 }.Select(h => Network.FromPieceSquares(numbers, h)).ToArray();

// Positions from random games, each with its accumulators ready, for the evaluation timings: few enough
// to stay in cache, as the search's one position does (4,096 of them timed the cache misses instead).
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

var perftTimes = new Dictionary<string, List<double>>();
var evalTimes = new Dictionary<string, List<double>>();
long makesCount = 0;
int sink = 0;
for (int round = 0; round < rounds; round++)
{
    foreach (var net in new Network?[] { null }.Concat(nets).OrderBy(_ => (round * 7 + _?.Hidden ?? 0) % 5))
    {
        string arm = net is null ? "none" : $"net{net.Hidden}";
        var p = Position.FromFen(kiwipete, net);
        var clock = Stopwatch.StartNew();
        makesCount = MakesIn(p, 4);
        double ns = clock.Elapsed.TotalNanoseconds / makesCount;
        (perftTimes.TryGetValue(arm, out var l) ? l : perftTimes[arm] = new()).Add(ns);
    }
    foreach (var net in new Network?[] { null }.Concat(nets).OrderBy(_ => (round * 3 + _?.Hidden ?? 0) % 5))
    {
        string arm = net is null ? "sf1" : $"net{net.Hidden}";
        var positions = fens.Select(f => Position.FromFen(f, net)).ToArray();
        var clock = Stopwatch.StartNew();
        for (int repeat = 0; repeat < 5_000; repeat++)
            foreach (var position in positions)
                sink += net is null ? Evaluation.Evaluate(position, numbers) : Evaluation.Evaluate(position);
        double ns = clock.Elapsed.TotalNanoseconds / (5_000.0 * positions.Length);
        (evalTimes.TryGetValue(arm, out var l) ? l : evalTimes[arm] = new()).Add(ns);
    }
}

double Median(List<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2; }
Console.WriteLine($"Kiwipete perft 4 by make: {makesCount:N0} makes (legality checks included); {rounds} rounds, medians; sink {sink}");
Console.WriteLine("arm      make+unmake ns   accumulator update ns   evaluation ns");
foreach (var arm in new[] { "none/sf1", "net128", "net256", "net512" })
{
    string pa = arm == "none/sf1" ? "none" : arm, ea = arm == "none/sf1" ? "sf1" : arm;
    double make = Median(perftTimes[pa]), update = make - Median(perftTimes["none"]);
    Console.WriteLine($"{arm,-8} {make,15:0.0} {(pa == "none" ? "" : update.ToString("0.0")),23} {Median(evalTimes[ea]),15:0.0}");
}
