#:project tree/src/ChessBruteforcer.Core/ChessBruteforcer.Core.csproj
#:property TargetFramework=net8.0
#:property PublishAot=false
// Scratch cost probe for NNUE.md: one thread, the engine's bench positions, every arm on the same tree,
// arms interleaved round by round (the laptop's speed drifts, and the bot is playing beside us).
using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Game;

int depth = args.Length > 0 ? int.Parse(args[0]) : 8;
int rounds = args.Length > 1 ? int.Parse(args[1]) : 3;
string? evalFile = args.Length > 2 ? args[2] : null;
if (evalFile is not null) Evaluation.Parameters = EvalParams.Load(evalFile);

Console.WriteLine($"Avx2 {Avx2.IsSupported}  Avx512BW {Avx512BW.IsSupported}  Vector512 accel {Vector512.IsHardwareAccelerated}  runtime {Environment.Version}");

string[] positions =
{
    "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
    "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
    "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
    "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
    "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
    "r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
};

var arms = new (string Name, int Mode, int Hidden)[]
{
    ("A  today's eval", 0, 0),
    ("B  eval twice", 1, 0),
    ("C  +768x128x2", 2, 128),
    ("C  +768x256x2", 2, 256),
    ("C  +768x512x2", 2, 512),
    ("D  +HalfKP 256x2-32-32", 3, 256),
};
var seconds = new double[arms.Length, rounds];
var nodes = new long[arms.Length];
var evals = new long[arms.Length];
var updates = new long[arms.Length];
var refreshes = new long[arms.Length];

// warm-up: every arm once at a shallow depth, so the JIT has tiered up
foreach (var arm in arms) Run(arm.Mode, arm.Hidden, Math.Max(4, depth - 3));

for (int r = 0; r < rounds; r++)
    for (int k = 0; k < arms.Length; k++)
    {
        int a = (k + r) % arms.Length;   // rotate the order each round
        var (s, n, e, u, f) = Run(arms[a].Mode, arms[a].Hidden, depth);
        seconds[a, r] = s; nodes[a] = n; evals[a] = e; updates[a] = u; refreshes[a] = f;
        Console.Error.WriteLine($"round {r + 1} {arms[a].Name,-24} {s,7:0.000}s");
    }

double Best(int a) => Enumerable.Range(0, rounds).Min(r => seconds[a, r]);
double Median(int a) { var x = Enumerable.Range(0, rounds).Select(r => seconds[a, r]).OrderBy(v => v).ToArray(); return x[x.Length / 2]; }

Console.WriteLine($"depth {depth}, {rounds} rounds; nodes {nodes[0]:N0} (same tree every arm: {nodes.All(n => n == nodes[0])}); evaluations {evals[0]:N0} = {(double)evals[0] / nodes[0]:0.00} a node");
double baseT = Median(0);
double evalCost = Median(1) - baseT;
Console.WriteLine($"{"arm",-26}{"median s",9}{"best s",9}{"knps",8}{"extra ns/eval",15}{"updates/node",14}{"refresh/node",14}");
for (int a = 0; a < arms.Length; a++)
{
    double extra = (Median(a) - baseT) / evals[0] * 1e9;
    Console.WriteLine($"{arms[a].Name,-26}{Median(a),9:0.000}{Best(a),9:0.000}{nodes[0] / Median(a) / 1000,8:0}{extra,15:0.0}{(double)updates[a] / nodes[0],14:0.00}{(double)refreshes[a] / nodes[0],14:0.000}");
}
Console.WriteLine($"today's evaluation: {evalCost / baseT * 100:0.0}% of search time, {evalCost / evals[0] * 1e9:0.0} ns each");
for (int a = 2; a < arms.Length; a++)
{
    double netT = Median(a) - evalCost;   // the network instead of today's evaluation
    Console.WriteLine($"{arms[a].Name.Substring(3),-22} in place of today's: {nodes[0] / netT / 1000,6:0} knps ({(baseT / netT - 1) * 100:+0;-0}% speed)");
}

(double, long, long, long, long) Run(int mode, int hidden, int d)
{
    Probe.Setup(mode, hidden);
    Probe.Evals = Probe.Updates = Probe.Refreshes = 0;
    long total = 0;
    var clock = Stopwatch.StartNew();
    foreach (string fen in positions)
    {
        var position = Position.FromFen(fen);
        Probe.Reset();
        var search = new Search(new TranspositionTable(16));
        total += search.Run(position, new SearchLimits { Depth = d }).Nodes;
    }
    return (clock.Elapsed.TotalSeconds, total, Probe.Evals, Probe.Updates, Probe.Refreshes);
}
