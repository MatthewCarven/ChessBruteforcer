#:project probe-src/src/ChessBruteforcer.Core/ChessBruteforcer.Core.csproj
#:property TargetFramework=net8.0
#:property PublishAot=false
// Where a node's time goes with a network, on bench's six positions at a fixed depth, one thread.
// Per net: a counting run that records every evaluation's value, a check that replaying them gives the
// identical tree, then timed arms on that identical tree:
//   full = as the engine plays; upd = accumulator updates, evaluations replayed (no forward pass);
//   none = no updates and no forward pass (replayed). full - upd = the forward pass, upd - none = the
//   updates, none = the rest of the search.
// Time is this thread's own CPU time (QueryThreadCycleTime, TSC ticks at 2.496 GHz), so time spent waiting
// for the CPU (this process runs below normal, beside the bot) doesn't count; arms rotate search by
// search (one position at a time), and each arm's round is the sum over the six positions.
//   dotnet run -c Release probe.cs <depth> <rounds> <name>=<net .nnue> ...
using System.Diagnostics;
using System.Runtime.InteropServices;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Engine.Nnue;
using ChessBruteforcer.Core.Game;

Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
const double TscGhz = 2.496;
int depth = int.Parse(args[0]);
int rounds = int.Parse(args[1]);
var nets = args[2..].Select(a => a.Split('=', 2)).Select(p => (Name: p[0], Net: Network.Load(p[1]))).ToArray();
string[] fens =
{
    "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
    "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
    "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
    "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
    "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
    "r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
};

(long Nodes, double ThreadNs, double WallNs) RunOne(int index, Network net)
{
    var search = new Search(new TranspositionTable(16));
    var position = Position.FromFen(fens[index], net);
    nint thread = Native.GetCurrentThread();
    Native.QueryThreadCycleTime(thread, out ulong before);
    var clock = Stopwatch.StartNew();
    var result = search.Run(position, new SearchLimits { Depth = depth });
    double wall = clock.Elapsed.TotalNanoseconds;
    Native.QueryThreadCycleTime(thread, out ulong after);
    return (result.Nodes, (after - before) / TscGhz, wall);
}

var recorded = new Dictionary<string, (int[] Values, int[] Starts, long[] Nodes, bool[] Used, int[] PushStarts)>();
var perNode = new Dictionary<string, (double Pushes, double Evals)>();
foreach (var (name, net) in nets)
{
    Probe.Mode = 1;
    Probe.Counting = true;
    Probe.SkipUpdates = false;
    Probe.Values = new int[1 << 20];   // a fresh buffer: the last net's verify left its recording here
    Probe.Count = 0;
    Probe.UsedBits = new bool[1 << 23];
    Probe.PushSeq = 0;
    Probe.ResetCounters();
    var starts = new int[fens.Length + 1];
    var pushStarts = new int[fens.Length + 1];
    var nodesOf = new long[fens.Length];
    for (int i = 0; i < fens.Length; i++)
    {
        starts[i] = Probe.Count;
        pushStarts[i] = Probe.PushSeq;
        nodesOf[i] = RunOne(i, net).Nodes;
    }
    starts[^1] = Probe.Count;
    pushStarts[^1] = Probe.PushSeq;
    Probe.Counting = false;
    recorded[name] = (Probe.Values[..Probe.Count], starts, nodesOf, Probe.UsedBits[..Probe.PushSeq], pushStarts);
    long nodes = nodesOf.Sum(), pushes = Probe.Quiet + Probe.Capture + Probe.Castle;
    double n = nodes;
    perNode[name] = (pushes / n, Probe.Evals / n);
    Console.WriteLine($"{name} ({net}): {nodes:N0} nodes (Negamax {Probe.NegamaxNodes:N0}, quiescence {Probe.QsNodes:N0})");
    Console.WriteLine($"  a node: {pushes / n:0.000} pushes (quiet {Probe.Quiet / n:0.000}, capture {Probe.Capture / n:0.000}, castle {Probe.Castle / n:0.0000}), " +
                      $"{Probe.Evals / n:0.000} evaluations (quiescence stand-pat {Probe.QsEvals / n:0.000}, others {(Probe.Evals - Probe.QsEvals) / n:0.000})");
    Console.WriteLine($"  illegal makes {Probe.Illegal / n:0.000} a node ({100.0 * Probe.Illegal / pushes:0.0}% of pushes); bottom refreshes {Probe.BottomRefreshes}; pops {Probe.Pops:N0} v pushes {pushes:N0}");
    Console.WriteLine($"  pushes no evaluation ever needed: {100.0 * Probe.Unused / pushes:0.0}% ({100.0 * Probe.UnusedLeaf / pushes:0.0}% leaves, illegal makes included; " +
                      $"{100.0 * (Probe.Unused - Probe.UnusedLeaf) / pushes:0.0}% with children)");
    Console.WriteLine($"  a lazy update's chain: {(double)Probe.ChainLength / Math.Max(1, Probe.ChainEvals):0.000} updates per evaluation that needs any; " +
                      $"{100.0 * (Probe.Evals - Probe.ChainEvals) / Probe.Evals:0.0}% of evaluations need none (already computed)");

    Probe.Mode = 3;
    Probe.Values = recorded[name].Values;
    Probe.Next = 0;
    Probe.Mismatches = 0;
    long checkNodes = 0;
    for (int i = 0; i < fens.Length; i++)
        checkNodes += RunOne(i, net).Nodes;
    Console.WriteLine($"  replay check: {checkNodes:N0} nodes, {Probe.Next:N0} of {recorded[name].Values.Length:N0} values replayed, {Probe.Mismatches} mismatches");
    if (checkNodes != nodes || Probe.Next != recorded[name].Values.Length || Probe.Mismatches != 0)
        throw new Exception("the replay isn't the identical tree");

    // The ideal lazy update must give every evaluation exactly: the forward pass on its accumulators, checked.
    Probe.Mode = 3;
    Probe.IdealLazy = true;
    Probe.UsedBits = recorded[name].Used;
    Probe.PushSeq = 0;
    Probe.Next = 0;
    Probe.Mismatches = 0;
    long lazyNodes = 0;
    for (int i = 0; i < fens.Length; i++)
        lazyNodes += RunOne(i, net).Nodes;
    Probe.IdealLazy = false;
    Console.WriteLine($"  ideal lazy check: {lazyNodes:N0} nodes, {Probe.PushSeq:N0} of {recorded[name].Used.Length:N0} pushes seen, " +
                      $"{recorded[name].Used.Count(u => !u):N0} of them skipped, {Probe.Mismatches} evaluations wrong");
    if (lazyNodes != nodes || Probe.PushSeq != recorded[name].Used.Length || Probe.Mismatches != 0)
        throw new Exception("the ideal lazy update isn't exact");
}

string[] armNames = { "full", "lazy", "upd", "none" };
var combos = nets.SelectMany(n => armNames.Select(a => (Net: n, Arm: a))).ToArray();
var thread = combos.ToDictionary(c => (c.Net.Name, c.Arm), _ => new double[rounds + 1]);
var wall = combos.ToDictionary(c => (c.Net.Name, c.Arm), _ => new double[rounds + 1]);
for (int round = 0; round <= rounds; round++)   // round 0 warms up and is dropped
{
    for (int i = 0; i < fens.Length; i++)
    {
        int shift = round * 5 + i;
        foreach (var (net, arm) in Enumerable.Range(0, combos.Length).Select(k => combos[(k + shift) % combos.Length]))
        {
            var rec = recorded[net.Name];
            Probe.Mode = arm is "full" or "lazy" ? 0 : 2;
            Probe.SkipUpdates = arm == "none";
            Probe.IdealLazy = arm == "lazy";
            Probe.UsedBits = rec.Used;
            Probe.PushSeq = rec.PushStarts[i];
            Probe.Values = rec.Values;
            Probe.Next = rec.Starts[i];
            var (nodes, threadNs, wallNs) = RunOne(i, net.Net);
            Probe.SkipUpdates = false;
            Probe.IdealLazy = false;
            if (nodes != rec.Nodes[i] || (arm is "upd" or "none" && Probe.Next != rec.Starts[i + 1])
                || (arm == "lazy" && Probe.PushSeq != rec.PushStarts[i + 1]))
                throw new Exception($"{net.Name} {arm} position {i}: not the identical tree ({nodes:N0} nodes)");
            thread[(net.Name, arm)][round] += threadNs;
            wall[(net.Name, arm)][round] += wallNs;
        }
    }
    Console.Error.WriteLine($"round {round} done {DateTime.Now:HH:mm:ss}");
}

double Median(IEnumerable<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2; }
foreach (var (title, table) in new[] { ("this thread's CPU time", thread), ("wall-clock time", wall) })
{
    Console.WriteLine();
    Console.WriteLine($"{title}, ns a node, medians of {rounds} rounds (and every round):");
    foreach (var c in combos)
    {
        double nodes = recorded[c.Net.Name].Nodes.Sum();
        var perRound = table[(c.Net.Name, c.Arm)].Skip(1).Select(t => t / nodes).ToList();
        Console.WriteLine($"  {c.Net.Name,-6} {c.Arm,-5} {Median(perRound),7:0.0}   {string.Join(' ', perRound.Select(x => x.ToString("0")))}");
    }
    Console.WriteLine("net     ns/node   forward pass       updates            rest of the search   (medians of per-round differences)");
    foreach (var (name, _) in nets)
    {
        double nodes = recorded[name].Nodes.Sum();
        var full = table[(name, "full")].Skip(1).Select(t => t / nodes).ToList();
        var upd = table[(name, "upd")].Skip(1).Select(t => t / nodes).ToList();
        var none = table[(name, "none")].Skip(1).Select(t => t / nodes).ToList();
        double f = Median(full);
        double forward = Median(full.Zip(upd, (a, b) => a - b)), update = Median(upd.Zip(none, (a, b) => a - b)), rest = Median(none);
        Console.WriteLine($"{name,-6} {f,8:0.0}   {forward,6:0.0} ({100 * forward / f,4:0.0}%)    {update,6:0.0} ({100 * update / f,4:0.0}%)    {rest,6:0.0} ({100 * rest / f,4:0.0}%)" +
                          $"   in place: {update / perNode[name].Pushes:0.0} ns a push, {forward / perNode[name].Evals:0.0} ns an evaluation");
        var lazy = table[(name, "lazy")].Skip(1).Select(t => t / nodes).ToList();
        double saved = Median(full.Zip(lazy, (a, b) => a - b));
        Console.WriteLine($"{"",-6} ideal lazy update: {Median(lazy):0.0} ns a node, {saved:0.0} ns saved ({100 * saved / f:0.0}% of a node, {100 * saved / update:0.0}% of the updates' cost; " +
                          $"every round: {string.Join(' ', full.Zip(lazy, (a, b) => (a - b).ToString("0")))})");
    }
}

static class Native
{
    [DllImport("kernel32.dll")]
    public static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryThreadCycleTime(nint thread, out ulong cycles);
}
