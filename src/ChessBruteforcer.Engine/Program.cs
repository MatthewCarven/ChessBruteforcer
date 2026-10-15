using System.Diagnostics;
using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Engine.Nnue;
using ChessBruteforcer.Core.Game;

// ChessBruteforcer as a UCI engine: point a GUI or match runner at this
// executable.  "bench [depth]" searches a fixed set of positions and prints
// the node count and speed, which is how changes are compared for speed.
// An optional second argument (or --tables <dir>) enables endgame tables.

string? tables = null;
int tablesAt = Array.IndexOf(args, "--tables");
if (tablesAt >= 0 && tablesAt + 1 < args.Length)
    tables = args[tablesAt + 1];

if (args.Length > 0 && args[0] == "bench")
{
    // bench [depth] [--eval <numbers .txt or network .nnue>]
    int depth = args.Length > 1 && int.TryParse(args[1], out int d) ? d : 8;
    int evalAt = Array.IndexOf(args, "--eval");
    Network? network = null;
    if (evalAt >= 0 && evalAt + 1 < args.Length)
    {
        if (Network.IsNetworkFile(args[evalAt + 1]))
            network = Network.Load(args[evalAt + 1]);
        else
            Evaluation.Parameters = EvalParams.Load(args[evalAt + 1]);
        Console.WriteLine($"evaluation: {(network is null ? "numbers" : "network")} from {args[evalAt + 1]}" +
                          (network is null ? "" : $" ({network})"));
    }
    return Bench(depth, network);
}

if (args.Length > 0 && args[0] == "bench-threads")
    return BenchThreads.Run(args[1..]);

if (args.Length > 0 && args[0] == "selfplay")
{
    int games = args.Length > 1 && int.TryParse(args[1], out int g) ? g : 4;
    int moveTime = args.Length > 2 && int.TryParse(args[2], out int t) ? t : 100;
    return SelfPlay.Run(games, moveTime, tables);
}

new UciEngine(Console.Out, tables).Run(Console.In);
return 0;

static int Bench(int depth, Network? network)
{
    string[] positions =
    {
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
        "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
        "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
        "r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
    };
    long total = 0;
    var clock = Stopwatch.StartNew();
    foreach (string fen in positions)
    {
        var search = new Search(new TranspositionTable(16));
        var result = search.Run(Position.FromFen(fen, network), new SearchLimits { Depth = depth });
        Console.WriteLine($"{result.BestMove?.ToUci(),-6} score {result.Score,6}  nodes {result.Nodes,12:N0}  {fen}");
        total += result.Nodes;
    }
    double seconds = clock.Elapsed.TotalSeconds;
    Console.WriteLine($"bench depth {depth}: {total:N0} nodes in {seconds:0.00}s = {total / seconds / 1000:0} knps");
    return 0;
}

/// <summary>
/// The engine against itself from a handful of openings, applying every way
/// a game can end.  A robustness check (no crashes, no illegal moves, games
/// finish) and the seed of the local match runner.
/// </summary>
static class SelfPlay
{
    private static readonly string[] Openings =
    {
        "e2e4 e7e5", "d2d4 d7d5", "e2e4 c7c5", "c2c4 e7e5", "d2d4 g8f6 c2c4 e7e6", "e2e4 e7e6",
        "g1f3 d7d5", "e2e4 c7c6",
    };

    public static int Run(int games, int moveTimeMs, string? tables)
    {
        var tally = new Dictionary<string, int>();
        for (int game = 0; game < games; game++)
        {
            string opening = Openings[game % Openings.Length];
            var (result, reason, moves) = Play(opening, moveTimeMs, tables);
            tally[result] = tally.GetValueOrDefault(result) + 1;
            Console.WriteLine($"game {game + 1}: {result} ({reason}), {moves.Count} plies");
            Console.WriteLine($"  {string.Join(' ', moves)}");
        }
        Console.WriteLine(string.Join(", ", tally.Select(kv => $"{kv.Key}: {kv.Value}")));
        return 0;
    }

    private static (string Result, string Reason, List<string> Moves) Play(string opening, int moveTimeMs, string? tables)
    {
        var position = Position.Start();
        var hashes = new List<ulong>();
        var moves = new List<string>();
        var tablebase = tables is null ? null : new Tablebase(tables) { SolveMissing = false, LoadOnDemand = false };
        tablebase?.Preload();
        var search = new Search(new TranspositionTable(32), tablebase);

        void Play(Move move)
        {
            hashes.Add(position.Hash);
            moves.Add(move.ToUci());
            position.MakeMove(move);
        }

        foreach (string uci in opening.Split(' '))
            Play(position.ParseUciMove(uci)!.Value);

        while (true)
        {
            switch (position.Status())
            {
                case GameStatus.Checkmate:
                    return (position.SideToMove == Colour.White ? "0-1" : "1-0", "checkmate", moves);
                case GameStatus.Stalemate:
                    return ("1/2-1/2", "stalemate", moves);
            }
            if (position.HalfmoveClock >= 100)
                return ("1/2-1/2", "50-move rule", moves);
            if (hashes.Count(h => h == position.Hash) >= 2)
                return ("1/2-1/2", "threefold repetition", moves);
            if (InsufficientMaterial(position))
                return ("1/2-1/2", "insufficient material", moves);
            if (moves.Count >= 400)
                return ("1/2-1/2", "move limit", moves);

            var result = search.Run(position, new SearchLimits { MoveTimeMs = moveTimeMs }, hashes);
            var move = result.BestMove ?? throw new InvalidOperationException("No move in a live position.");
            if (position.ParseUciMove(move.ToUci()) is null)
                throw new InvalidOperationException($"Illegal move {move} in {position.ToFen()}");
            Play(move);
        }
    }

    /// <summary>Only kings, or king and one minor piece against a bare king.</summary>
    private static bool InsufficientMaterial(Position position)
    {
        int minors = 0;
        for (int square = 0; square < 64; square++)
        {
            switch (position[square].Type)
            {
                case PieceType.None or PieceType.King: break;
                case PieceType.Knight or PieceType.Bishop: minors++; break;
                default: return false;
            }
        }
        return minors <= 1;
    }
}

/// <summary>
/// The search on more than one thread, measured.  Every position is searched
/// to a fixed depth by every arm ("1": one thread, "L4": Lazy SMP on 4, "R4":
/// Ranges on 4), all the arms one after another within a position, so that a
/// change in the machine's speed falls on them alike, and in reverse order
/// on alternate rounds.  The table is cleared before each search.  One line
/// a search, then a table of each arm against "1".  Runs at below-normal
/// priority, so that a game the bot is playing comes first.
///
/// With --tables, the endgame tables in that folder are loaded as the UCI
/// engine loads them (plain ones into memory up to 1 GB, compressed ones
/// read a block at a time as probed), every position is searched once by
/// the last arm before the timed rounds (so the table files are in the
/// disk cache for all the arms alike), and the compressed tables' block
/// cache (--table-cache MB, the engine's default if not given) is emptied
/// before each search, as the hash table is, and its memory let go.
/// With --trace, every read of a compressed table in the timed searches is
/// written to that file (see TableReadTrace; scripts/block-cache-trace.py
/// reads it), with the tables' numbering beside it in &lt;file&gt;.tables.tsv.
///
///   bench-threads positions.txt 12 [--arms 1,L2,L4,L8,R2,R4,R8] [--rounds 2]
///                 [--hash 64] [--eval eval.txt] [--step 15] [--tsv out.tsv]
///                 [--tables dir] [--table-cache mb] [--trace reads.bin]
/// </summary>
static class BenchThreads
{
    private sealed record Arm(string Name, int Threads, ParallelMode Split);

    private sealed record Row(int Round, int Position, string Arm, double Ms, long Nodes, string Move, int Score, int Fallbacks,
                              long TableHits, BlockCacheCounts Blocks);

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: bench-threads <fen file> <depth> [--arms 1,L2,R2,...] [--rounds n] [--hash mb] " +
                                    "[--eval file] [--step cp] [--tsv file] [--tables dir] [--table-cache mb] [--trace file]");
            return 1;
        }
        var fens = File.ReadLines(args[0]).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
        int depth = int.Parse(args[1]);
        string armText = "1,L2,L4,L8,R2,R4,R8";
        int rounds = 2, hash = 64, tableCache = BlockCache.DefaultMegabytes;
        int? step = null, overlap = null;   // (unset: the search's own defaults)
        string? tsv = null, tables = null, trace = null;
        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--tables": tables = args[++i]; break;
                case "--trace": trace = args[++i]; break;
                case "--table-cache": tableCache = int.Parse(args[++i]); break;
                case "--arms": armText = args[++i]; break;
                case "--rounds": rounds = int.Parse(args[++i]); break;
                case "--hash": hash = int.Parse(args[++i]); break;
                case "--eval": Evaluation.Parameters = EvalParams.Load(args[++i]); break;
                case "--step": step = int.Parse(args[++i]); break;
                case "--overlap": overlap = int.Parse(args[++i]); break;
                case "--tsv": tsv = args[++i]; break;
                default:
                    Console.Error.WriteLine($"unknown argument {args[i]}");
                    return 1;
            }
        }
        var arms = armText.Split(',').Select(ParseArm).ToArray();
        using (var self = Process.GetCurrentProcess())
            self.PriorityClass = ProcessPriorityClass.BelowNormal;   // (threads made later inherit it)

        // The tables as the UCI engine has them: never solved or loaded mid-search, read in before the first.
        using var tablebase = tables is null ? null
            : new Tablebase(tables, blockCache: new BlockCache(tableCache)) { SolveMissing = false, LoadOnDemand = false };
        if (tablebase is not null)
        {
            var loading = Stopwatch.StartNew();
            var (count, bytes, onDisk) = tablebase.Preload();
            Console.WriteLine($"loaded {count} endgame tables from {tables} in {loading.Elapsed.TotalSeconds:0.0} s " +
                              $"({bytes / (1024 * 1024)} MB in memory, {onDisk} read from disk as probed, " +
                              $"their blocks cached in {tablebase.BlockCache.Megabytes} MB at most)");
        }
        var table = new TranspositionTable(hash);
        var search = new Search(table, tablebase);
        search.RangeStep = step ?? search.RangeStep;
        search.RangeOverlap = overlap ?? search.RangeOverlap;
        var rows = new List<Row>();
        using var output = tsv is null ? null : new StreamWriter(tsv);
        output?.WriteLine("round\tposition\tarm\tms\tnodes\tmove\tscore\tfallbacks\ttable_hits\tblocks\tblock_ms\twaits\twait_ms" +
                          "\tsame_block_waits\tsame_block_wait_ms");
        // A search's reads: a marker (the sign bit, then round, position and threads), then the reads.
        using var traceOut = trace is null ? null : new BinaryWriter(File.Create(trace));
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}: {fens.Length} positions to depth {depth}, {rounds} rounds, " +
                          $"arms {armText}, hash {hash} MB, range step {search.RangeStep} cp, overlap {search.RangeOverlap} cp" +
                          (tablebase is null ? "" : ", endgame tables on"));
        // Warm up first (the JIT, the caches), off the record: the first search ran a third slower.
        foreach (var arm in arms)
        {
            table.Clear();
            search.Threads = arm.Threads;
            search.Split = arm.Split;
            search.Run(Position.FromFen(fens[0]), new SearchLimits { Depth = Math.Max(1, depth - 2) });
        }
        if (tablebase is not null)
        {
            // Every position once, so that the first arm timed doesn't pay alone for reading the table files from disk.
            var warming = Stopwatch.StartNew();
            foreach (string fen in fens)
            {
                table.Clear();
                tablebase.ClearBlockCaches();
                search.Run(Position.FromFen(fen), new SearchLimits { Depth = depth });
            }
            Console.WriteLine($"every position searched once on {arms[^1].Name} in {warming.Elapsed.TotalSeconds:0.0} s, off the record " +
                              $"({Memory()})");
        }
        if (traceOut is not null)
        {
            TableReadTrace.Take();
            TableReadTrace.Start();
        }
        for (int round = 0; round < rounds; round++)
        {
            var order = round % 2 == 0 ? arms : arms.Reverse().ToArray();
            for (int p = 0; p < fens.Length; p++)
            {
                foreach (var arm in order)
                {
                    table.Clear();
                    tablebase?.ClearBlockCaches();
                    var before = tablebase?.BlockCacheCounts() ?? default;
                    search.Threads = arm.Threads;
                    search.Split = arm.Split;
                    var clock = Stopwatch.StartNew();
                    var result = search.Run(Position.FromFen(fens[p]), new SearchLimits { Depth = depth });
                    double ms = clock.Elapsed.TotalMilliseconds;
                    var blocks = (tablebase?.BlockCacheCounts() ?? default) - before;
                    var row = new Row(round, p, arm.Name, ms, result.Nodes, result.BestMove?.ToUci() ?? "0000", result.Score,
                                      search.RangeFallbacks, search.TableHits, blocks);
                    rows.Add(row);
                    Console.WriteLine($"{round} {p,3} {arm.Name,-4} {ms,9:0.0} ms {row.Nodes,13:N0} nodes " +
                                      $"{row.Nodes / Math.Max(ms, 0.001):0} knps  {row.Move,-6} {row.Score,6}" +
                                      (row.Fallbacks > 0 ? $"  ({row.Fallbacks} fallbacks)" : "") +
                                      (tablebase is null ? "" : $"  tb {row.TableHits:N0}, {blocks.Decompressed:N0} blocks in " +
                                                                $"{blocks.DecompressMs:0} ms, {blocks.Waits:N0} waits {blocks.WaitMs:0.0} ms " +
                                                                $"({blocks.SameBlockWaits:N0} for the same block)"));
                    output?.WriteLine($"{round}\t{p}\t{arm.Name}\t{ms:0.000}\t{row.Nodes}\t{row.Move}\t{row.Score}\t{row.Fallbacks}\t" +
                                      $"{row.TableHits}\t{blocks.Decompressed}\t{blocks.DecompressMs:0.000}\t{blocks.Waits}\t{blocks.WaitMs:0.000}\t" +
                                      $"{blocks.SameBlockWaits}\t{blocks.SameBlockWaitMs:0.000}");
                    if (traceOut is not null)
                    {
                        traceOut.Write(long.MinValue | (long)round << 32 | (long)p << 16 | (long)arm.Threads);
                        foreach (long read in TableReadTrace.Take())
                            traceOut.Write(read);
                    }
                }
            }
            Console.WriteLine($"round {round} done, {Memory()}");
        }
        if (traceOut is not null)
        {
            TableReadTrace.Stop();
            using var names = new StreamWriter(trace + ".tables.tsv");
            names.WriteLine("id\tmaterial\tdtz\tsize\tvalues_per_block");
            foreach (var info in TableReadTrace.Tables)
                names.WriteLine($"{info.Id}\t{info.Material}\t{(info.Dtz ? 1 : 0)}\t{info.Size}\t{info.ValuesPerBlock}");
            Console.WriteLine($"traced to {trace}");
        }
        Summarise(rows, arms);
        if (tablebase is not null)
            SummariseTables(rows, arms);
        return 0;
    }

    private static string Memory()
    {
        using var self = Process.GetCurrentProcess();
        return $"private {self.PrivateMemorySize64 / (1024 * 1024):N0} MB, peak {self.PeakPagedMemorySize64 / (1024 * 1024):N0} MB";
    }

    /// <summary>The endgame tables' share of each arm's searches: table hits, blocks decompressed, and waits, as shares of the arm's thread time.</summary>
    private static void SummariseTables(List<Row> rows, Arm[] arms)
    {
        var baseline = rows.Where(r => r.Arm == "1").ToDictionary(r => (r.Round, r.Position));
        Console.WriteLine();
        Console.WriteLine("| arm | threads | table hits a search | table hits v 1 | table hits a second | blocks a search | " +
                          "decompressing, of thread time | waits a search | waiting, of thread time | waits for the same block |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var arm in arms)
        {
            var mine = rows.Where(r => r.Arm == arm.Name).ToList();
            if (mine.Count == 0)
                continue;
            double threadMs = mine.Sum(r => r.Ms) * arm.Threads;
            long hits = mine.Sum(r => r.TableHits);
            long baseHits = mine.Where(r => baseline.ContainsKey((r.Round, r.Position))).Sum(r => baseline[(r.Round, r.Position)].TableHits);
            Console.WriteLine($"| {arm.Name} | {arm.Threads} | {hits / mine.Count:N0} | {(baseHits > 0 ? $"{(double)hits / baseHits:0.00}" : "")} | " +
                              $"{hits / (mine.Sum(r => r.Ms) / 1000):N0} | {mine.Average(r => r.Blocks.Decompressed):N0} | " +
                              $"{mine.Sum(r => r.Blocks.DecompressMs) / threadMs:P1} | {mine.Average(r => r.Blocks.Waits):N0} | " +
                              $"{mine.Sum(r => r.Blocks.WaitMs) / threadMs:P1} | " +
                              $"{(double)mine.Sum(r => r.Blocks.SameBlockWaits) / Math.Max(1, mine.Sum(r => r.Blocks.Waits)):P0} |");
        }
    }

    private static Arm ParseArm(string text) => text switch
    {
        "1" => new Arm("1", 1, ParallelMode.LazySmp),
        ['L', .. var n] => new Arm(text, int.Parse(n), ParallelMode.LazySmp),
        ['R', .. var n] => new Arm(text, int.Parse(n), ParallelMode.Ranges),
        _ => throw new FormatException($"An arm is 1, L<threads> or R<threads>, not {text}."),
    };

    /// <summary>Each arm against the one-thread arm on the same searches (same round and position).</summary>
    private static void Summarise(List<Row> rows, Arm[] arms)
    {
        var baseline = rows.Where(r => r.Arm == "1").ToDictionary(r => (r.Round, r.Position));
        if (baseline.Count == 0)
        {
            Console.WriteLine("(no \"1\" arm to compare with)");
            return;
        }
        var repeats = baseline.Values.Where(r => r.Round > 0)
            .Count(r => r.Nodes != baseline[(0, r.Position)].Nodes || r.Move != baseline[(0, r.Position)].Move);
        Console.WriteLine();
        Console.WriteLine($"One thread, repeated: {repeats} of {baseline.Values.Count(r => r.Round > 0)} later searches differ from round 0 in nodes or move.");
        Console.WriteLine();
        Console.WriteLine("| arm | threads | total time s | speedup (total) | speedup (geo. mean) | nodes v 1 | knps | knps v 1 | same move | same score | mean abs score diff | fallbacks |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        double baseSeconds = baseline.Values.Sum(r => r.Ms) / 1000;
        long baseNodes = baseline.Values.Sum(r => r.Nodes);
        foreach (var arm in arms)
        {
            var mine = rows.Where(r => r.Arm == arm.Name && baseline.ContainsKey((r.Round, r.Position))).ToList();
            if (mine.Count == 0)
                continue;
            double seconds = mine.Sum(r => r.Ms) / 1000;
            long nodes = mine.Sum(r => r.Nodes);
            double geo = Math.Exp(mine.Average(r => Math.Log(baseline[(r.Round, r.Position)].Ms / Math.Max(r.Ms, 0.001))));
            double sameMove = mine.Count(r => r.Move == baseline[(r.Round, r.Position)].Move) * 100.0 / mine.Count;
            double sameScore = mine.Count(r => r.Score == baseline[(r.Round, r.Position)].Score) * 100.0 / mine.Count;
            double scoreDiff = mine.Average(r => Math.Abs(r.Score - baseline[(r.Round, r.Position)].Score));
            double knps = nodes / seconds / 1000, baseKnps = baseNodes / baseSeconds / 1000;
            Console.WriteLine($"| {arm.Name} | {arm.Threads} | {seconds:0.0} | {baseSeconds / seconds:0.00} | {geo:0.00} | " +
                              $"{(double)nodes / baseNodes:0.00} | {knps:N0} | {knps / baseKnps:0.00} | {sameMove:0}% | {sameScore:0}% | " +
                              $"{scoreDiff:0.0} | {mine.Sum(r => r.Fallbacks)} |");
        }
    }
}
