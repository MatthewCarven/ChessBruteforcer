using System.Diagnostics;
using System.Numerics;
using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Game;
using ChessBruteforcer.Core.Match;
using ChessBruteforcer.Core.Possibility;
using ChessBruteforcer.Core.Records;

return Cli.Run(args);

static class Cli
{
    private const string Usage = """
        ChessBruteforcer: 48-byte chess boards, checks, and the size of the space.

        Boards are given as FEN (quote it) or as 96 hex digits.

          ranges                         how many boards survive each tier of checks
          check <board>...               check boards and say why any are impossible
          show <board>                   print a board as a diagram, FEN and hex
          sample [count] [seed]          collapse random boards from superposition and check them

        Positions are full FEN (side to move, castling, en passant); the start position if omitted.

          moves [fen]                    legal moves, and whether it is check, mate or stalemate
          perft <depth> [fen]            count every move sequence to <depth>
          divide <depth> [fen]           perft split by first move, for tracking down bugs

        Endgame tables (up to 5 pieces without pawns, 4 with; saved in ./tables, reused next time):

          solve <material> [--cap N]     solve e.g. KQvK or KRvK and print what it found; with a cap, only
                                         to N plies (a table solved to less is carried on, not redone)
          probe <fen>                    the outcome, and every move ranked best first
          probe <fen> --rule             the same under the 50-move rule only (DTZ tables; no mate distances needed)
          line <fen>                     best play from here to mate
          verify <material> [stride] [--cap N]  check every (or every n-th) position against its moves
          dtz <material>                 solve under the 50-move rule (.cbz beside the table): wins, draws,
                                         losses, and the wins and losses the rule turns into draws
          dtz <material> verify [stride] check the DTZ table against its moves
          upgrade [dir]                  rewrite older table files in the current format (they load either way)
          compare <file> <file>          two table files of one material, value by value
          compress [dir|file] [--quality N]  compress complete tables in place (13-18x smaller; they load and probe
                                         as before, a block at a time): each is checked value by value against
                                         the plain file before replacing it.  Brotli quality 0-11, default 10

        Tables live in ./tables, or wherever CHESS_TABLES points.

          file add <file> <board>...     append boards to a board file (.cbb)
          file import <file> <text>      append every FEN / hex line of a text file
          file list <file>               print each board as FEN (or hex if meaningless)
          file export <file> <text>      write each board as a FEN line to a text file
          file check <file>              check every board, tallying the tier each reached
          file dedupe <file> [out]       sort and drop duplicate boards (in place by default)
          file count <file>              number of boards in the file

        Game files (.cbg): tags, result, and one byte per move.

          game import <file> <pgn>...    append every game in the PGN files (comments and variations dropped)
          game export <file> <pgn>       write every game out as PGN
          game list <file>               one line per game: number, result, length, players
          game count <file>              number of games in the file
          game show <file> <n> [ply]     game n replayed to a ply: 0 = start, -1 = one before the end, default the end
          game grade <file> [examples]   early kill / efficient / time waster, with the sharpest examples of each
          game idle <file>...            idle moves (nothing happening): sent straight back, cycled, or new?
          game tree <file>...            how much the games share (tree of moves, set of positions); what each file adds
          game endings <file>...         which endgame tables (5 pieces or fewer) the games reach; the 5-piece tables
                                         with pawns not yet in ./tables ranked, in the order they can be built
          game takeover <file>... [--play N]  where each game enters a table we have (both kinds): what the table
                                         says at the game's clock against what happened; --play N plays N of them out
                                         with the engine on both sides (0: all)
          game takeover <file>... --versus N "<engine command>" [ms]  N of those positions, each played twice
                                         against a UCI engine (our engine on each side in turn), ms a move (100)
        """;

    public static int Run(string[] args)
    {
        try
        {
            return args switch
            {
                ["ranges"] => Ranges(),
                ["check", .. var boards] when boards.Length > 0 => Check(boards),
                ["show", var board] => Show(board),
                ["sample"] => Sample(1000, null),
                ["sample", var n] => Sample(int.Parse(n), null),
                ["sample", var n, var seed] => Sample(int.Parse(n), int.Parse(seed)),
                ["moves"] => Moves(Fen.StartPosition),
                ["moves", var fen] => Moves(fen),
                ["perft", var depth] => RunPerft(int.Parse(depth), Fen.StartPosition, divide: false),
                ["perft", var depth, var fen] => RunPerft(int.Parse(depth), fen, divide: false),
                ["divide", var depth] => RunPerft(int.Parse(depth), Fen.StartPosition, divide: true),
                ["divide", var depth, var fen] => RunPerft(int.Parse(depth), fen, divide: true),
                ["solve", var material] => Solve(material, null),
                ["solve", var material, "--cap", var cap] => Solve(material, int.Parse(cap)),
                ["probe", var fen] => Probe(fen),
                ["probe", var fen, "--rule"] => ProbeUnderRule(fen),
                ["dtz", var material] => Dtz(material),
                ["dtz", var material, "verify"] => DtzVerify(material, 1),
                ["dtz", var material, "verify", var stride] => DtzVerify(material, int.Parse(stride)),
                ["line", var fen] => Line(fen),
                ["verify", var material] => Verify(material, 1, null),
                ["verify", var material, "--cap", var cap] => Verify(material, 1, int.Parse(cap)),
                ["verify", var material, var stride] => Verify(material, int.Parse(stride), null),
                ["verify", var material, var stride, "--cap", var cap] => Verify(material, int.Parse(stride), int.Parse(cap)),
                ["upgrade"] => Upgrade(TableDirectory),
                ["compare", var first, var second] => Compare(first, second),
                ["upgrade", var directory] => Upgrade(directory),
                ["compress"] => Compress(TableDirectory, EndgameTable.CompressionQuality),
                ["compress", "--quality", var quality] => Compress(TableDirectory, int.Parse(quality)),
                ["compress", var target] => Compress(target, EndgameTable.CompressionQuality),
                ["compress", var target, "--quality", var quality] => Compress(target, int.Parse(quality)),
                ["file", "add", var file, .. var boards] when boards.Length > 0 => FileAdd(file, boards),
                ["file", "import", var file, var text] => FileAdd(file, ReadLines(text)),
                ["file", "list", var file] => FileList(file),
                ["file", "export", var file, var text] => FileExport(file, text),
                ["file", "check", var file] => FileCheck(file),
                ["file", "dedupe", var file] => FileDedupe(file, null),
                ["file", "dedupe", var file, var output] => FileDedupe(file, output),
                ["file", "count", var file] => Print($"{BoardFile.Count(file):N0} boards"),
                ["game", "import", var file, .. var pgns] when pgns.Length > 0 => GameImport(file, pgns),
                ["game", "export", var file, var pgn] => GameExport(file, pgn),
                ["game", "list", var file] => GameList(file),
                ["game", "count", var file] => Print($"{GameFile.Count(file):N0} games"),
                ["game", "show", var file, var n] => GameShow(file, long.Parse(n), "end"),
                ["game", "show", var file, var n, var ply] => GameShow(file, long.Parse(n), ply),
                ["game", "grade", var file] => GameGrade(file, 3),
                ["game", "grade", var file, var n] => GameGrade(file, int.Parse(n)),
                ["game", "tree", .. var files] when files.Length > 0 => GameTree(files),
                ["game", "idle", .. var files] when files.Length > 0 => GameIdle(files),
                ["game", "endings", .. var files] when files.Length > 0 => GameEndings(files),
                ["game", "takeover", .. var files, "--play", var n] when files.Length > 0 => GameTakeover(files, int.Parse(n)),
                ["game", "takeover", .. var files, "--versus", var n, var engine] when files.Length > 0 =>
                    GameTakeover(files, null, (int.Parse(n), engine, 100)),
                ["game", "takeover", .. var files, "--versus", var n, var engine, var ms] when files.Length > 0 =>
                    GameTakeover(files, null, (int.Parse(n), engine, int.Parse(ms))),
                ["game", "takeover", .. var files] when files.Length > 0 => GameTakeover(files, null),
                _ => Print(Usage, 1),
            };
        }
        catch (Exception e) when (e is FormatException or IOException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    private static int Ranges()
    {
        var counter = RangeCounter.Standard;
        var stopwatch = Stopwatch.StartNew();

        Console.WriteLine("Raw storage, for comparison:");
        foreach (int bits in new[] { 4, 5, 6 })
            Console.WriteLine($"  {bits} bits/square ({bits * 8} bytes): {Describe(counter.RawBoards(bits))}");
        Console.WriteLine();

        Console.WriteLine("Boards surviving each tier (6-bit, 48 bytes):");
        BigInteger previous = BigInteger.Zero;
        for (int tier = Tiers.Raw; tier <= Tiers.Material; tier++)
        {
            BigInteger count = counter.BoardsAtTier(tier);
            string kept = tier == Tiers.Raw ? "" : $"  (keeps 1 in {Ratio(previous, count)})";
            Console.WriteLine($"  tier {tier}  {Tiers.Names[tier]}");
            Console.WriteLine($"          {Describe(count)}{kept}");
            previous = count;
        }
        Console.WriteLine();

        BigInteger noPromotions = new RangeCounter(BoardGeometry.Standard, MaterialRules.StandardNoPromotions)
            .MaterialBoards();
        Console.WriteLine("For comparison, tier 4 if pawns could never promote:");
        Console.WriteLine($"          {Describe(noPromotions)}  (promotions multiply the space by {Ratio(previous, noPromotions)})");
        Console.WriteLine();
        Console.WriteLine($"Exact tier 4 count:\n  {previous}");
        Console.WriteLine($"({stopwatch.Elapsed.TotalSeconds:0.00}s)");
        return 0;
    }

    private static int Check(IEnumerable<string> inputs)
    {
        int invalid = 0;
        foreach (string input in inputs)
        {
            var board = ParseBoard(input);
            var result = BoardChecker.Standard.Check(board);
            Console.WriteLine(input);
            Report(result);
            if (!result.IsValid)
                invalid++;
        }
        return invalid == 0 ? 0 : 2;
    }

    private static int Show(string input)
    {
        var board = ParseBoard(input);
        Console.WriteLine(board.ToDiagram());
        Console.WriteLine($"FEN: {Fen.ToPlacement(board) ?? "(has meaningless codes)"}");
        Console.WriteLine($"hex: {board.ToHex()}");
        Report(BoardChecker.Standard.Check(board));
        return 0;
    }

    private static int Moves(string fen)
    {
        var position = Position.FromFen(fen);
        var moves = MoveGenerator.Legal(position);
        Console.WriteLine(position.ToPackedBoard().ToDiagram());
        Console.WriteLine(position.ToFen());
        string status = position.Status() switch
        {
            GameStatus.Checkmate => "checkmate",
            GameStatus.Stalemate => "stalemate",
            _ when position.InCheck() => "in check",
            _ => "to move",
        };
        Console.WriteLine($"{position.SideToMove} {status}, {moves.Count} legal moves:");
        Console.WriteLine(string.Join(" ", moves.Select(m => m.ToUci()).Order()));
        return 0;
    }

    private static int RunPerft(int depth, string fen, bool divide)
    {
        var position = Position.FromFen(fen);
        var stopwatch = Stopwatch.StartNew();
        long nodes;
        if (divide)
        {
            var split = Perft.Divide(position, depth);
            foreach (var (move, count) in split.OrderBy(s => s.Move.ToUci()))
                Console.WriteLine($"{move.ToUci()}: {count}");
            nodes = split.Sum(s => s.Nodes);
        }
        else
        {
            nodes = Perft.Count(position, depth);
        }
        double seconds = stopwatch.Elapsed.TotalSeconds;
        Console.WriteLine($"perft {depth}: {nodes:N0} nodes in {seconds:0.00}s ({nodes / Math.Max(seconds, 1e-9) / 1e6:0.0}M nodes/s)");
        return 0;
    }

    private static readonly string TableDirectory = Environment.GetEnvironmentVariable("CHESS_TABLES") ?? "tables";

    /// <summary>Rewrite every table saved before symmetry ("CBT1") in the new, smaller format.</summary>
    private static int Upgrade(string directory)
    {
        int upgraded = 0;
        var files = Directory.EnumerateFiles(directory, "*.cbt").Concat(Directory.EnumerateFiles(directory, "*.cbz"));
        foreach (string path in files.Order())
        {
            // From before symmetry, with identical pieces in every order, or DTZ at two bytes a value.
            if (!EndgameTable.IsOutdatedFile(path))
                continue;
            if (File.Exists(Path.ChangeExtension(path, ".cbf")))
            {
                Console.WriteLine($"{Path.GetFileName(path),-14} capped, with a frontier: left as it is (solve it again to upgrade)");
                continue;
            }
            long before = new FileInfo(path).Length;
            using (var table = EndgameTable.Load(path, intoMemory: true))   // in memory: the file is rewritten in place
                table.Save(path);
            Console.WriteLine($"{Path.GetFileName(path),-14} {before / 1048576.0,8:0.0} MB -> {new FileInfo(path).Length / 1048576.0,6:0.0} MB");
            upgraded++;
        }
        return Print($"{upgraded} tables upgraded in {directory}");
    }

    /// <summary>
    /// Two table files of one material, value by value in the current
    /// numbering (older files are renumbered as they load): for checking a
    /// change of format, where the bytes may differ but no value may.
    /// </summary>
    private static int Compare(string first, string second)
    {
        using var a = EndgameTable.Load(first);
        using var b = EndgameTable.Load(second);
        if (a.Material != b.Material || a.Size != b.Size)
            return Print($"different tables: {a.Material} ({a.Size:N0} slots) and {b.Material} ({b.Size:N0} slots)", 2);
        var (differ, legal, firstDiffer) = Differences(a, b);
        if (differ == 0)
            return Print($"{a.Material}: {a.Size:N0} slots, {legal:N0} legal, all equal");
        Console.WriteLine($"{a.Material}: {differ:N0} of {a.Size:N0} slots differ; first at {firstDiffer:N0} " +
                          $"({a.PositionAt(firstDiffer).ToFen()}): {a[firstDiffer]?.ToString() ?? "impossible"} v {b[firstDiffer]?.ToString() ?? "impossible"}");
        return 2;
    }

    /// <summary>Two tables of one material and size, value by value: how many differ, how many are legal, the first to differ (-1 if none).</summary>
    private static (long Differ, long Legal, long First) Differences(EndgameTable a, EndgameTable b)
    {
        long differ = 0, legal = 0;
        long firstDiffer = -1;
        for (long i = 0; i < a.Size; i++)
        {
            var x = a[i];
            if (x is not null)
                legal++;
            if (x != b[i])
            {
                differ++;
                if (firstDiffer < 0)
                    firstDiffer = i;
            }
        }
        return (differ, legal, firstDiffer);
    }

    /// <summary>
    /// Compress every complete table in a folder (or one file) in place.
    /// Each is written to a temporary file, read back and compared with the
    /// plain one value by value, and only then moved over it.  Tables already
    /// compressed are skipped, and so are capped ones (they grow deeper yet).
    /// </summary>
    private static int Compress(string target, int quality)
    {
        if (quality is < 0 or > 11)
            return Print("error: Brotli's quality is 0 to 11", 1);
        var files = Directory.Exists(target)
            ? Directory.EnumerateFiles(target, "*.cbt").Concat(Directory.EnumerateFiles(target, "*.cbz")).Order().ToList()
            : File.Exists(target) ? new List<string> { target } : throw new FileNotFoundException($"No table or folder {target}.");
        int compressed = 0, already = 0;
        long before = 0, after = 0;
        var total = Stopwatch.StartNew();
        foreach (string path in files)
        {
            string name = Path.GetFileName(path);
            if (EndgameTable.IsCompressedFile(path))
            {
                already++;
                continue;
            }
            string temp = path + ".tmp";
            var watch = Stopwatch.StartNew();
            long plainBytes = new FileInfo(path).Length, packedBytes;
            using (var table = EndgameTable.Load(path))
            {
                if (table.Cap is not null)
                {
                    Console.WriteLine($"{name,-14} capped at {table.Cap} plies: left plain (only complete tables are compressed)");
                    continue;
                }
                packedBytes = table.SaveCompressed(temp, quality);
                using var check = EndgameTable.Load(temp);
                var (differ, _, first) = Differences(table, check);
                if (differ != 0)
                {
                    check.Dispose();
                    File.Delete(temp);
                    return Print($"{name}: {differ:N0} values differ once compressed (first at {first:N0}): left plain, stopping", 2);
                }
            }
            File.Move(temp, path, overwrite: true);
            before += plainBytes;
            after += packedBytes;
            compressed++;
            Console.WriteLine($"{name,-14} {plainBytes / 1048576.0,9:0.0} MB -> {packedBytes / 1048576.0,7:0.0} MB " +
                              $"({(double)plainBytes / packedBytes,5:0.0}x) in {watch.Elapsed.TotalSeconds,5:0.0}s");
        }
        string ratio = after == 0 ? "" : $" ({(double)before / after:0.0}x)";
        return Print($"{compressed} tables compressed in {total.Elapsed.TotalMinutes:0.0} min, " +
                     $"{before / 1048576.0:0.0} MB -> {after / 1048576.0:0.0} MB{ratio}; {already} already compressed");
    }

    private static Tablebase OpenTablebase(int? cap = null) =>
        new(TableDirectory, new Progress<string>(message => Console.Error.Write($"\r{message,-70}"))) { Cap = cap };

    private static int Solve(string text, int? cap)
    {
        var material = Material.Parse(text);
        var stopwatch = Stopwatch.StartNew();
        var table = OpenTablebase(cap).Get(material);
        Console.Error.Write($"\r{"",-70}\r");
        var stats = table.Statistics();

        Console.WriteLine($"{table.Material}: {table.Size:N0} indexed slots ({table.Size * 2 / 1024.0 / 1024.0:0.0} MB), " +
                          $"ready in {stopwatch.Elapsed.TotalSeconds:0.0}s");
        if (table.SolverMemory is { } memory)
            Console.WriteLine($"  solver memory: {Mb(memory.TotalBytes)} = arrays {Mb(memory.ArrayBytes)} + queues " +
                              $"{Mb(memory.QueueBytes)} ({memory.QueueEntries:N0} entries); process peak " +
                              $"{Mb(Process.GetCurrentProcess().PeakWorkingSet64)}");
        foreach (var side in new[] { Colour.White, Colour.Black })
        {
            int s = (int)side;
            long legal = stats.Legal(side);
            Console.WriteLine($"  {side} to move: {legal:N0} legal positions");
            Console.WriteLine($"    wins   {stats.Wins[s],10:N0}  ({100.0 * stats.Wins[s] / legal:0.0}%)");
            Console.WriteLine($"    draws  {stats.Draws[s],10:N0}  ({100.0 * stats.Draws[s] / legal:0.0}%)");
            Console.WriteLine($"    losses {stats.Losses[s],10:N0}  ({100.0 * stats.Losses[s] / legal:0.0}%)");
            if (table.Cap is int tableCap)
                Console.WriteLine($"    beyond {stats.Beyond[s],10:N0}  ({100.0 * stats.Beyond[s] / legal:0.0}%)  " +
                                  $"not settled within {tableCap} plies: longer wins and losses, and draws");
            if (stats.Longest[s] is var (outcome, index))
                Console.WriteLine($"    longest: {outcome}, e.g. {table.PositionAt(index).ToFen()}");
        }
        return 0;
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:0.0} MB";

    private static int Verify(string text, int stride, int? cap)
    {
        var material = Material.Parse(text);
        var stopwatch = Stopwatch.StartNew();
        var (checkedPositions, mismatches) = OpenTablebase(cap).Verify(material, stride,
            new Progress<string>(message => Console.Error.Write($"\r{message,-70}")));
        Console.Error.Write($"\r{"",-70}\r");
        foreach (string mismatch in mismatches)
            Console.WriteLine($"  MISMATCH {mismatch}");
        Console.WriteLine($"{material.Canonical}: {checkedPositions:N0} positions checked in " +
                          $"{stopwatch.Elapsed.TotalSeconds:0.0}s, {(mismatches.Count == 0 ? "all consistent" : $"{mismatches.Count}+ mismatches")}");
        return mismatches.Count == 0 ? 0 : 2;
    }

    /// <summary>Under the 50-move rule alone: needs only the DTZ tables, not the mate distances.</summary>
    private static int ProbeUnderRule(string fen)
    {
        var position = Position.FromFen(fen);
        var tablebase = OpenTablebase();
        var outcome = tablebase.ProbeDtz(position);
        var ranked = tablebase.RankMovesUnderRule(position);
        Console.Error.Write($"\r{"",-70}\r");
        Console.WriteLine(position.ToPackedBoard().ToDiagram());
        Console.WriteLine($"{position.SideToMove} to move, under the 50-move rule: {DescribeDtz(outcome)}");
        foreach (var (move, result) in ranked)
            Console.WriteLine($"  {move.ToUci(),-6} {DescribeDtz(result)}");
        return 0;
    }

    private static int Probe(string fen)
    {
        var position = Position.FromFen(fen);
        var tablebase = OpenTablebase();
        var outcome = tablebase.Probe(position);
        var ranked = tablebase.RankMoves(position);
        Console.Error.Write($"\r{"",-70}\r");
        Console.WriteLine(position.ToPackedBoard().ToDiagram());
        var underRule = tablebase.ProbeDtz(position);
        var ruled = tablebase.RankMovesUnderRule(position).ToDictionary(r => r.Move, r => r.Outcome);
        Console.Error.Write($"\r{"",-70}\r");
        Console.WriteLine($"{position.SideToMove} to move: {outcome}");
        Console.WriteLine($"  under the 50-move rule: {DescribeDtz(underRule)}");
        foreach (var (move, result) in ranked)
            Console.WriteLine($"  {move.ToUci(),-6} {result,-45} rule: {DescribeDtz(ruled[move])}");
        return 0;
    }

    /// <summary>A DTZ result: its distance counts to the next capture or pawn move, not to mate.</summary>
    private static string DescribeDtz(Outcome outcome) => outcome.Kind switch
    {
        OutcomeKind.Win => $"win, capture/pawn move/mate in {outcome.Plies} plies",
        OutcomeKind.Loss when outcome.Plies == 0 => "loss, checkmated",
        OutcomeKind.Loss => $"loss, opponent's capture/pawn move/mate in {outcome.Plies} plies",
        _ => "draw",
    };

    private static int Dtz(string text)
    {
        var material = Material.Parse(text);
        var stopwatch = Stopwatch.StartNew();
        var tablebase = OpenTablebase();
        var dtz = tablebase.GetDtz(material);
        Console.Error.Write($"\r{"",-70}\r");
        Console.WriteLine($"{dtz.Material} under the 50-move rule: ready in {stopwatch.Elapsed.TotalSeconds:0.0}s");
        if (dtz.SolverMemory is { } memory)
            Console.WriteLine($"  solver memory: {Mb(memory.TotalBytes)} = arrays {Mb(memory.ArrayBytes)} + queues " +
                              $"{Mb(memory.QueueBytes)} ({memory.QueueEntries:N0} entries); process peak " +
                              $"{Mb(Process.GetCurrentProcess().PeakWorkingSet64)}");

        // Best play without the rule, for the cursed wins: only if that table is already on disk
        // (at 5 pieces it is a solve of its own, and a complete one is needed to compare).
        EndgameTable? full = null;
        using var onDisk = new Tablebase(TableDirectory) { SolveMissing = false };
        try
        {
            full = onDisk.Get(material);
            if (full.Cap is not null)
                full = null;
        }
        catch (TableMissingException)
        {
        }
        var stats = dtz.Statistics();
        var fullStats = full?.Statistics();
        var (cursed, blessed) = full?.RuleDraws(dtz) ?? (null!, null!);

        foreach (var side in new[] { Colour.White, Colour.Black })
        {
            int s = (int)side;
            long legal = stats.Legal(side);
            string Without(long[]? counts) => counts is null ? "" : $"  [{counts[s]:N0}]";
            Console.WriteLine($"  {side} to move: {legal:N0} legal positions" +
                              (full is null ? "" : " (best play without the rule in brackets)"));
            Console.WriteLine($"    wins   {stats.Wins[s],10:N0}  ({100.0 * stats.Wins[s] / legal:0.0}%){Without(fullStats?.Wins)}");
            Console.WriteLine($"    draws  {stats.Draws[s],10:N0}  ({100.0 * stats.Draws[s] / legal:0.0}%){Without(fullStats?.Draws)}");
            Console.WriteLine($"    losses {stats.Losses[s],10:N0}  ({100.0 * stats.Losses[s] / legal:0.0}%){Without(fullStats?.Losses)}");
            Console.WriteLine(full is null
                ? "    cursed wins, blessed losses: solve the table without the rule first to compare"
                : $"    cursed wins {cursed[s]:N0}, blessed losses {blessed[s]:N0}");
            if (stats.Longest[s] is var (outcome, index))
                Console.WriteLine($"    longest: {DescribeDtz(outcome)}, e.g. {dtz.PositionAt(index).ToFen()}");
        }
        return 0;
    }

    private static int DtzVerify(string text, int stride)
    {
        var material = Material.Parse(text);
        var stopwatch = Stopwatch.StartNew();
        var (checkedPositions, mismatches) = OpenTablebase().VerifyDtz(material, stride,
            new Progress<string>(message => Console.Error.Write($"\r{message,-70}")));
        Console.Error.Write($"\r{"",-70}\r");
        foreach (string mismatch in mismatches)
            Console.WriteLine($"  MISMATCH {mismatch}");
        Console.WriteLine($"{material.Canonical} DTZ: {checkedPositions:N0} positions checked in " +
                          $"{stopwatch.Elapsed.TotalSeconds:0.0}s, {(mismatches.Count == 0 ? "all consistent" : $"{mismatches.Count}+ mismatches")}");
        return mismatches.Count == 0 ? 0 : 2;
    }

    private static int Line(string fen)
    {
        var position = Position.FromFen(fen);
        var tablebase = OpenTablebase();
        var outcome = tablebase.Probe(position);
        var line = tablebase.PrincipalLine(position);
        Console.Error.Write($"\r{"",-70}\r");
        Console.WriteLine($"{position.SideToMove} to move: {outcome}");

        var text = new System.Text.StringBuilder();
        int number = position.FullmoveNumber;
        var colour = position.SideToMove;
        if (colour == Colour.Black)
            text.Append($"{number}... ");
        foreach (var move in line)
        {
            if (colour == Colour.White)
                text.Append($"{number}. ");
            text.Append(move.ToUci()).Append(' ');
            if (colour == Colour.Black)
                number++;
            colour = Position.Opponent(colour);
        }
        Console.WriteLine(text.ToString().TrimEnd());
        foreach (var move in line)
            position.MakeMove(move);
        Console.WriteLine($"final: {position.ToFen()} ({position.Status().ToString().ToLowerInvariant()})");
        return 0;
    }

    private static int Sample(int count, int? seed)
    {
        var rng = seed is int s ? new Random(s) : new Random();
        var superposed = new SuperposedBoard();
        var rawTiers = new long[Tiers.Material + 1];
        var validTiers = new long[Tiers.Material + 1];
        for (int i = 0; i < count; i++)
        {
            rawTiers[BoardChecker.Standard.Check(superposed.Collapse(rng)).HighestTierPassed]++;
            validTiers[BoardChecker.Standard.Check(superposed.CollapseToValidCodes(rng)).HighestTierPassed]++;
        }

        Console.WriteLine($"{count:N0} boards collapsed from full superposition (2^384 states):");
        PrintTally(rawTiers);
        Console.WriteLine();
        Console.WriteLine($"{count:N0} boards collapsed among meaningful codes only (13^64 states):");
        PrintTally(validTiers);
        return 0;
    }

    private static int FileAdd(string file, IEnumerable<string> inputs)
    {
        long written = BoardFile.Append(file, inputs.Select(ParseBoard));
        return Print($"appended {written:N0} boards to {file} (now {BoardFile.Count(file):N0})");
    }

    private static int FileList(string file)
    {
        foreach (var board in BoardFile.Read(file))
            Console.WriteLine(Fen.ToPlacement(board) ?? board.ToHex());
        return 0;
    }

    private static int FileExport(string file, string text)
    {
        File.WriteAllLines(text, BoardFile.Read(file).Select(b => Fen.ToPlacement(b) ?? b.ToHex()));
        return Print($"wrote {BoardFile.Count(file):N0} lines to {text}");
    }

    private static int FileCheck(string file)
    {
        var tiers = new long[Tiers.Material + 1];
        long index = 0;
        foreach (var board in BoardFile.Read(file))
        {
            var result = BoardChecker.Standard.Check(board);
            tiers[result.HighestTierPassed]++;
            if (!result.IsValid)
                Console.WriteLine($"#{index}: {string.Join(" ", result.Problems)}");
            index++;
        }
        Console.WriteLine($"{index:N0} boards checked:");
        PrintTally(tiers);
        return tiers[Tiers.Material] == index ? 0 : 2;
    }

    private static int FileDedupe(string file, string? output)
    {
        var (before, after) = BoardFile.Deduplicate(file, output);
        return Print($"{before:N0} boards -> {after:N0} unique ({before - after:N0} duplicates removed), sorted, in {output ?? file}");
    }

    private static int GameImport(string file, string[] pgnFiles)
    {
        long before = File.Exists(file) ? new FileInfo(file).Length : 0;
        var (games, moves) = GameFile.Append(file, pgnFiles.SelectMany(Pgn.ReadFile));
        long added = new FileInfo(file).Length - before;
        return Print($"appended {games:N0} games ({moves:N0} moves) to {file}, now {GameFile.Count(file):N0} games\n" +
                     $"  {added:N0} bytes: {moves:N0} for moves (1 each), {added - moves:N0} for tags and headers");
    }

    private static int GameExport(string file, string pgnFile)
    {
        long count = 0;
        using (var writer = new StreamWriter(pgnFile))
        {
            foreach (var game in GameFile.Read(file))
            {
                writer.Write(game.ToPgn());
                count++;
            }
        }
        return Print($"wrote {count:N0} games to {pgnFile}");
    }

    private static int GameList(string file)
    {
        long number = 0;
        foreach (var game in GameFile.Read(file))
        {
            number++;
            string players = $"{game.Tag("White") ?? "?"} v {game.Tag("Black") ?? "?"}";
            string start = game.Tag("FEN") is null ? "" : "  (from a set-up position)";
            Console.WriteLine($"{number,6}  {game.Result,-7}  {game.Moves.Count,4} plies  {players}{start}");
        }
        return 0;
    }

    /// <summary>
    /// The position after <paramref name="plyText"/> half-moves of game <paramref name="number"/>:
    /// a count from the start, a negative count back from the end, or "end".
    /// </summary>
    private static int GameShow(string file, long number, string plyText)
    {
        var game = GameFile.Read(file, number);
        int count = game.Moves.Count;
        int ply = plyText == "end" ? count : int.Parse(plyText);
        if (ply < 0)
            ply += count;
        if (ply < 0 || ply > count)
            throw new ArgumentException($"Game {number} has {count} plies: give 0 to {count}, or -1 to -{count} from the end.");

        var san = game.San();
        var start = game.StartPosition();
        int offset = start.SideToMove == Colour.Black ? 1 : 0;
        string Label(int i) =>
            $"{start.FullmoveNumber + (i + offset) / 2}{((i + offset) % 2 == 0 ? "." : "...")} {san[i]}";

        Console.WriteLine($"game {number:N0}: {game.Tag("White") ?? "?"} v {game.Tag("Black") ?? "?"}, " +
                          $"{game.Result}, {count} plies");
        Console.WriteLine(ply == 0 ? "at the start" : $"after {ply} {(ply == 1 ? "ply" : "plies")}, the last {Label(ply - 1)}");

        var position = game.PositionAt(ply);
        Console.WriteLine(position.ToPackedBoard().ToDiagram());
        Console.WriteLine(position.ToFen());
        string status = position.Status() switch
        {
            GameStatus.Checkmate => "checkmated",
            GameStatus.Stalemate => "stalemated",
            _ when position.InCheck() => "to move, in check",
            _ => "to move",
        };
        string next = ply < count ? $"next in the game: {Label(ply)}" : $"the game ends here: {game.Result}";
        Console.WriteLine($"{position.SideToMove} {status}; {next}");

        // The whole game, with | where this position sits.
        var words = new List<string>();
        for (int i = 0; i < count; i++)
        {
            if (i == ply)
                words.Add("|");
            bool white = (i + offset) % 2 == 0;
            words.Add(white || i == 0 || i == ply ? Label(i) : san[i]);
        }
        if (ply == count)
            words.Add("|");
        words.Add(game.Result);
        var line = new System.Text.StringBuilder();
        foreach (string word in words)
        {
            if (line.Length > 0 && line.Length + word.Length + 1 > 79)
            {
                Console.WriteLine(line);
                line.Clear();
            }
            if (line.Length > 0)
                line.Append(' ');
            line.Append(word);
        }
        Console.WriteLine(line);
        return 0;
    }

    private sealed record Graded(long Number, StoredGame Game, GameMetrics Metrics, GameStyle Style);

    /// <summary>Sort every game into Matthew's three styles (plus draws and clock losses), with examples of each.</summary>
    /// <summary>
    /// Matthew's stalling question: when nothing is happening, do players send
    /// pieces straight back, walk them round cycles, or keep finding new
    /// arrangements?  And do cycles catch games the grader's rules miss?
    /// </summary>
    private static int GameIdle(string[] files)
    {
        const int CycleLimit = 3;
        long games = 0, withIdle = 0, idle = 0, straight = 0, cycles = 0, fresh = 0;
        long cyclers = 0, cyclersAlreadyFlagged = 0, cyclersByQuiet = 0, cyclersByShuffle = 0, cyclersByRepeat = 0;
        long[] byStyle = new long[Enum.GetValues<GameStyle>().Length];
        var cycleShare = new List<double>();   // per game with 10+ idle moves: the share that are cycles
        foreach (string file in files)
        {
            foreach (var game in GameFile.Read(file))
            {
                games++;
                var moves = GameAnalysis.Idle(game);
                if (moves.Moves == 0)
                    continue;
                withIdle++;
                idle += moves.Moves;
                straight += moves.StraightBack;
                cycles += moves.Cycles;
                fresh += moves.Fresh;
                if (moves.Moves >= 10)
                    cycleShare.Add((double)moves.Cycles / moves.Moves);
                if (moves.Cycles < CycleLimit)
                    continue;
                cyclers++;
                var metrics = GameAnalysis.Measure(game);
                byStyle[(int)GameAnalysis.Grade(game, metrics)]++;
                if (GameAnalysis.MarksTime(metrics))
                    cyclersAlreadyFlagged++;
                if (metrics.LongestQuiet >= GameAnalysis.QuietLimit)
                    cyclersByQuiet++;
                if (metrics.IdleShuffles >= GameAnalysis.ShuffleLimit)
                    cyclersByShuffle++;
                if (metrics.Repeats > 0)
                    cyclersByRepeat++;
            }
        }

        double Pct(long part, long whole) => whole == 0 ? 0 : 100.0 * part / whole;
        Console.WriteLine($"{games:N0} games; {withIdle:N0} ({Pct(withIdle, games):0.0}%) have idle moves " +
                          $"({GameAnalysis.IdlePlies}+ plies into a stretch with no capture or pawn move).");
        Console.WriteLine();
        Console.WriteLine($"Idle moves: {idle:N0}");
        Console.WriteLine($"  straight back   {straight,12:N0}  ({Pct(straight, idle):0.0}%)  the shuffle: a piece returns to where it just came from");
        Console.WriteLine($"  cycle           {cycles,12:N0}  ({Pct(cycles, idle):0.0}%)  back to an arrangement already had this stretch, another way");
        Console.WriteLine($"  fresh           {fresh,12:N0}  ({Pct(fresh, idle):0.0}%)  an arrangement new to the stretch");
        Console.WriteLine();
        if (cycleShare.Count > 0)
        {
            cycleShare.Sort();
            double At(double q) => 100 * cycleShare[(int)Math.Min(cycleShare.Count - 1, q * cycleShare.Count)];
            Console.WriteLine($"Games with 10+ idle moves: {cycleShare.Count:N0}. Share of their idle moves that are cycles: " +
                              $"median {At(0.5):0}%, 90th percentile {At(0.9):0}%, 99th {At(0.99):0}%.");
            Console.WriteLine();
        }
        Console.WriteLine($"Games with {CycleLimit}+ cycle moves: {cyclers:N0} ({Pct(cyclers, games):0.00}% of games)");
        Console.WriteLine($"  already flagged as marking time  {cyclersAlreadyFlagged,9:N0}  ({Pct(cyclersAlreadyFlagged, cyclers):0.0}%)");
        Console.WriteLine($"    by a long quiet stretch ({GameAnalysis.QuietLimit}+)   {cyclersByQuiet,9:N0}");
        Console.WriteLine($"    by idle shuffling              {cyclersByShuffle,9:N0}");
        Console.WriteLine($"    by a repeated position         {cyclersByRepeat,9:N0}");
        Console.WriteLine($"  flagged by nothing else          {cyclers - cyclersAlreadyFlagged,9:N0}  " +
                          $"({Pct(cyclers - cyclersAlreadyFlagged, cyclers):0.0}%): what a cycle rule would add");
        Console.WriteLine($"  by style: " + string.Join(", ", Enum.GetValues<GameStyle>()
            .Where(style => byStyle[(int)style] > 0).Select(style => $"{style} {byStyle[(int)style]:N0}")));
        return 0;
    }

    private static int GameGrade(string file, int examples)
    {
        var styles = Enum.GetValues<GameStyle>();
        var byStyle = styles.ToDictionary(s => s, _ => new List<Graded>());
        long number = 0;
        foreach (var game in GameFile.Read(file))
        {
            number++;
            var metrics = GameAnalysis.Measure(game);
            var style = GameAnalysis.Grade(game, metrics);
            byStyle[style].Add(new Graded(number, game, metrics, style));
        }

        Console.WriteLine($"{number:N0} games. Early kill: won within {GameAnalysis.EarlyKillPlies} plies. " +
                          $"Marking time: a stretch of {GameAnalysis.QuietLimit}+ plies with no capture or pawn move,");
        Console.WriteLine($"{GameAnalysis.ShuffleLimit}+ pieces sent straight back where they came from while nothing was happening " +
                          $"({GameAnalysis.IdlePlies}+ plies without a capture or pawn move), or a position repeated.");
        Console.WriteLine();
        Console.WriteLine($"{"style",-13} {"games",9} {"share",7} {"avg plies",10} {"by mate",8} {"avg rating",11}");
        foreach (var style in styles)
        {
            var list = byStyle[style];
            if (list.Count == 0)
                continue;
            double rating = list.Select(g => AverageRating(g.Game)).Where(r => r > 0).DefaultIfEmpty(0).Average();
            Console.WriteLine($"{Name(style),-13} {list.Count,9:N0} {100.0 * list.Count / number,6:0.0}% " +
                              $"{list.Average(g => g.Metrics.Plies),10:0.0} {100.0 * list.Count(g => g.Metrics.EndsInMate) / list.Count,7:0}% " +
                              $"{rating,11:0}");
        }

        var wasters = byStyle[GameStyle.TimeWaster];
        if (wasters.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Time wasters, by what gave them away (a game can show several):");
            Console.WriteLine($"  long quiet stretch {wasters.Count(g => g.Metrics.LongestQuiet >= GameAnalysis.QuietLimit),9:N0}");
            Console.WriteLine($"  idle shuffling     {wasters.Count(g => g.Metrics.IdleShuffles >= GameAnalysis.ShuffleLimit),9:N0}");
            Console.WriteLine($"  repeated position  {wasters.Count(g => g.Metrics.Repeats > 0),9:N0}");
            Console.WriteLine($"  shuffling only     {wasters.Count(g => g.Metrics.Repeats == 0 && g.Metrics.LongestQuiet < GameAnalysis.QuietLimit),9:N0}");
            int won = wasters.Count(g => g.Game.Result != "1/2-1/2");
            Console.WriteLine($"  ...and then won    {won,9:N0} ({100.0 * won / wasters.Count:0}%): the long plan that may have paid off");
        }

        // Examples: the sharpest of each kind, which is where a human eye learns most.
        double median = Median(byStyle[GameStyle.Efficient]);
        var picks = new (GameStyle Style, string Why, Func<Graded, double> Key)[]
        {
            (GameStyle.EarlyKill, "fastest mates", g => g.Metrics.EndsInMate ? g.Metrics.Plies : 1e9),
            (GameStyle.Efficient, $"won by mate, nearest the median length ({median} plies)", g => g.Metrics.EndsInMate ? Math.Abs(g.Metrics.Plies - median) : 1e9),
            (GameStyle.TimeWaster, "longest quiet stretch that still ended in mate", g => g.Metrics.EndsInMate ? -g.Metrics.LongestQuiet : 1e9),
        };
        foreach (var (style, why, key) in picks)
        {
            if (byStyle[style].Count == 0 || examples == 0)
                continue;
            Console.WriteLine();
            Console.WriteLine($"{Name(style)}: {why}");
            foreach (var g in byStyle[style].OrderBy(key).ThenBy(g => g.Number).Take(examples))
            {
                var m = g.Metrics;
                Console.WriteLine($"  #{g.Number,-7} {g.Game.Result,-7} {m.Plies,3} plies  quiet {m.LongestQuiet,3}  " +
                                  $"shuffles {m.IdleShuffles,2}/{m.Shuffles,-2} idle/all  repeats {m.Repeats,2}  {g.Game.Tag("Site") ?? ""}");
            }
        }
        return 0;

        static string Name(GameStyle style) => style switch
        {
            GameStyle.EarlyKill => "early kill",
            GameStyle.Efficient => "efficient",
            GameStyle.TimeWaster => "time waster",
            GameStyle.CleanDraw => "clean draw",
            _ => "clock",
        };

        static double Median(List<Graded> list) =>
            list.Count == 0 ? 0 : list.Select(g => g.Metrics.Plies).Order().ElementAt(list.Count / 2);
    }

    private static double AverageRating(StoredGame game) =>
        int.TryParse(game.Tag("WhiteElo"), out int white) && int.TryParse(game.Tag("BlackElo"), out int black)
            ? (white + black) / 2.0 : 0;

    /// <summary>
    /// How much the games share, stored as one tree of moves (and as one set of
    /// positions).  With several files, each is added on top of the ones before,
    /// so the report shows what each one still adds.
    /// </summary>
    private static int GameTree(string[] files)
    {
        var stats = new GameTreeStats();
        static double Share(long part, long whole) => 100.0 * part / Math.Max(1, whole);
        var ends = new List<string>();
        Console.WriteLine($"{"file",-22} {"games",9} {"plies",11} {"new nodes",11} {"new",6} {"new positions",14} {"new",6}  games new from ply");
        foreach (string file in files)
        {
            long games = stats.Games, plies = stats.Plies, nodes = stats.UniquePrefixes, positions = stats.UniquePositions;
            var opening = stats.Opening;
            var endgames = stats.Endgames.Select(e => e.Copy()).ToList();
            foreach (var game in GameFile.Read(file))
                stats.Add(game);
            long addedPlies = stats.Plies - plies;
            long addedNodes = stats.UniquePrefixes - nodes, addedPositions = stats.UniquePositions - positions;
            var newFrom = stats.NewFrom.Skip((int)games).Order().ToList();
            int Percentile(double p) => newFrom.Count == 0 ? 0 : newFrom[(int)Math.Min(newFrom.Count - 1, p * newFrom.Count)];
            Console.WriteLine($"{Path.GetFileName(file),-22} {stats.Games - games,9:N0} {addedPlies,11:N0} " +
                              $"{addedNodes,11:N0} {Share(addedNodes, addedPlies),5:0.0}% " +
                              $"{addedPositions,14:N0} {Share(addedPositions, addedPlies),5:0.0}%  " +
                              $"median {Percentile(0.5)}, 90% by {Percentile(0.9)}");

            // The two ends of the game: the first 6 moves each, and the last few pieces.
            var line = new System.Text.StringBuilder();
            line.Append($"{Path.GetFileName(file),-22} {Share(stats.Opening.New - opening.New, stats.Opening.Plies - opening.Plies),8:0.0}%");
            for (int b = 0; b < endgames.Count; b++)
            {
                var before = endgames[b];
                var now = stats.Endgames[b];
                long reached = now.Games - before.Games;
                line.Append($"   {Share(reached, stats.Games - games),5:0.0}% {Share(now.New - before.New, now.Plies - before.Plies),6:0.0}% " +
                            $"{Share(now.KnownEntries - before.KnownEntries, reached),7:0.0}%");
            }
            ends.Add(line.ToString());
        }

        Console.WriteLine();
        Console.WriteLine($"{"",-22} {"opening",9}   {"--- 6 pieces or fewer ---",-24}   {"--- 4 or fewer (tables) ---",-24}");
        Console.WriteLine($"{"file",-22} {"new",9}   {"games",6} {"new",7} {"arrive",8}   {"games",6} {"new",7} {"arrive",8}");
        Console.WriteLine($"{"",-22} {"",9}   {"reach",6} {"pos.",7} {"known",8}   {"reach",6} {"pos.",7} {"known",8}");
        foreach (string line in ends)
            Console.WriteLine(line);
        Console.WriteLine($"(opening = first {GameTreeStats.OpeningPlies} plies, new tree nodes; \"arrive known\" = the game's first " +
                          "such position had been reached by an earlier game)");

        long all = stats.Plies;
        Console.WriteLine();
        Console.WriteLine($"all together: {stats.Games:N0} games, {all:N0} plies stored game by game");
        Console.WriteLine($"  as a tree of moves:     {stats.UniquePrefixes,12:N0} nodes " +
                          $"({100.0 * stats.UniquePrefixes / all:0.0}%: shared openings stored once)");
        Console.WriteLine($"  as a set of positions:  {stats.UniquePositions,12:N0} positions " +
                          $"({100.0 * stats.UniquePositions / all:0.0}%: transpositions merged too)");
        Console.WriteLine($"  games played before, move for move: {stats.DuplicateGames:N0}");
        return 0;
    }

    /// <summary>
    /// Which endgame tables the games reach (step B of the pawn-table plan).
    /// The tables we have are the .cbt files in the tables folder.  The 5-piece
    /// tables with pawns still missing are put in an order to build them (see
    /// <see cref="EndingStats.BuildOrder"/>: a table comes after the ones its
    /// promotions lead to), with the share of games each one brings wholly
    /// inside the tables and the share of the work (positions) it costs.
    /// </summary>
    private static int GameEndings(string[] files)
    {
        var watch = Stopwatch.StartNew();
        var stats = new EndingStats();
        foreach (string file in files)
            foreach (var game in GameFile.Read(file))
                stats.Add(game);

        var have = new HashSet<Material>();
        if (Directory.Exists(TableDirectory))
            foreach (string path in Directory.EnumerateFiles(TableDirectory, "*.cbt"))
                try { have.Add(Material.Parse(Path.GetFileNameWithoutExtension(path)).Canonical); }
                catch (FormatException) { }

        // Every 5-piece table with a pawn: three pieces against a bare king, or two against one.
        var types = new[] { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight, PieceType.Pawn };
        var sets = new List<PieceType[]>();
        for (int a = 0; a < 5; a++)
            for (int b = a; b < 5; b++)
                sets.Add([types[a], types[b]]);
        var pawnTables = new HashSet<Material>();
        foreach (var pair in sets)
        {
            foreach (var type in types)
            {
                var three = pair.Append(type).ToArray();
                pawnTables.Add(new Material(three, []).Canonical);
                pawnTables.Add(new Material(pair, [type]).Canonical);
            }
        }
        pawnTables.RemoveWhere(m => !m.HasPawns);
        int Pawns(Material m) => m.White.Count(t => t == PieceType.Pawn) + m.Black.Count(t => t == PieceType.Pawn);

        var id = stats.Materials.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i);
        long GamesAt(Material m) => id.TryGetValue(m, out int i) ? stats.Counts[i].Games : 0;
        long FirstAt(Material m) => id.TryGetValue(m, out int i) ? stats.Counts[i].FirstEntries : 0;
        // Each game: in our tables all the way, needing some of the missing pawn tables, or a table outside them.
        var missing = pawnTables.Where(m => !have.Contains(m)).ToHashSet();
        long inTables = 0, outsidePlan = 0, backInReach = 0;
        var needs = new List<Material[]>();
        foreach (var passed in stats.GameMaterials)
        {
            var need = passed.Select(i => stats.Materials[i]).Where(m => !have.Contains(m)).ToArray();
            if (need.Any(m => !missing.Contains(m)))
                outsidePlan++;
            else if (need.Length == 0)
                inTables++;
            else
                needs.Add(need);
            if (need.Length > 0 && have.Contains(stats.Materials[passed[^1]]))
                backInReach++;
        }
        var plan = EndingStats.BuildOrder(missing.OrderBy(m => m.ToString(), StringComparer.Ordinal).ToList(), needs);
        var step = plan.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i);
        var byGames = plan.OrderByDescending(GamesAt).ThenBy(m => m.ToString(), StringComparer.Ordinal)
            .Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i + 1);
        var readyAfter = new long[plan.Count];
        foreach (var need in needs)
            readyAfter[need.Max(m => step[m])]++;

        double Pct(long part, long whole) => whole == 0 ? 0 : 100.0 * part / whole;
        long reaching = stats.Reaching;
        long waiting = reaching - inTables - outsidePlan;
        Console.WriteLine($"{stats.Games:N0} games in {watch.Elapsed.TotalSeconds:0}s; {reaching:N0} ({Pct(reaching, stats.Games):0.0}%) " +
                          $"get down to {EndingStats.MaxPieces} pieces or fewer. Tables on disk: {have.Count} (in {TableDirectory}).");
        Console.WriteLine($"Of those {reaching:N0} games:");
        Console.WriteLine($"  every such position in a table we have     {inTables,9:N0}  ({Pct(inTables, reaching):0.0}%)");
        Console.WriteLine($"  through a 5-piece pawn table not built yet {waiting,9:N0}  ({Pct(waiting, reaching):0.0}%)");
        if (outsidePlan > 0)
            Console.WriteLine($"  through another table we don't have        {outsidePlan,9:N0}  ({Pct(outsidePlan, reaching):0.0}%)");
        Console.WriteLine($"  ...of the last two, back in our tables by the end  {backInReach,9:N0}  " +
                          $"({Pct(backInReach, waiting + outsidePlan):0.0}%)");
        Console.WriteLine($"  with castling rights still held at {EndingStats.MaxPieces} pieces or fewer (no table covers those positions): " +
                          $"{stats.WithCastling:N0}");
        Console.WriteLine();

        long totalSize = plan.Sum(EndgameTable.TableSize);
        Console.WriteLine($"The {plan.Count} pawn tables to build, in the order that brings games into the tables soonest per position " +
                          "solved (each after the tables its promotions lead to):");
        Console.WriteLine($"{"step",4} {"table",-8} {"pawns",5} {"positions",10} {"games",8} {"of all",7} {"first",7} {"rank",5}   " +
                          $"{"games in tables",15} {"work done",10}");
        long covered = inTables, size = 0;
        for (int k = 0; k < plan.Count; k++)
        {
            var material = plan[k];
            covered += readyAfter[k];
            size += EndgameTable.TableSize(material);
            Console.WriteLine($"{k + 1,4} {material,-8} {Pawns(material),5} {EndgameTable.TableSize(material) / 1e6,9:0}M " +
                              $"{GamesAt(material),8:N0} {Pct(GamesAt(material), stats.Games),6:0.00}% {FirstAt(material),7:N0} " +
                              $"{byGames[material],5}   {Pct(covered, reaching),14:0.0}% {Pct(size, totalSize),9:0.0}%");
        }
        Console.WriteLine("(games: reaching the table at all; first: arriving there first, their first position of " +
                          $"{EndingStats.MaxPieces} pieces or fewer; rank: by games; games in tables: share of the {reaching:N0} " +
                          "whose every such position is in a table once this one is built; work: positions solved so far, of the plan's)");

        // What else games spend time in: the most visited materials of all, for scale.
        Console.WriteLine();
        Console.WriteLine($"Most reached materials of {EndingStats.MaxPieces} pieces or fewer (all of them, for scale):");
        foreach (var (material, counts) in stats.Materials.Zip(stats.Counts).OrderByDescending(x => x.Second.Games).Take(15))
            Console.WriteLine($"  {material,-8} {counts.Games,8:N0} games  {Pct(counts.Games, stats.Games),5:0.0}%  " +
                              $"{counts.Plies / (double)counts.Games,5:0} plies each  {(have.Contains(material) ? "have" : "not yet")}");
        return 0;
    }

    private sealed record TakeoverRow(TakeoverPoint Point, Outcome Mate, Outcome Rule, OutcomeKind Actual);

    /// <summary>
    /// Matthew's early test (2026-10-01): where each game first enters a table
    /// we have (both kinds on disk), what the table says there at the game's own
    /// 50-move clock, against what actually happened.  With <paramref name="play"/>,
    /// that many of them (0: all) are played out by the engine on both sides.
    /// </summary>
    private static int GameTakeover(string[] files, int? play, (int Count, string Engine, int MoveMs)? versus = null)
    {
        var watch = Stopwatch.StartNew();
        // Both kinds on disk.  A 5-piece table counts once compressed: until then a build may be about
        // to replace its files, which Windows refuses while another process has them open.
        var covered = new HashSet<Material>();
        if (Directory.Exists(TableDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(TableDirectory, "*.cbt"))
            {
                string dtz = Path.ChangeExtension(path, ".cbz");
                Material material;
                try { material = Material.Parse(Path.GetFileNameWithoutExtension(path)).Canonical; }
                catch (FormatException) { continue; }
                if (File.Exists(dtz) && (material.PieceCount < 5
                                         || (EndgameTable.IsCompressedFile(path) && EndgameTable.IsCompressedFile(dtz))))
                    covered.Add(material);
            }
        }

        long games = 0;
        var points = new List<TakeoverPoint>();
        foreach (string file in files)
        {
            foreach (var game in GameFile.Read(file))
            {
                games++;
                if (Takeover.Find(game, covered.Contains) is { } point)
                    points.Add(point);
            }
        }

        // The tables' verdicts, a material at a time (its tables closed after, to keep memory down).
        var rows = new List<TakeoverRow>();
        foreach (var group in points.GroupBy(p => p.Material))
        {
            using var tables = new Tablebase(TableDirectory) { SolveMissing = false };
            foreach (var point in group)
            {
                if (!tables.TryProbe(point.Position, out var mate) || !tables.TryProbeWithClock(point.Position, out var rule))
                    continue;
                bool whiteToMove = point.Position.SideToMove == Colour.White;
                var actual = point.Game.Result switch
                {
                    "1-0" => whiteToMove ? OutcomeKind.Win : OutcomeKind.Loss,
                    "0-1" => whiteToMove ? OutcomeKind.Loss : OutcomeKind.Win,
                    "1/2-1/2" => OutcomeKind.Draw,
                    _ => OutcomeKind.Beyond,   // unfinished
                };
                rows.Add(new TakeoverRow(point, mate, rule, actual));
            }
        }

        double Pct(long part, long whole) => whole == 0 ? 0 : 100.0 * part / whole;
        static double Median(IEnumerable<int> values)
        {
            var sorted = values.Order().ToList();
            return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
        }
        Console.WriteLine($"{games:N0} games in {watch.Elapsed.TotalSeconds:0}s. Tables on disk with both kinds: {covered.Count}.");
        Console.WriteLine($"{rows.Count:N0} games ({Pct(rows.Count, games):0.0}%) enter a table we have, with a move still to make.");
        var left = rows.Select(r => r.Point.PliesLeft).Order().ToList();
        if (left.Count > 0)
            Console.WriteLine($"Still to play when they get there: median {left[left.Count / 2] / 2.0:0} moves " +
                              $"(quartiles {left[left.Count / 4] / 2.0:0} and {left[3 * left.Count / 4] / 2.0:0}; the game had ended there " +
                              $"in {left.Count(p => p == 0):N0})");
        Console.WriteLine();

        // The table's verdict for the side to move, by the rule at the game's clock, against what happened.
        string[] names = ["won", "drawn", "lost"];
        var kinds = new[] { OutcomeKind.Win, OutcomeKind.Draw, OutcomeKind.Loss };
        Console.WriteLine("The table (50-move rule, the game's clock) for the side to move, against the game's result:");
        Console.WriteLine($"  {"table says",-11} {"games",8}   {"won",14} {"drawn",14} {"lost",14} {"unfinished",11}");
        foreach (var (kind, name) in kinds.Zip(names))
        {
            var set = rows.Where(r => r.Rule.Kind == kind).ToList();
            string Cell(OutcomeKind actual) { long n = set.Count(r => r.Actual == actual); return $"{n:N0} ({Pct(n, set.Count):0.0}%)"; }
            Console.WriteLine($"  {name,-11} {set.Count,8:N0}   {Cell(OutcomeKind.Win),14} {Cell(OutcomeKind.Draw),14} {Cell(OutcomeKind.Loss),14} " +
                              $"{set.Count(r => r.Actual == OutcomeKind.Beyond),11:N0}");
        }
        var finished = rows.Where(r => r.Actual != OutcomeKind.Beyond).ToList();
        var changed = finished.Where(r => r.Actual != r.Rule.Kind).ToList();
        Console.WriteLine($"  Ended otherwise than the table says: {changed.Count:N0} of {finished.Count:N0} ({Pct(changed.Count, finished.Count):0.0}%): " +
                          "the results perfect play from there would have changed.");
        var wins = rows.Where(r => r.Rule.Kind != OutcomeKind.Draw).ToList();   // someone wins
        var thrown = wins.Where(r => r.Actual != OutcomeKind.Beyond && r.Actual != r.Rule.Kind).ToList();
        Console.WriteLine($"  Won endings not won: {thrown.Count:N0} of {wins.Count:N0}; of those, " +
                          $"{thrown.Count(r => r.Point.Game.Tag("Termination") == "Time forfeit"):N0} on time.");
        var cursed = rows.Where(r => r.Mate.Kind != OutcomeKind.Draw && r.Rule.Kind == OutcomeKind.Draw).ToList();
        Console.WriteLine($"  Won or lost with best play but drawn by the 50-move rule at the game's clock: {cursed.Count:N0}");
        var mated = rows.Where(r => r.Rule.Kind != OutcomeKind.Draw && r.Actual == r.Rule.Kind
                                    && r.Point.Game.PositionAt(r.Point.Game.Moves.Count).Status() == GameStatus.Checkmate).ToList();
        if (mated.Count > 0)
            Console.WriteLine($"  Won endings that ended in mate ({mated.Count:N0}): the players took a median {Median(mated.Select(r => r.Point.PliesLeft)) / 2:0.0} moves, " +
                              $"best play {Median(mated.Select(r => r.Mate.Plies)) / 2:0.0}; slower than best in " +
                              $"{Pct(mated.Count(r => r.Point.PliesLeft > r.Mate.Plies), mated.Count):0}% of them.");
        Console.WriteLine();

        Console.WriteLine("Where they enter, the most common tables:");
        Console.WriteLine($"  {"table",-8} {"games",7}  {"won for someone",16}  {"...and won",11}  {"drawn",7}  {"...and drawn",13}");
        foreach (var group in rows.GroupBy(r => r.Point.Material).OrderByDescending(g => g.Count()).Take(15))
        {
            var won = group.Where(r => r.Rule.Kind != OutcomeKind.Draw && r.Actual != OutcomeKind.Beyond).ToList();
            var drawn = group.Where(r => r.Rule.Kind == OutcomeKind.Draw && r.Actual != OutcomeKind.Beyond).ToList();
            Console.WriteLine($"  {group.Key,-8} {group.Count(),7:N0}  {won.Count,16:N0}  {Pct(won.Count(r => r.Actual == r.Rule.Kind), won.Count),10:0.0}%  " +
                              $"{drawn.Count,7:N0}  {Pct(drawn.Count(r => r.Actual == OutcomeKind.Draw), drawn.Count),12:0.0}%");
        }

        if (play is int count)
            PlayTakeovers(rows, count, Pct);
        if (versus is { } match)
            VersusTakeovers(rows, match.Count, match.Engine, match.MoveMs, Pct);
        return 0;
    }

    /// <summary>
    /// Takeover positions against another engine: each played twice, our engine
    /// (in this process, with every table) on the side to move, then on the
    /// other side; set against what the table says each side should get.
    /// </summary>
    private static void VersusTakeovers(List<TakeoverRow> rows, int count, string command, int moveMs,
                                        Func<long, long, double> Pct)
    {
        var chosen = count <= 0 || count >= rows.Count
            ? rows
            : Enumerable.Range(0, count).Select(i => rows[(int)((long)i * rows.Count / count)]).ToList();
        var clock = TimeControl.Parse($"movetime={moveMs}");
        var watch = Stopwatch.StartNew();
        // [table verdict for that side, result for that side], for us and for them.
        var ours = new long[3, 3];
        var theirs = new long[3, 3];
        static int Index(OutcomeKind kind) => kind switch { OutcomeKind.Win => 0, OutcomeKind.Draw => 1, _ => 2 };
        static OutcomeKind Flip(OutcomeKind kind) => kind switch
        {
            OutcomeKind.Win => OutcomeKind.Loss,
            OutcomeKind.Loss => OutcomeKind.Win,
            _ => kind,
        };
        var endings = new Dictionary<string, int>();
        var surprises = new List<string>();
        int played = 0;
        using var opponent = new UciPlayer(EngineSpec.Parse(["name=opponent", $"cmd={command}"]));
        foreach (var group in chosen.GroupBy(r => r.Point.Material))
        {
            using var tables = new Tablebase(TableDirectory) { SolveMissing = false };
            using var us = new SearchPlayer("ours", tables);
            foreach (var row in group)
            {
                string fen = row.Point.Position.ToFen();
                bool whiteToMove = row.Point.Position.SideToMove == Colour.White;
                foreach (bool weMoveFirst in new[] { true, false })
                {
                    bool weAreWhite = weMoveFirst == whiteToMove;
                    var record = GamePlayer.Play(weAreWhite ? us : opponent, weAreWhite ? opponent : us,
                                                 Array.Empty<string>(), clock, ++played, null, fen);
                    var result = record.Result switch
                    {
                        GameResult.Draw => OutcomeKind.Draw,
                        GameResult.WhiteWins => weAreWhite ? OutcomeKind.Win : OutcomeKind.Loss,
                        _ => weAreWhite ? OutcomeKind.Loss : OutcomeKind.Win,
                    };
                    var verdict = weMoveFirst ? row.Rule.Kind : Flip(row.Rule.Kind);   // the table, for our side
                    ours[Index(verdict), Index(result)]++;
                    theirs[Index(Flip(verdict)), Index(Flip(result))]++;
                    string end = record.Termination.StartsWith("tablebase") ? "tablebase" : record.Termination;
                    endings[end] = endings.GetValueOrDefault(end) + 1;
                    if (Index(result) > Index(verdict) && surprises.Count < 10)   // we did worse than the table
                        surprises.Add($"{fen}: we had {verdict} as {(weAreWhite ? "white" : "black")}, got {result} " +
                                      $"({record.Termination}, {record.Moves.Count} plies)");
                    if (played % 50 == 0)
                        Console.Error.Write($"\rplayed {played:N0} of {chosen.Count * 2:N0}");
                }
            }
        }
        Console.Error.Write($"\r{"",-40}\r");

        Console.WriteLine();
        Console.WriteLine($"Against {command}, {moveMs} ms a move: {chosen.Count:N0} positions, each played twice " +
                          $"(our engine on each side), {played:N0} games in {watch.Elapsed.TotalMinutes:0.0} min.");
        string[] names = ["won", "drawn", "lost"];
        foreach (var (title, matrix) in new[] { ("Our engine (every table)", ours), ("The opponent (no tables)", theirs) })
        {
            Console.WriteLine($"  {title}: what the table says for its side, against what it got");
            Console.WriteLine($"    {"table says",-11} {"games",7}   {"won",14} {"drawn",14} {"lost",14}");
            for (int v = 0; v < 3; v++)
            {
                long total = matrix[v, 0] + matrix[v, 1] + matrix[v, 2];
                string Cell(int r) => $"{matrix[v, r]:N0} ({Pct(matrix[v, r], total):0.0}%)";
                Console.WriteLine($"    {names[v],-11} {total,7:N0}   {Cell(0),14} {Cell(1),14} {Cell(2),14}");
            }
        }
        double Points(long[,] m) => Enumerable.Range(0, 3).Sum(v => m[v, 0] + 0.5 * m[v, 1]);
        double Expected(long[,] m) => Enumerable.Range(0, 3).Sum(v => (m[v, 0] + m[v, 1] + m[v, 2]) * (v == 0 ? 1 : v == 1 ? 0.5 : 0));
        Console.WriteLine($"  Points: ours {Points(ours):N1} (the table's {Expected(ours):N1}), theirs {Points(theirs):N1} " +
                          $"(the table's {Expected(theirs):N1}).");
        Console.WriteLine($"  How they ended: " + string.Join(", ", endings.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:N0}")));
        foreach (string surprise in surprises)
            Console.WriteLine($"  WE DID WORSE THAN THE TABLE: {surprise}");
    }

    /// <summary>Play takeover positions out with the engine on both sides, and set the results against the tables' verdicts and the players'.</summary>
    private static void PlayTakeovers(List<TakeoverRow> rows, int count, Func<long, long, double> Pct)
    {
        var chosen = count <= 0 || count >= rows.Count
            ? rows
            : Enumerable.Range(0, count).Select(i => rows[(int)((long)i * rows.Count / count)]).ToList();
        var watch = Stopwatch.StartNew();
        long agree = 0, beatPlayers = 0, decisive = 0, faster = 0;
        var byEnd = new Dictionary<string, int>();
        var surprises = new List<string>();
        var pliesOurs = new List<int>();
        var pliesTheirs = new List<int>();
        int done = 0;
        foreach (var group in chosen.GroupBy(r => r.Point.Material))
        {
            using var tables = new Tablebase(TableDirectory) { SolveMissing = false };
            foreach (var row in group)
            {
                var (result, termination, plies) = Takeover.PlayOut(row.Point.Position, row.Point.Hashes, tables);
                bool whiteToMove = row.Point.Position.SideToMove == Colour.White;
                var ours = result switch
                {
                    GameResult.Draw => OutcomeKind.Draw,
                    GameResult.WhiteWins => whiteToMove ? OutcomeKind.Win : OutcomeKind.Loss,
                    _ => whiteToMove ? OutcomeKind.Loss : OutcomeKind.Win,
                };
                string end = termination.StartsWith("tablebase") ? "tablebase" : termination;
                byEnd[end] = byEnd.GetValueOrDefault(end) + 1;
                if (ours == row.Rule.Kind)
                    agree++;
                else if (surprises.Count < 10)
                    surprises.Add($"{row.Point.Position.ToFen()}: table {row.Rule}, played out {ours} ({termination}, {plies} plies)");
                if (ours != row.Actual && row.Actual != OutcomeKind.Beyond && ours == row.Rule.Kind)
                    beatPlayers++;
                if (ours != OutcomeKind.Draw && row.Actual == ours)
                {
                    decisive++;
                    pliesOurs.Add(plies);
                    pliesTheirs.Add(row.Point.PliesLeft);
                    if (plies < row.Point.PliesLeft)
                        faster++;
                }
                if (++done % 500 == 0)
                    Console.Error.Write($"\rplayed {done:N0} of {chosen.Count:N0}");
            }
        }
        Console.Error.Write($"\r{"",-40}\r");
        Console.WriteLine();
        Console.WriteLine($"Played out with the engine on both sides: {chosen.Count:N0} positions in {watch.Elapsed.TotalSeconds:0}s " +
                          $"({watch.Elapsed.TotalMilliseconds / Math.Max(1, chosen.Count):0} ms each).");
        Console.WriteLine($"  ended as the table said: {agree:N0} ({Pct(agree, chosen.Count):0.0}%)");
        Console.WriteLine($"  how they ended: " + string.Join(", ", byEnd.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:N0}")));
        Console.WriteLine($"  results different from the players', as the table said: {beatPlayers:N0} ({Pct(beatPlayers, chosen.Count):0.0}%)");
        if (decisive > 0)
            Console.WriteLine($"  won both ways ({decisive:N0}): the engine took a median {pliesOurs.Order().ElementAt(pliesOurs.Count / 2) / 2.0:0} moves, " +
                              $"the players {pliesTheirs.Order().ElementAt(pliesTheirs.Count / 2) / 2.0:0} (they often resigned sooner); " +
                              $"engine quicker in {Pct(faster, decisive):0}%");
        foreach (string surprise in surprises)
            Console.WriteLine($"  NOT AS THE TABLE SAID: {surprise}");
    }

    private static PackedBoard ParseBoard(string input)
    {
        input = input.Trim();
        if (input.Length == PackedBoard.ByteLength * 2 && input.All(Uri.IsHexDigit))
            return PackedBoard.FromHex(input);
        return Fen.Parse(input);
    }

    private static IEnumerable<string> ReadLines(string path) =>
        File.ReadLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));

    private static void Report(CheckResult result)
    {
        if (result.IsValid)
        {
            Console.WriteLine($"  ok: passes every tier (white {result.White}, black {result.Black})");
            return;
        }
        int failed = result.HighestTierPassed + 1;
        Console.WriteLine($"  impossible: fails tier {failed} ({Tiers.Names[failed]})");
        foreach (string problem in result.Problems)
            Console.WriteLine($"    - {problem}");
    }

    private static void PrintTally(long[] tiers)
    {
        long total = tiers.Sum();
        for (int tier = 0; tier < tiers.Length; tier++)
        {
            string label = tier == Tiers.Material ? "passes everything" : $"fails tier {tier + 1}";
            Console.WriteLine($"  {label,-20} {tiers[tier],12:N0}  ({100.0 * tiers[tier] / Math.Max(1, total):0.00}%)");
        }
    }

    private static string Describe(BigInteger n) =>
        n.IsZero ? "0" : $"~{Scientific(n)}  (~2^{BigInteger.Log(n, 2):0.0})";

    private static string Scientific(BigInteger n)
    {
        double log10 = BigInteger.Log10(n);
        double exponent = Math.Floor(log10);
        return $"{Math.Pow(10, log10 - exponent):0.000}e{exponent}";
    }

    private static string Ratio(BigInteger larger, BigInteger smaller)
    {
        if (smaller.IsZero)
            return "infinity";
        double ratio = Math.Pow(10, BigInteger.Log10(larger) - BigInteger.Log10(smaller));
        return ratio < 1e6 ? $"~{ratio:N0}" : $"~{ratio:0.###e0}";
    }

    private static int Print(string text, int code = 0)
    {
        Console.WriteLine(text);
        return code;
    }
}
