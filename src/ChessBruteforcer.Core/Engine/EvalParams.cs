using System.Globalization;
using System.Text;

namespace ChessBruteforcer.Core.Engine;

/// <summary>
/// The evaluation's numbers (Texel tuning, 2026-10-14): for each piece type (1 pawn .. 6 king) and square, a
/// middlegame and an endgame value, piece value included, in centipawns, as seen from white with rank 8
/// first (the layout the tables have always been written in); and the bishop pair's bonus, both ways. The
/// evaluation blends middlegame and endgame by the game phase. As a flat vector for the tuner: the
/// middlegame tables (pawn to king, 64 each), then the endgame ones, then the bishop pair's two.
/// </summary>
public sealed class EvalParams
{
    public const int Count = 2 * 6 * 64 + 2;

    private static readonly string[] Names = { "", "pawn", "knight", "bishop", "rook", "queen", "king" };

    public int[][] Mg { get; } = Enumerable.Range(0, 7).Select(_ => new int[64]).ToArray();

    public int[][] Eg { get; } = Enumerable.Range(0, 7).Select(_ => new int[64]).ToArray();

    public int BishopPairMg { get; set; }

    public int BishopPairEg { get; set; }

    /// <summary>The evaluation's original numbers (Michniewski's tables; only the king differs by phase).</summary>
    public static EvalParams Original()
    {
        var p = new EvalParams();
        Evaluation.FillOriginal(p);
        return p;
    }

    public static int Index(bool middlegame, int type, int square) => (middlegame ? 0 : 6 * 64) + (type - 1) * 64 + square;

    public const int BishopPairMgIndex = 2 * 6 * 64, BishopPairEgIndex = 2 * 6 * 64 + 1;

    public double[] ToVector()
    {
        var v = new double[Count];
        for (int type = 1; type <= 6; type++)
            for (int i = 0; i < 64; i++)
            {
                v[Index(true, type, i)] = Mg[type][i];
                v[Index(false, type, i)] = Eg[type][i];
            }
        v[BishopPairMgIndex] = BishopPairMg;
        v[BishopPairEgIndex] = BishopPairEg;
        return v;
    }

    /// <summary>From a flat vector, rounded to whole centipawns.</summary>
    public static EvalParams FromVector(double[] v)
    {
        var p = new EvalParams();
        for (int type = 1; type <= 6; type++)
            for (int i = 0; i < 64; i++)
            {
                p.Mg[type][i] = (int)Math.Round(v[Index(true, type, i)]);
                p.Eg[type][i] = (int)Math.Round(v[Index(false, type, i)]);
            }
        p.BishopPairMg = (int)Math.Round(v[BishopPairMgIndex]);
        p.BishopPairEg = (int)Math.Round(v[BishopPairEgIndex]);
        return p;
    }

    /// <summary>
    /// As text: "bishop-pair mg eg", then per table a line "pawn mg" (or "king eg", ...) and its 64 values,
    /// eight to a line, rank 8 first, as seen from white. Lines from # on are comments.
    /// </summary>
    public void Save(string path, string? comment = null)
    {
        var text = new StringBuilder();
        text.AppendLine("# ChessBruteforcer evaluation numbers, centipawns, as seen from white, rank 8 first (piece value included).");
        if (comment is not null)
            foreach (string line in comment.Split('\n'))
                text.AppendLine("# " + line.TrimEnd());
        text.AppendLine($"bishop-pair {BishopPairMg} {BishopPairEg}");
        for (int type = 1; type <= 6; type++)
            foreach (var (name, table) in new[] { ("mg", Mg[type]), ("eg", Eg[type]) })
            {
                text.AppendLine($"{Names[type]} {name}");
                for (int rank = 0; rank < 8; rank++)
                    text.AppendLine(string.Join(" ", table.Skip(rank * 8).Take(8).Select(x => x.ToString(CultureInfo.InvariantCulture).PadLeft(5))));
            }
        File.WriteAllText(path, text.ToString());
    }

    public static EvalParams Load(string path)
    {
        var p = new EvalParams();
        var tokens = File.ReadLines(path)
            .Select(line => line.Split('#')[0])
            .SelectMany(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        var seen = new HashSet<string>();
        for (int i = 0; i < tokens.Count;)
        {
            if (tokens[i] == "bishop-pair")
            {
                p.BishopPairMg = int.Parse(tokens[i + 1], CultureInfo.InvariantCulture);
                p.BishopPairEg = int.Parse(tokens[i + 2], CultureInfo.InvariantCulture);
                seen.Add("bishop-pair");
                i += 3;
                continue;
            }
            int type = Array.IndexOf(Names, tokens[i]);
            if (type < 1 || i + 66 > tokens.Count || tokens[i + 1] is not ("mg" or "eg"))
                throw new FormatException($"{path}: expected a table name (pawn .. king) and mg or eg at \"{tokens[i]}\"");
            var table = tokens[i + 1] == "mg" ? p.Mg[type] : p.Eg[type];
            for (int k = 0; k < 64; k++)
                table[k] = int.Parse(tokens[i + 2 + k], CultureInfo.InvariantCulture);
            seen.Add(tokens[i] + " " + tokens[i + 1]);
            i += 66;
        }
        if (seen.Count != 13)
            throw new FormatException($"{path}: {seen.Count} of the 13 parts (12 tables and the bishop pair)");
        return p;
    }
}
