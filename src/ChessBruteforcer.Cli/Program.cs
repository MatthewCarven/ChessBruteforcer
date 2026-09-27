using System.Diagnostics;
using System.Numerics;
using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Game;
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

        Endgame tables (up to 4 pieces; saved in ./tables, reused next time):

          solve <material>               solve e.g. KQvK or KRvK and print what it found
          probe <fen>                    the outcome, and every move ranked best first
          line <fen>                     best play from here to mate
          verify <material> [stride]     check every (or every n-th) position against its moves

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
                ["solve", var material] => Solve(material),
                ["probe", var fen] => Probe(fen),
                ["line", var fen] => Line(fen),
                ["verify", var material] => Verify(material, 1),
                ["verify", var material, var stride] => Verify(material, int.Parse(stride)),
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

    private const string TableDirectory = "tables";

    private static Tablebase OpenTablebase() =>
        new(TableDirectory, new Progress<string>(message => Console.Error.Write($"\r{message,-70}")));

    private static int Solve(string text)
    {
        var material = Material.Parse(text);
        var stopwatch = Stopwatch.StartNew();
        var table = OpenTablebase().Get(material);
        Console.Error.Write($"\r{"",-70}\r");
        var stats = table.Statistics();

        Console.WriteLine($"{table.Material}: {table.Size:N0} indexed slots ({table.Size * 2 / 1024.0 / 1024.0:0.0} MB), " +
                          $"ready in {stopwatch.Elapsed.TotalSeconds:0.0}s");
        foreach (var side in new[] { Colour.White, Colour.Black })
        {
            int s = (int)side;
            long legal = stats.Legal(side);
            Console.WriteLine($"  {side} to move: {legal:N0} legal positions");
            Console.WriteLine($"    wins   {stats.Wins[s],10:N0}  ({100.0 * stats.Wins[s] / legal:0.0}%)");
            Console.WriteLine($"    draws  {stats.Draws[s],10:N0}  ({100.0 * stats.Draws[s] / legal:0.0}%)");
            Console.WriteLine($"    losses {stats.Losses[s],10:N0}  ({100.0 * stats.Losses[s] / legal:0.0}%)");
            if (stats.Longest[s] is var (outcome, index))
                Console.WriteLine($"    longest: {outcome}, e.g. {table.PositionAt(index).ToFen()}");
        }
        return 0;
    }

    private static int Verify(string text, int stride)
    {
        var material = Material.Parse(text);
        var stopwatch = Stopwatch.StartNew();
        var (checkedPositions, mismatches) = OpenTablebase().Verify(material, stride,
            new Progress<string>(message => Console.Error.Write($"\r{message,-70}")));
        Console.Error.Write($"\r{"",-70}\r");
        foreach (string mismatch in mismatches)
            Console.WriteLine($"  MISMATCH {mismatch}");
        Console.WriteLine($"{material.Canonical}: {checkedPositions:N0} positions checked in " +
                          $"{stopwatch.Elapsed.TotalSeconds:0.0}s, {(mismatches.Count == 0 ? "all consistent" : $"{mismatches.Count}+ mismatches")}");
        return mismatches.Count == 0 ? 0 : 2;
    }

    private static int Probe(string fen)
    {
        var position = Position.FromFen(fen);
        var tablebase = OpenTablebase();
        var outcome = tablebase.Probe(position);
        var ranked = tablebase.RankMoves(position);
        Console.Error.Write($"\r{"",-70}\r");
        Console.WriteLine(position.ToPackedBoard().ToDiagram());
        Console.WriteLine($"{position.SideToMove} to move: {outcome}");
        foreach (var (move, result) in ranked)
            Console.WriteLine($"  {move.ToUci(),-6} {result}");
        return 0;
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
