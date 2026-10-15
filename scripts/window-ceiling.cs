#:project ../src/ChessBruteforcer.Core/ChessBruteforcer.Core.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false
#:property Nullable=enable

// The most the Ranges split (Baudet's parallel aspiration search) could ever gain, however many
// threads: at best, some thread has a window just wide enough to hold the score, searches it
// alone and undisturbed, and the depth costs what that search costs.  So the ceiling is the
// nodes of the full-window search of a depth over the nodes of the narrowest window around its
// score (+-1, 5, 10, 25, 50, 100 cp) whose search still returned a score inside it, from the
// same start (the table and the move ordering left by the depths before, deterministic on one
// thread).  Counted in nodes, so the machine's load doesn't matter.  Baudet (1978) put the
// ceiling at about 5-6 for the trees of his day.
//
//   dotnet run -c Release scripts/window-ceiling.cs -- positions.txt 11 [eval.txt]
using System.Reflection;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Game;

var fens = File.ReadLines(args[0]).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
int depth = int.Parse(args[1]);
if (args.Length > 2)
    Evaluation.Parameters = EvalParams.Load(args[2]);
const int Infinity = 32_000;
int[] halfWidths = { 1, 5, 10, 25, 50, 100 };

var negamax = typeof(Search).GetMethod("Negamax", BindingFlags.NonPublic | BindingFlags.Instance)!;
var nodesField = typeof(Search).GetField("_nodes", BindingFlags.NonPublic | BindingFlags.Instance)!;
var table = new TranspositionTable(64);

// One depth from the state the depths before it leave, in the window given: (score, nodes).
(int Score, long Nodes) LastDepth(Position root, int alpha, int beta)
{
    table.Clear();
    var search = new Search(table);
    search.Run(root, new SearchLimits { Depth = depth - 1 });
    long before = (long)nodesField.GetValue(search)!;
    int score = (int)negamax.Invoke(search, new object[] { root, depth, alpha, beta, 0, false })!;
    return (score, (long)nodesField.GetValue(search)! - before);
}

// A narrow window that doesn't hold the score fails, cheaply, and settles nothing: so the
// ceiling is set by the narrowest window that did hold it (the full window always does).
var full = new List<long>();
var best = new List<long>();
var narrow = halfWidths.ToDictionary(h => h, _ => new List<long>());
var exact = halfWidths.ToDictionary(h => h, _ => 0);
Console.WriteLine($"{DateTime.Now:HH:mm:ss}: {fens.Length} positions, depth {depth} (! = that window failed, held no score)");
for (int p = 0; p < fens.Length; p++)
{
    var root = Position.FromFen(fens[p]);
    var (score, nodes) = LastDepth(root, -Infinity, Infinity);
    full.Add(nodes);
    long fewest = nodes;
    var line = $"{p,3} score {score,6} full {nodes,12:N0}";
    foreach (int h in halfWidths)
    {
        var (s, n) = LastDepth(root, score - h, score + h);
        narrow[h].Add(n);
        bool held = s > score - h && s < score + h;
        if (held)
        {
            exact[h]++;
            fewest = Math.Min(fewest, n);
        }
        line += $"  +-{h}: {(double)nodes / n:0.00}{(held ? " " : "!")}";
    }
    best.Add(fewest);
    Console.WriteLine(line + $"  ceiling {(double)nodes / fewest:0.00}");
}

Console.WriteLine();
Console.WriteLine("| window around the score | searches it held the score | full-window nodes / window nodes (total) | (geometric mean) |");
Console.WriteLine("|---|---:|---:|---:|");
foreach (int h in halfWidths)
{
    double total = (double)full.Sum() / narrow[h].Sum();
    double geo = Math.Exp(full.Zip(narrow[h]).Average(pair => Math.Log((double)pair.First / pair.Second)));
    Console.WriteLine($"| +-{h} cp | {exact[h]} of {fens.Length} | {total:0.00} | {geo:0.00} |");
}
{
    double total = (double)full.Sum() / best.Sum();
    double geo = Math.Exp(full.Zip(best).Average(pair => Math.Log((double)pair.First / pair.Second)));
    double median = full.Zip(best).Select(pair => (double)pair.First / pair.Second).Order().ElementAt(fens.Length / 2);
    Console.WriteLine($"| the narrowest that held it (the ceiling) | {fens.Length} of {fens.Length} | {total:0.00} | {geo:0.00} (median {median:0.00}) |");
}
