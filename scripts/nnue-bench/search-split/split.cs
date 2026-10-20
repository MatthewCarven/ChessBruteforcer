// Where a search node's time goes outside the network: bench's six positions at a fixed depth, one thread,
// below-normal priority, timed by the thread's own CPU time (QueryThreadCycleTime, TSC ticks at 2.496 GHz).
// Per position: a recording run (every evaluation, InCheck, PickNext choice, table probe, repetition and
// tablebase answer, and the move lists), a check that the network gives the recorded values, then rounds of
// arms on the identical tree (checked: the same nodes, every replayed stream read exactly to its end), the arms'
// order rotated round by round. "none" is the search with no network work at all (evaluations replayed, no
// accumulator pushes); a removal arm is "none" with one piece of work replayed instead of done; a doubling arm
// is "none" with one piece of work done twice. Round 0 warms up and is dropped. Before any of it, every arm runs
// twice at depth 6 on one position, so the JIT's profile (tiered PGO) sees all of them, not the recording alone.
//   split <depth> <rounds> <net .nnue> <empty tables folder> [--hash MB] [--arms a,b,...] [--tsv file]
using System.Diagnostics;
using System.Runtime.InteropServices;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Engine.Nnue;
using ChessBruteforcer.Core.Game;

Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
const double TscGhz = 2.496;
int depth = int.Parse(args[0]);
int rounds = int.Parse(args[1]);
var net = Network.Load(args[2]);
string tbDir = args[3];
int hashMb = 16;
string[]? only = null;
string? tsv = null;
for (int a = 4; a < args.Length; a++)
{
    if (args[a] == "--hash") hashMb = int.Parse(args[++a]);
    else if (args[a] == "--arms") only = args[++a].Split(',');
    else if (args[a] == "--tsv") tsv = args[++a];
    else throw new ArgumentException($"unknown argument {args[a]}");
}

string[] fens =
{
    "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
    "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
    "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
    "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
    "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
    "r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
};

// The bot's tables object (no solving, no loading mid-search), over an empty folder: past five pieces, or with
// castling rights, it answers as the bot's does; the recording counts the probes that would reach a table.
using var tablebase = new Tablebase(tbDir) { SolveMissing = false, LoadOnDemand = false };
var table = new TranspositionTable(hashMb);

static void None()
{
    Hooks.EvalMode = 2;
    Hooks.SkipUpdates = true;
}

var allArms = new (string Name, Action Set, string Streams)[]
{
    ("full", () => { }, ""),
    ("none", None, "e"),
    ("-chk", () => { None(); Hooks.NoChk = true; }, "e ce cl"),
    ("-chkL", () => { None(); Hooks.NoChkLegal = true; }, "e cl"),
    ("-illegal", () => { None(); Hooks.NoIllegal = true; }, "e cl"),
    ("-tt", () => { None(); Hooks.NoTt = true; }, "e tt"),
    ("-rep", () => { None(); Hooks.NoRep = true; }, "e rep"),
    ("-tb", () => { None(); Hooks.NoTb = true; }, "e tb"),
    ("-pick", () => { None(); Hooks.PickReplay = true; }, "e pick"),
    ("-order", () => { None(); Hooks.PickReplay = true; Hooks.NoScore = true; }, "e pick"),
    ("-gen", () => { None(); Hooks.NoGen = 7; }, "e gen0 gen1 gen2"),
    ("-genneg", () => { None(); Hooks.NoGen = 1; }, "e gen0"),      // Negamax's lists (every move)
    ("-gencap", () => { None(); Hooks.NoGen = 2; }, "e gen1"),      // quiescence's (captures, queen promotions)
    ("-genevade", () => { None(); Hooks.NoGen = 4; }, "e gen2"),    // quiescence's in check (every move)
    ("-castle", () => { None(); Hooks.NoCastle = true; }, "e ca"),  // castling's attack tests, inside generation
    ("+gen", () => { None(); Hooks.GenX2 = 1; }, "e"),
    ("+genqs", () => { None(); Hooks.GenX2 = 2; }, "e"),
    ("+genneg", () => { None(); Hooks.GenX2 = 3; }, "e"),
    ("+make", () => { None(); Hooks.MakeX2 = true; }, "e"),
    ("+chk", () => { None(); Hooks.ChkX2 = true; }, "e"),
    ("+score", () => { None(); Hooks.ScoreX2 = true; }, "e"),
    // The prototype of decision K's (b) and (c): today's answers from a set per colour and piece type (Hooks.cs).
    ("sets", () => { None(); Hooks.KeepSets = true; }, "e"),                                    // kept in make/unmake, not read
    ("tables", () => { None(); Hooks.KeepSets = Hooks.TableAttacks = true; }, "e"),             // InCheck, castling, the tablebase's count
    ("tables16", () => { None(); Hooks.KeepSets = Hooks.TableAttacks = Hooks.Compressed = true; }, "e"),
    ("vbb", () => { None(); Hooks.KeepSets = Hooks.VerifyBb = true; }, "e"),                    // a check, not a lever
    ("fulltables", () => { Hooks.KeepSets = Hooks.TableAttacks = true; }, ""),                  // the network on, the identical tree
    ("fulltables16", () => { Hooks.KeepSets = Hooks.TableAttacks = Hooks.Compressed = true; }, ""),
    // And generation from the sets in today's order: still the identical tree.
    ("bbo", () => { None(); Hooks.KeepSets = Hooks.TableAttacks = Hooks.BbOrdered = true; }, "e"),
    ("vbbo", () => { None(); Hooks.KeepSets = Hooks.VerifyBbo = true; }, "e"),                  // a check, not a lever
    ("fullbbo", () => { Hooks.KeepSets = Hooks.TableAttacks = Hooks.BbOrdered = true; }, ""),
    // And generation from the sets: another move order, so PickNext breaks ties another way: its own tree.
    ("fullbb", () => { Hooks.KeepSets = Hooks.TableAttacks = Hooks.BbGen = true; }, "free"),
    ("fullbb16", () => { Hooks.KeepSets = Hooks.TableAttacks = Hooks.BbGen = Hooks.Compressed = true; }, "free"),
};
var arms = only is null ? allArms : allArms.Where(a => only.Contains(a.Name)).ToArray();
if (only is not null && arms.Length != only.Length)
    throw new ArgumentException("an arm named in --arms doesn't exist");
bool wantGens = arms.Any(a => a.Name.StartsWith("-gen"));

(long Nodes, double ThreadNs, double WallNs, long Alloc, int Gcs) RunOne(int index, int searchDepth)
{
    table.Clear();
    var search = new Search(table, tablebase);
    var position = Position.FromFen(fens[index], net);
    nint thread = Native.GetCurrentThread();
    int gcs = GC.CollectionCount(0);
    long allocated = GC.GetAllocatedBytesForCurrentThread();
    Native.QueryThreadCycleTime(thread, out ulong before);
    var clock = Stopwatch.StartNew();
    var result = search.Run(position, new SearchLimits { Depth = searchDepth });
    double wallNs = clock.Elapsed.TotalNanoseconds;
    Native.QueryThreadCycleTime(thread, out ulong after);
    return (result.Nodes, (after - before) / TscGhz, wallNs, GC.GetAllocatedBytesForCurrentThread() - allocated,
            GC.CollectionCount(0) - gcs);
}

// The recording (the full engine, every answer noted), then the check that the network replays it.
(long Nodes, Dictionary<string, int> Length) Record(int index, int searchDepth)
{
    Hooks.ResetArms();
    Hooks.ResetStreams();
    Hooks.Rec = true;
    Hooks.EvalMode = 1;
    Hooks.RecordGens = wantGens;
    var rec = RunOne(index, searchDepth);
    Hooks.ResetArms();
    Hooks.RecordGens = false;
    var length = new Dictionary<string, int>
    {
        ["e"] = Hooks.EvalN, ["ce"] = Hooks.ChkEN, ["cl"] = Hooks.ChkLN, ["pick"] = Hooks.PickN, ["tt"] = Hooks.TtN,
        ["rep"] = Hooks.RepN, ["tb"] = Hooks.TbN, ["tbout"] = Hooks.TbOutN, ["ca"] = Hooks.CaN,
        ["gen0"] = Hooks.GenCN[0] + Hooks.GenMN[0], ["gen1"] = Hooks.GenCN[1] + Hooks.GenMN[1],
        ["gen2"] = Hooks.GenCN[2] + Hooks.GenMN[2], ["genm"] = Hooks.GenMN.Sum(),
    };
    Hooks.ResetStreams();
    Hooks.EvalMode = 3;
    Hooks.Mismatches = 0;
    var check = RunOne(index, searchDepth);
    Hooks.ResetArms();
    if (check.Nodes != rec.Nodes || Hooks.EvalN != length["e"] || Hooks.Mismatches != 0)
        throw new Exception($"position {index}: the network doesn't replay ({check.Nodes} nodes, {Hooks.Mismatches} mismatches)");
    return (rec.Nodes, length);
}

(long Nodes, double ThreadNs, double WallNs, long Alloc, int Gcs) RunArm((string Name, Action Set, string Streams) arm,
                                                                         int index, int searchDepth, long nodes, Dictionary<string, int> length)
{
    Hooks.ResetArms();
    Hooks.ResetStreams();
    arm.Set();
    var run = RunOne(index, searchDepth);
    Hooks.ResetArms();
    if (arm.Streams == "free")
        return run;
    if (run.Nodes != nodes)
        throw new Exception($"{arm.Name} position {index}: not the identical tree ({run.Nodes:N0} nodes against {nodes:N0})");
    foreach (string stream in arm.Streams.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        var (read, of) = stream switch
        {
            "e" => (Hooks.EvalN, length["e"]),
            "ce" => (Hooks.ChkEN, length["ce"]),
            "cl" => (Hooks.ChkLN, length["cl"]),
            "pick" => (Hooks.PickN, length["pick"]),
            "tt" => (Hooks.TtN, length["tt"]),
            "rep" => (Hooks.RepN, length["rep"]),
            "tb" => (Hooks.TbN + Hooks.TbOutN, length["tb"] + length["tbout"]),
            "ca" => (Hooks.CaN, length["ca"]),
            "gen0" => (Hooks.GenCN[0] + Hooks.GenMN[0], length["gen0"]),
            "gen1" => (Hooks.GenCN[1] + Hooks.GenMN[1], length["gen1"]),
            "gen2" => (Hooks.GenCN[2] + Hooks.GenMN[2], length["gen2"]),
            _ => throw new Exception(stream),
        };
        if (read != of)
            throw new Exception($"{arm.Name} position {index}: stream {stream} read {read:N0} of {of:N0}");
    }
    return run;
}

var started = DateTime.Now;
Console.WriteLine($"split: depth {depth}, {rounds} rounds (+1 warm-up), hash {hashMb} MB, net {net}, arms {string.Join(' ', arms.Select(a => a.Name))}");

// The JIT's warm-up: every arm twice at depth 6, so tier 1 is compiled from a profile of all of them.
{
    var warm = Record(3, 6);
    for (int round = 0; round < 2; round++)
        for (int k = 0; k < arms.Length; k++)
            RunArm(arms[(k + round * 7) % arms.Length], 3, 6, warm.Nodes, warm.Length);
    Console.Error.WriteLine($"warmed up {DateTime.Now:HH:mm:ss}");
}

var threadNs = arms.ToDictionary(a => a.Name, _ => new double[rounds + 1]);
var wallNs = arms.ToDictionary(a => a.Name, _ => new double[rounds + 1]);
var allocs = arms.ToDictionary(a => a.Name, _ => new long[rounds + 1]);
var gcCounts = arms.ToDictionary(a => a.Name, _ => new int[rounds + 1]);
var nodesRun = arms.ToDictionary(a => a.Name, _ => new long[rounds + 1]);   // (an arm on its own tree has its own)
var lines = new List<string> { "position\tround\tarm\tnodes\tthread_ns\twall_ns\talloc_bytes" };
long totalNodes = 0;
// The counts come from the six real recordings only.
Hooks.NegamaxNodes = Hooks.QsNodes = Hooks.ChkEntryNeg = Hooks.ChkEntryQs = Hooks.ChkLmr = Hooks.Makes = Hooks.MakesInCheck =
    Hooks.Illegal = Hooks.IllegalInCheck = Hooks.GensNeg = Hooks.GensQs = Hooks.GensQsChk = Hooks.MovesNeg = Hooks.MovesQs =
    Hooks.MovesQsChk = Hooks.PicksNeg = Hooks.PicksQs = Hooks.ScanNeg = Hooks.ScanQs = Hooks.TtProbes = Hooks.TtHits = Hooks.TtStores =
    Hooks.RepCalls = Hooks.RepYes = Hooks.RepWindow = Hooks.TbCalls = Hooks.TbCastling = Hooks.TbSmall = Hooks.TbHits =
    Hooks.NullMoves = Hooks.EvalCount = Hooks.CastleAttackChecks = Hooks.MakesKing = Hooks.IllegalKing =
    Hooks.IllegalOther = Hooks.ChkEYes = Hooks.Handoffs = Hooks.TtMoveCuts = 0;
for (int i = 0; i < fens.Length; i++)
{
    var (nodes, length) = Record(i, depth);
    totalNodes += nodes;
    Console.Error.WriteLine($"position {i}: {nodes:N0} nodes recorded and checked {DateTime.Now:HH:mm:ss}" +
                            (wantGens ? $" (move lists {length["genm"] * 4L / 1048576.0:0} MB)" : ""));
    for (int round = 0; round <= rounds; round++)
    {
        for (int k = 0; k < arms.Length; k++)
        {
            var arm = arms[(k + round * 5 + i) % arms.Length];
            var run = RunArm(arm, i, depth, nodes, length);
            threadNs[arm.Name][round] += run.ThreadNs;
            wallNs[arm.Name][round] += run.WallNs;
            allocs[arm.Name][round] += run.Alloc;
            gcCounts[arm.Name][round] += run.Gcs;
            nodesRun[arm.Name][round] += run.Nodes;
            lines.Add($"{i}\t{round}\t{arm.Name}\t{run.Nodes}\t{run.ThreadNs:0}\t{run.WallNs:0}\t{run.Alloc}");
        }
        Console.Error.WriteLine($"  position {i} round {round} done {DateTime.Now:HH:mm:ss}");
    }
}
if (tsv is not null)
    File.WriteAllLines(tsv, lines);

double n = totalNodes;
Console.WriteLine($"{totalNodes:N0} nodes ({DateTime.Now - started:mm\\:ss}); counts a node, from the six recordings:");
Console.WriteLine($"  Negamax {Hooks.NegamaxNodes / n:0.000}, quiescence {Hooks.QsNodes / n:0.000} (a Negamax node at depth 0 hands on to quiescence: both count)");
Console.WriteLine($"  InCheck: node entry {Hooks.ChkEntryNeg / n:0.000} Negamax + {Hooks.ChkEntryQs / n:0.000} quiescence, LMR's gives-check {Hooks.ChkLmr / n:0.000}, " +
                  $"after make {Hooks.Makes / n:0.000}; in generation, castling's attack tests {Hooks.CastleAttackChecks / n:0.000}; " +
                  $"{100.0 * Hooks.ChkEYes / Math.Max(1, Hooks.ChkEntryNeg + Hooks.ChkEntryQs + Hooks.ChkLmr):0.0}% of entry and LMR tests find check");
Console.WriteLine($"  makes {Hooks.Makes / n:0.000} ({Hooks.MakesInCheck / n:0.000} at nodes in check; elsewhere {Hooks.MakesKing / n:0.000} king moves); illegal {Hooks.Illegal / n:0.000} = {100.0 * Hooks.Illegal / Hooks.Makes:0.0}% of makes: " +
                  $"{Hooks.IllegalInCheck / n:0.000} at nodes in check ({100.0 * Hooks.IllegalInCheck / Math.Max(1, Hooks.MakesInCheck):0}% of the makes there), " +
                  $"elsewhere {Hooks.IllegalKing / n:0.000} king moves ({100.0 * Hooks.IllegalKing / Math.Max(1, Hooks.MakesKing):0}% of them) and {Hooks.IllegalOther / n:0.0000} others (pins, en passant)");
long gensCap = Hooks.GensQs - Hooks.GensQsChk, movesCap = Hooks.MovesQs - Hooks.MovesQsChk;
Console.WriteLine($"  generations: Negamax {Hooks.GensNeg / n:0.000} ({(double)Hooks.MovesNeg / Math.Max(1, Hooks.GensNeg):0.0} moves), " +
                  $"quiescence's captures {gensCap / n:0.000} ({(double)movesCap / Math.Max(1, gensCap):0.0} moves), " +
                  $"quiescence's evasions {Hooks.GensQsChk / n:0.000} ({(double)Hooks.MovesQsChk / Math.Max(1, Hooks.GensQsChk):0.0} moves)");
Console.WriteLine($"  picks: Negamax {Hooks.PicksNeg / n:0.000} (scan {(double)Hooks.ScanNeg / Math.Max(1, Hooks.PicksNeg):0.0} compares), quiescence {Hooks.PicksQs / n:0.000} (scan {(double)Hooks.ScanQs / Math.Max(1, Hooks.PicksQs):0.0})");
Console.WriteLine($"  table: probes {Hooks.TtProbes / n:0.000} (hits {100.0 * Hooks.TtHits / Math.Max(1, Hooks.TtProbes):0.0}%), stores {Hooks.TtStores / n:0.000}");
Console.WriteLine($"  repetition: {Hooks.RepCalls / n:0.000} checks ({Hooks.RepYes} found; window {(double)Hooks.RepWindow / Math.Max(1, Hooks.RepCalls):0.0} plies, half of them compared)");
Console.WriteLine($"  tablebase: {Hooks.TbCalls / n:0.000} probes ({100.0 * Hooks.TbCastling / Math.Max(1, Hooks.TbCalls):0.0}% stopped by castling rights; {Hooks.TbSmall} with 5 pieces or fewer; {Hooks.TbHits} hits)");
Console.WriteLine($"  null moves {Hooks.NullMoves / n:0.0000}, evaluations {Hooks.EvalCount / n:0.000}; Negamax hands on to quiescence {Hooks.Handoffs / n:0.000}; " +
                  $"nodes whose table move, searched first, cut off {Hooks.TtMoveCuts / n:0.000} ({100.0 * Hooks.TtMoveCuts / Math.Max(1, Hooks.GensNeg):0.0}% of Negamax's generations)");

double Median(IEnumerable<double> v)
{
    var s = v.OrderBy(x => x).ToList();
    return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
}
List<double> PerNode(string arm) => threadNs[arm].Zip(nodesRun[arm], (t, k) => t / k).Skip(1).ToList();
bool Has(string arm) => threadNs.ContainsKey(arm);

Console.WriteLine();
Console.WriteLine($"thread time, ns a node, medians of {rounds} rounds (and every round); wall clock; allocated bytes a node, gen-0 collections a round");
foreach (var arm in arms)
{
    var t = PerNode(arm.Name);
    var w = wallNs[arm.Name].Zip(nodesRun[arm.Name], (t, k) => t / k).Skip(1);   // (an arm on its own tree by its own nodes)
    Console.WriteLine($"  {arm.Name,-9} {Median(t),7:0.0}   {string.Join(' ', t.Select(x => x.ToString("0")))}   wall {Median(w),6:0.0}   " +
                      $"{allocs[arm.Name].Zip(nodesRun[arm.Name], (b, k) => (double)b / k).Skip(1).Average():0.00} B, {gcCounts[arm.Name].Skip(1).Average():0.0} GCs");
}

double fullNs = Has("full") ? Median(PerNode("full")) : double.NaN, noneNs = Has("none") ? Median(PerNode("none")) : double.NaN;
void Diff(string label, string plus, string minus, double callsPerNode, string per)
{
    if (!Has(plus) || !Has(minus))
        return;
    var d = PerNode(plus).Zip(PerNode(minus), (a, b) => a - b).ToList();
    double m = Median(d);
    string perCall = callsPerNode > 0 ? $"{m / callsPerNode,7:0.0} ns {per}" : "";
    Console.WriteLine($"  {label,-52} {m,7:0.0} {100 * m / noneNs,6:0.0}% {100 * m / fullNs,6:0.0}%   {perCall,-28} rounds {string.Join(' ', d.Select(x => x.ToString("0")))}");
}
double chkCalls = (Hooks.ChkEntryNeg + Hooks.ChkEntryQs + Hooks.ChkLmr + Hooks.Makes) / n;
Console.WriteLine();
Console.WriteLine($"the split, ns a node (medians of per-round differences), % of none ({noneNs:0.0}) and of full ({fullNs:0.0}):");
Diff("the network (full - none)", "full", "none", Hooks.EvalCount / n, "an evaluation");
Diff("generation, replayed (none - -gen)", "none", "-gen", (Hooks.GensNeg + Hooks.GensQs) / n, "a generation");
Diff("  Negamax's lists (none - -genneg)", "none", "-genneg", Hooks.GensNeg / n, "a generation");
Diff("  quiescence's captures (none - -gencap)", "none", "-gencap", gensCap / n, "a generation");
Diff("  quiescence's evasions (none - -genevade)", "none", "-genevade", Hooks.GensQsChk / n, "a generation");
Diff("  castling's attack tests in it (none - -castle)", "none", "-castle", Hooks.CastleAttackChecks / n, "a test");
Diff("generation, doubled (+gen - none)", "+gen", "none", (Hooks.GensNeg + Hooks.GensQs) / n, "a generation");
Diff("generation, Negamax's, doubled (+genneg - none)", "+genneg", "none", Hooks.GensNeg / n, "a generation");
Diff("generation, quiescence's, doubled (+genqs - none)", "+genqs", "none", Hooks.GensQs / n, "a generation");
Diff("ScoreMoves (-pick - -order)", "-pick", "-order", (Hooks.MovesNeg + Hooks.MovesQs) / n, "a move scored");
Diff("ScoreMoves, doubled (+score - none)", "+score", "none", (Hooks.MovesNeg + Hooks.MovesQs) / n, "a move scored");
Diff("PickNext's scan (none - -pick)", "none", "-pick", (Hooks.PicksNeg + Hooks.PicksQs) / n, "a pick");
Diff("ordering, all of it (none - -order)", "none", "-order", (Hooks.PicksNeg + Hooks.PicksQs) / n, "a pick");
Diff("InCheck, every call (none - -chk)", "none", "-chk", chkCalls, "a call");
Diff("InCheck, every call, doubled (+chk - none)", "+chk", "none", chkCalls, "a call");
Diff("InCheck after make (none - -chkL)", "none", "-chkL", Hooks.Makes / n, "a call");
Diff("InCheck at entry and LMR (-chkL - -chk)", "-chkL", "-chk", (Hooks.ChkEntryNeg + Hooks.ChkEntryQs + Hooks.ChkLmr) / n, "a call");
Diff("make + unmake, the board, doubled (+make - none)", "+make", "none", Hooks.Makes / n, "a make + unmake");
Diff("illegal moves: make, test, unmake (none - -illegal)", "none", "-illegal", Hooks.Illegal / n, "an illegal move");
Diff("table probe + store (none - -tt)", "none", "-tt", Hooks.TtProbes / n, "a probe (+ store)");
Diff("repetition check + hash list (none - -rep)", "none", "-rep", Hooks.RepCalls / n, "a check");
Diff("tablebase probe (none - -tb)", "none", "-tb", Hooks.TbCalls / n, "a probe");
double attackTests = chkCalls + Hooks.CastleAttackChecks / n;
Diff("prototype: the sets' upkeep in Put (sets - none)", "sets", "none", Hooks.Makes / n, "a make + unmake");
Diff("prototype: attack tables, upkeep in (none - tables)", "none", "tables", attackTests, "an attack test");
Diff("  in 16 bits (none - tables16)", "none", "tables16", attackTests, "an attack test");
Diff("prototype: attack tables, network on (full - fulltables)", "full", "fulltables", attackTests, "an attack test");
Diff("  in 16 bits (full - fulltables16)", "full", "fulltables16", attackTests, "an attack test");
Diff("prototype: generation from the sets, today's order (tables - bbo)", "tables", "bbo", (Hooks.GensNeg + Hooks.GensQs) / n, "a generation");
Diff("prototype: bitboards in today's order, network on (full - fullbbo)", "full", "fullbbo", 0, "");
Diff("  of which generation (fulltables - fullbbo)", "fulltables", "fullbbo", (Hooks.GensNeg + Hooks.GensQs) / n, "a generation");
if (Has("vbb") || Has("vbbo"))
    Console.WriteLine($"  generation from the sets checked against today's ({(Has("vbbo") ? "in order" : "as sets")}{(Has("vbb") && Has("vbbo") ? ", and as sets" : "")}): " +
                      $"{Hooks.BbLists:N0} lists, {Hooks.BbMismatches} differ");
// Arms on their own trees (generation from the sets, another move order): a node's cost, and the time to finish the depth.
foreach (var (own, tables) in new[] { ("fullbb", "fulltables"), ("fullbb16", "fulltables16") })
{
    if (!Has(own) || !Has("full"))
        continue;
    var perNode = PerNode("full").Zip(PerNode(own), (a, b) => a - b).ToList();
    var time = threadNs["full"].Zip(threadNs[own], (a, b) => 1 - b / a).Skip(1).ToList();
    Console.WriteLine($"  {own}: {nodesRun[own][1]:N0} nodes against {nodesRun["full"][1]:N0} ({100.0 * nodesRun[own][1] / nodesRun["full"][1] - 100:+0.00;-0.00}%); " +
                      $"a node {Median(perNode):0.0} ns cheaper than full ({100 * Median(perNode) / fullNs:0.0}%, +{100 * fullNs / (fullNs - Median(perNode)) - 100:0.0}% nodes a second; " +
                      $"rounds {string.Join(' ', perNode.Select(x => x.ToString("0")))}); the six searches {100 * Median(time):0.0}% sooner " +
                      $"(rounds {string.Join(' ', time.Select(x => (100 * x).ToString("0.0")))})");
    if (Has(tables))
    {
        var gen = PerNode(tables).Zip(PerNode(own), (a, b) => a - b).ToList();
        Console.WriteLine($"    of which generation from the sets ({tables} - {own}): {Median(gen):0.0} ns a node (rounds {string.Join(' ', gen.Select(x => x.ToString("0")))})");
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
