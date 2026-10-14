#:project ../../Lookuptables/src/Lookuptables/Lookuptables.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false
#:property Nullable=enable

// "Every game that began like this": Matthew's CSharp-Lookuptables burst trie against a sorted array and a plain
// scan, over the first moves of a month's games (INDEX.md, session 1's trial; the moves come from
// `scripts/index-month.py rows ... --lines 16`).
//
//   dotnet run -c Release scripts/lines-trial.cs -- <index dir> [--games N] [--queries 1000] [--scan-queries 100]
//
// The lines: N two-byte codes a game (its first N moves in SAN, 0 once it has ended). Sorted, equal lines run
// together, so each distinct line is a run of game numbers in one flat array. Then three ways to answer a
// prefix (the first k moves) with the games that began that way:
//   sorted array: binary search of the distinct lines for the first and last line with that prefix; the games
//                 are one contiguous run of the flat array;
//   burst trie:   the distinct lines as keys (moves as big-endian bytes, then the line's number, as in
//                 Matthew's 64 GB disk test), CollectPrefix, and each line's run of games;
//   scan:         every game's line compared, no index (the control).
// Every structure's answer (games, and the sum of their numbers) is checked against the others. Memory is
// measured from the managed heap before and after each build.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lookuptables;

if (args.Length < 1)
{
    Console.WriteLine("usage: dotnet run -c Release scripts/lines-trial.cs -- <index dir> [--games N] [--queries 1000] [--scan-queries 100]");
    return 1;
}
string dir = args[0];
long Option(string name, long fallback)
{
    int i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? long.Parse(args[i + 1]) : fallback;
}

using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "lines.json")));
int plies = meta.RootElement.GetProperty("plies").GetInt32();
long monthGames = meta.RootElement.GetProperty("games").GetInt64();
string[] moveNames = meta.RootElement.GetProperty("moves").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
int games = (int)Math.Min(Option("games", monthGames), monthGames);
int queryCount = (int)Option("queries", 1000);
int scanQueries = (int)Option("scan-queries", 100);

var clock = Stopwatch.StartNew();
ushort[] lines = new ushort[(long)games * plies];
using (var file = File.OpenRead(Path.Combine(dir, "lines.u16")))
{
    var bytes = MemoryMarshal.AsBytes(lines.AsSpan());
    int got = 0;
    while (got < bytes.Length)
    {
        int n = file.Read(bytes[got..]);
        if (n == 0)
            throw new InvalidDataException($"lines.u16 ended after {got:N0} bytes; {bytes.Length:N0} wanted");
        got += n;
    }
}
if (!BitConverter.IsLittleEndian)
    throw new PlatformNotSupportedException("lines.u16 is little-endian");
Console.WriteLine($"{dir}: {games:N0} of {monthGames:N0} games, their first {plies} moves ({lines.Length * 2L / 1e6:N0} MB), read in {clock.Elapsed.TotalSeconds:N1}s");
Span<ushort> Line(int game) => lines.AsSpan(game * plies, plies);

// Sort the games by line (ties by game number), and find the runs of equal lines.
clock.Restart();
int[] order = new int[games];
for (int i = 0; i < games; i++)
    order[i] = i;
order.AsSpan().Sort((a, b) =>
{
    int c = lines.AsSpan(a * plies, plies).SequenceCompareTo(lines.AsSpan(b * plies, plies));
    return c != 0 ? c : a.CompareTo(b);
});
double sortSeconds = clock.Elapsed.TotalSeconds;

// How far each game agrees with its neighbours in sorted order: distinct lines at each length, and games
// alone at each length.
var agreeHistogram = new long[plies + 1];      // pairs of neighbours agreeing on exactly L moves
var aloneHistogram = new long[plies + 1];      // games whose longest agreement with a neighbour is L
int Agree(int a, int b)
{
    var x = Line(a);
    var y = Line(b);
    int k = 0;
    while (k < plies && x[k] == y[k])
        k++;
    return k;
}
int previous = -1;
for (int i = 0; i < games; i++)
{
    int next = i + 1 < games ? Agree(order[i], order[i + 1]) : -1;
    if (i + 1 < games)
        agreeHistogram[next]++;
    aloneHistogram[Math.Max(Math.Max(previous, next), 0)]++;
    previous = next;
}
var lineStarts = new List<int> { 0 };
for (int i = 1; i < games; i++)
    if (!Line(order[i]).SequenceEqual(Line(order[i - 1])))
        lineStarts.Add(i);
int distinct = lineStarts.Count;
lineStarts.Add(games);
int[] runStart = lineStarts.ToArray();
lineStarts = null!;
Console.WriteLine($"sorted in {sortSeconds:N1}s; {distinct:N0} distinct lines of {plies} moves ({100.0 * distinct / games:N1}% of games)");
Console.WriteLine();
Console.WriteLine("first k moves   distinct   games sharing them with no other");
long differBefore = 0;                         // neighbours differing within the first k moves
for (int k = 1; k <= plies; k++)
{
    differBefore += agreeHistogram[k - 1];
    long alone = 0;
    for (int l = 0; l < k; l++)
        alone += aloneHistogram[l];
    if (k <= 4 || k % 2 == 0)
        Console.WriteLine($"{k,13} {1 + differBefore,10:N0}   {alone,12:N0} ({100.0 * alone / games:N1}%)");
}

// The queries: prefixes of random games' lines, k from a spread of lengths.
int[] lengths = { 1, 2, 3, 4, 6, 8, 10, 12, 14, 16 };
var random = new Random(1);
var queries = new (ushort[] Prefix, int K)[queryCount];
for (int q = 0; q < queryCount; q++)
{
    int k = Math.Min(lengths[q % lengths.Length], plies);
    queries[q] = (Line(random.Next(games))[..k].ToArray(), k);
}

// Sorted array: the distinct lines in order.
long heapBefore = GC.GetTotalMemory(true);
ushort[] sortedLines = new ushort[(long)distinct * plies];
for (int d = 0; d < distinct; d++)
    Line(order[runStart[d]]).CopyTo(sortedLines.AsSpan(d * plies, plies));
long sortedBytes = GC.GetTotalMemory(true) - heapBefore + runStart.Length * 4L;
int ComparePrefix(int d, ushort[] prefix) => sortedLines.AsSpan(d * plies, prefix.Length).SequenceCompareTo(prefix);
(long Games, long Sum) SortedQuery(ushort[] prefix)
{
    int lo = 0, hi = distinct;                  // first line >= prefix
    while (lo < hi)
    {
        int mid = (lo + hi) >>> 1;
        if (ComparePrefix(mid, prefix) < 0) lo = mid + 1; else hi = mid;
    }
    int first = lo;
    hi = distinct;                              // first line > prefix
    while (lo < hi)
    {
        int mid = (lo + hi) >>> 1;
        if (ComparePrefix(mid, prefix) <= 0) lo = mid + 1; else hi = mid;
    }
    long sum = 0;
    foreach (int g in order.AsSpan(runStart[first], runStart[lo] - runStart[first]))
        sum += g;
    return (runStart[lo] - runStart[first], sum);
}

// Burst trie: one key per distinct line, its moves big-endian then its number.
heapBefore = GC.GetTotalMemory(true);
clock.Restart();
var keys = new byte[distinct][];
for (int d = 0; d < distinct; d++)
{
    var key = new byte[plies * 2 + 4];
    var line = sortedLines.AsSpan(d * plies, plies);
    for (int m = 0; m < plies; m++)
        BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(m * 2), line[m]);
    BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(plies * 2), d);
    keys[d] = key;
}
var trie = new BurstTrie<byte[], ByteKey>();
trie.LoadDistinct(keys);
double trieSeconds = clock.Elapsed.TotalSeconds;
keys = null!;                                   // the trie holds the keys themselves; only the outer array goes
long trieBytes = GC.GetTotalMemory(true) - heapBefore;
var found = new List<byte[]>();
(long Games, long Sum) TrieQuery(ushort[] prefix)
{
    var bytes = new byte[prefix.Length * 2];
    for (int m = 0; m < prefix.Length; m++)
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(m * 2), prefix[m]);
    found.Clear();
    trie.CollectPrefix(bytes, found);
    long count = 0, sum = 0;
    foreach (var key in found)
    {
        int d = BinaryPrimitives.ReadInt32BigEndian(key.AsSpan(plies * 2));
        count += runStart[d + 1] - runStart[d];
        foreach (int g in order.AsSpan(runStart[d], runStart[d + 1] - runStart[d]))
            sum += g;
    }
    return (count, sum);
}

// The same trie through TryGetPrefixRange (added for this trial on Matthew's branch prefix-range): only the
// first and last line with the prefix, and the games are the one run between them, as for the sorted array.
(long Games, long Sum) TrieRangeQuery(ushort[] prefix)
{
    var bytes = new byte[prefix.Length * 2];
    for (int m = 0; m < prefix.Length; m++)
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(m * 2), prefix[m]);
    if (!trie.TryGetPrefixRange(bytes, out var firstKey, out var lastKey))
        return (0, 0);
    int from = runStart[BinaryPrimitives.ReadInt32BigEndian(firstKey.AsSpan(plies * 2))];
    int to = runStart[BinaryPrimitives.ReadInt32BigEndian(lastKey.AsSpan(plies * 2)) + 1];
    long sum = 0;
    foreach (int g in order.AsSpan(from, to - from))
        sum += g;
    return (to - from, sum);
}

(long Games, long Sum) ScanQuery(ushort[] prefix)
{
    long count = 0, sum = 0;
    for (int g = 0; g < games; g++)
        if (lines.AsSpan(g * plies, prefix.Length).SequenceEqual(prefix))
        {
            count++;
            sum += g;
        }
    return (count, sum);
}

// Time each: warm-up passes (at least a second, so .NET's tiered JIT has finished optimising, as Matthew's
// harness does), then the timed pass, each query timed alone.
(double[] Micros, (long Games, long Sum)[] Answers) Run(Func<ushort[], (long, long)> query, int count)
{
    var warm = Stopwatch.StartNew();
    do
    {
        for (int q = 0; q < count; q++)
            query(queries[q].Prefix);
    } while (warm.Elapsed.TotalSeconds < 1);
    var micros = new double[count];
    var answers = new (long, long)[count];
    for (int q = 0; q < count; q++)
    {
        long t = Stopwatch.GetTimestamp();
        answers[q] = query(queries[q].Prefix);
        micros[q] = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
    }
    return (micros, answers);
}
var sorted = Run(SortedQuery, queryCount);
var trieRun = Run(TrieQuery, queryCount);
var trieRange = Run(TrieRangeQuery, queryCount);
var scan = Run(ScanQuery, Math.Min(scanQueries, queryCount));
int disagree = 0;
for (int q = 0; q < queryCount; q++)
    if (sorted.Answers[q] != trieRun.Answers[q] || sorted.Answers[q] != trieRange.Answers[q] ||
        (q < scan.Answers.Length && scan.Answers[q] != sorted.Answers[q]))
        disagree++;

static double Median(IEnumerable<double> values)
{
    var v = values.OrderBy(x => x).ToArray();
    return v.Length == 0 ? double.NaN : v[v.Length / 2];
}
Console.WriteLine();
Console.WriteLine($"{"",-14} {"memory",12} {"built in",10}");
Console.WriteLine($"{"sorted array",-14} {sortedBytes / 1e6,9:N1} MB {"(the sort)",10}   + the games in line order, {order.Length * 4L / 1e6:N1} MB, shared by both");
Console.WriteLine($"{"burst trie",-14} {trieBytes / 1e6,9:N1} MB {trieSeconds,9:N1}s   (its keys included; {trie.Shape})");
Console.WriteLine($"{"scan",-14} {"none",12}");
Console.WriteLine();
Console.WriteLine($"median microseconds a query ({queryCount:N0} queries; the scan on the first {scan.Answers.Length:N0}), and games found;");
Console.WriteLine("the trie two ways: every matching line (CollectPrefix), and the first and last only (TryGetPrefixRange):");
Console.WriteLine($"{"first k moves",13} {"games found",14} {"sorted array",13} {"trie, every",12} {"trie, ends",11} {"scan",12}");
foreach (int k in lengths.Where(k => k <= plies).Distinct())
{
    var which = Enumerable.Range(0, queryCount).Where(q => queries[q].K == k).ToArray();
    var scanWhich = which.Where(q => q < scan.Answers.Length).ToArray();
    Console.WriteLine($"{k,13} {Median(which.Select(q => (double)sorted.Answers[q].Games)),14:N0} {Median(which.Select(q => sorted.Micros[q])),13:N2} " +
                      $"{Median(which.Select(q => trieRun.Micros[q])),12:N2} {Median(which.Select(q => trieRange.Micros[q])),11:N2} " +
                      $"{Median(scanWhich.Select(q => scan.Micros[q])),12:N0}");
}
Console.WriteLine($"all {queryCount:N0}: sorted array {sorted.Micros.Sum() / 1e3:N1} ms, trie every {trieRun.Micros.Sum() / 1e3:N1} ms, " +
                  $"trie ends {trieRange.Micros.Sum() / 1e3:N1} ms; scan {scan.Micros.Sum() / 1e3:N0} ms for {scan.Answers.Length:N0}");
Console.WriteLine(disagree == 0 ? "answers agree: yes" : $"answers DISAGREE on {disagree} queries");
var example = queries.First(q => q.K == Math.Min(8, plies));
Console.WriteLine($"e.g. the first {example.K} moves {string.Join(" ", example.Prefix.Where(c => c != 0).Select(c => moveNames[c]))}: {SortedQuery(example.Prefix).Games:N0} games");
return disagree == 0 ? 0 : 1;
