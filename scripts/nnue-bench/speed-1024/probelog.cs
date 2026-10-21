#:project probe-src/src/ChessBruteforcer.Core/ChessBruteforcer.Core.csproj
#:property TargetFramework=net8.0
#:property PublishAot=false
// The kernel lab's input: the first <records> accumulator pushes and evaluations of a real search of each
// of bench's six positions, as Probe.LogRecord writes them, each position's run starting with a record of
// kind 5 (a fresh root).
//   dotnet run -c Release probelog.cs <depth> <records a position> <net .nnue> <out .bin>
using System.Diagnostics;
using System.Runtime.InteropServices;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Engine.Nnue;
using ChessBruteforcer.Core.Game;

Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
int depth = int.Parse(args[0]);
int records = int.Parse(args[1]);
long skip = records;   // pass over as many first, so the log comes from the deeper iterations
var net = Network.Load(args[2]);
string[] fens =
{
    "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
    "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
    "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
    "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
    "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
    "r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
};
using var file = File.Create(args[3]);
long total = 0;
foreach (string fen in fens)
{
    Probe.Log = new int[(records + 1) * 10];
    Probe.LogCount = 0;
    Probe.LogRecord(0, 5, -1, -1, -1, -1, -1, -1, -1, -1);
    Probe.LogSkip = skip;
    Probe.Logging = true;
    var result = new Search(new TranspositionTable(16)).Run(Position.FromFen(fen, net), new SearchLimits { Depth = depth });
    Probe.Logging = false;
    file.Write(MemoryMarshal.AsBytes(Probe.Log.AsSpan(0, Probe.LogCount)));
    int kinds = Probe.LogCount / 10;
    var counts = new int[6];
    for (int i = 0; i < kinds; i++)
        counts[Probe.Log[i * 10 + 1]]++;
    Console.WriteLine($"{kinds - 1:N0} records ({counts[0]:N0} quiet, {counts[1]:N0} captures, {counts[2]:N0} castles, {counts[3]:N0} evaluations) of {result.Nodes:N0} nodes: {fen}");
    total += kinds;
}
Console.WriteLine($"{total:N0} records, {file.Length:N0} bytes, {args[3]}");
