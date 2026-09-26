using System.Diagnostics;
using System.Numerics;
using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Game;
using ChessBruteforcer.Core.Possibility;

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

          file add <file> <board>...     append boards to a board file (.cbb)
          file import <file> <text>      append every FEN / hex line of a text file
          file list <file>               print each board as FEN (or hex if meaningless)
          file export <file> <text>      write each board as a FEN line to a text file
          file check <file>              check every board, tallying the tier each reached
          file dedupe <file> [out]       sort and drop duplicate boards (in place by default)
          file count <file>              number of boards in the file
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
                ["file", "add", var file, .. var boards] when boards.Length > 0 => FileAdd(file, boards),
                ["file", "import", var file, var text] => FileAdd(file, ReadLines(text)),
                ["file", "list", var file] => FileList(file),
                ["file", "export", var file, var text] => FileExport(file, text),
                ["file", "check", var file] => FileCheck(file),
                ["file", "dedupe", var file] => FileDedupe(file, null),
                ["file", "dedupe", var file, var output] => FileDedupe(file, output),
                ["file", "count", var file] => Print($"{BoardFile.Count(file):N0} boards"),
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
