using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Game;
using ChessBruteforcer.Core.Records;

namespace ChessBruteforcer.Tests;

/// <summary>
/// The network's training data (NNUE.md, session 1, 2026-10-16): the quiet rule both commands share, the
/// 32-byte record, and `nnue data` itself, set against `tune positions` on the same games.
/// </summary>
public class NnueDataTests
{
    /// <summary>Random play from the start (seeded), up to <paramref name="maxPlies"/> plies or the game's end.</summary>
    internal static List<Move> RandomGame(Random rng, Position position, int maxPlies)
    {
        var moves = new List<Move>();
        for (int ply = 0; ply < maxPlies; ply++)
        {
            var legal = MoveGenerator.Legal(position);
            if (legal.Count == 0)
                break;
            var move = legal[rng.Next(legal.Count)];
            moves.Add(move);
            position.MakeMove(move);
        }
        return moves;
    }

    private static int Pieces(Position position) => Enumerable.Range(0, 64).Count(s => !position[s].IsEmpty);

    [Fact]
    public void TheQuietRuleIsTheTunersInWords()
    {
        // The rule restated from its words (after move 8, more than 5 pieces, not in check, neither the move
        // before nor the move played a capture or a promotion), pieces counted on the board rather than by
        // captures, and set against the shared walk over long random games. Each condition must be the only
        // one failing somewhere, and both edges (ply 16, 6 pieces) must be met, or a change to it could hide.
        var rng = new Random(20261016);
        var alone = new int[6];   // positions failing only: ply, pieces, move before, capture, promotion, check
        int atPly16 = 0, atSixPieces = 0, quietCount = 0;
        for (int g = 0; g < 600; g++)
        {
            var moves = RandomGame(rng, Position.Start(), 400);
            var game = new StoredGame(new List<(string, string)>(), moves, "*");
            var walked = new List<(int, string)>();
            QuietPositions.Walk(game, (ply, position) => walked.Add((ply, position.ToFen())));
            var expected = new List<(int, string)>();
            var replay = Position.Start();
            for (int ply = 0; ply < moves.Count; ply++)
            {
                var previous = ply > 0 ? moves[ply - 1] : default;
                bool[] fails =
                {
                    ply < 16,
                    Pieces(replay) <= 5,
                    previous.IsCapture || previous.IsPromotion,
                    moves[ply].IsCapture,
                    moves[ply].IsPromotion,
                    replay.InCheck(),
                };
                int failing = fails.Count(f => f);
                if (failing == 0)
                {
                    expected.Add((ply, replay.ToFen()));
                    atPly16 += ply == 16 ? 1 : 0;
                    atSixPieces += Pieces(replay) == 6 ? 1 : 0;
                }
                else if (failing == 1)
                    alone[Array.IndexOf(fails, true)]++;
                replay.MakeMove(moves[ply]);
            }
            Assert.Equal(expected, walked);
            quietCount += expected.Count;
        }
        Assert.All(alone, n => Assert.True(n > 0, $"each condition the only one failing somewhere: {string.Join(", ", alone)}"));
        Assert.True(atPly16 > 0 && atSixPieces > 0, $"quiet at ply 16: {atPly16}, with exactly 6 pieces: {atSixPieces}");
        Assert.True(quietCount > 10_000, $"{quietCount} quiet positions");
    }

    [Fact]
    public void TenThousandPositionsSurviveTheRecord()
    {
        // FEN -> record -> FEN. Castling rights and the en passant square aren't stored (the network doesn't
        // see them), so the record's FEN always says "- -", and only placement and side to move must match.
        var rng = new Random(16102026);
        int withCastling = 0, withEnPassant = 0, promoted = 0, checkedCount = 0;
        var record = new byte[NnueRecord.Size];
        var positions = new List<(string Fen, bool AfterPromotion)>();   // every 5th ply of random games, from the start on
        while (positions.Count < 10_000)
        {
            var walk = Position.Start();
            bool afterPromotion = false;
            for (int ply = 0; ply < 300 && MoveGenerator.HasLegalMove(walk); ply++)
            {
                if (ply % 5 == 0)
                    positions.Add((walk.ToFen(), afterPromotion));
                var legal = MoveGenerator.Legal(walk);
                var move = legal[rng.Next(legal.Count)];
                afterPromotion |= move.IsPromotion;
                walk.MakeMove(move);
            }
        }
        foreach (var (fen, afterPromotion) in positions.Take(10_000))
        {
            var position = Position.FromFen(fen);
            string[] fields = fen.Split(' ');
            withCastling += fields[2] != "-" ? 1 : 0;
            withEnPassant += fields[3] != "-" ? 1 : 0;
            promoted += afterPromotion ? 1 : 0;

            short score = (short)rng.Next(-NnueRecord.MaxScore, NnueRecord.MaxScore + 1);
            bool mate = rng.Next(10) == 0;
            if (mate)
                score = rng.Next(2) == 0 ? NnueRecord.MateScore : (short)-NnueRecord.MateScore;
            byte result = (byte)rng.Next(3);
            uint game = checkedCount == 0 ? uint.MaxValue : (uint)rng.NextInt64(0, (long)uint.MaxValue + 1);
            Array.Fill(record, (byte)0xAB);   // Write must set every byte
            NnueRecord.Write(record, Position.FromFen(fen), score, mate, result, game);

            string back = NnueRecord.ToFen(record);
            Assert.Equal(string.Join(' ', fields[..2]), string.Join(' ', back.Split(' ')[..2]));
            Assert.EndsWith(" - - 0 1", back);
            var decoded = NnueRecord.Read(record);
            for (int square = 0; square < 64; square++)
                Assert.Equal(position[square], decoded.Squares[square]);
            Assert.Equal((position.SideToMove, score, mate, result, game),
                         (decoded.SideToMove, decoded.WhiteScore, decoded.Mate, decoded.Result, decoded.Game));
            checkedCount++;
        }
        Assert.Equal(10_000, checkedCount);
        Assert.True(withCastling > 100 && withEnPassant > 10 && promoted > 100,
                    $"castling {withCastling}, en passant {withEnPassant}, after a promotion {promoted}: the cases worth having");
    }

    [Fact]
    public void TheLayoutIsTheOneTheTrainerReads()
    {
        // The start position, black to move, by hand: what records.py and the trainer assume, byte for byte.
        var record = new byte[NnueRecord.Size];
        var position = Position.FromFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR b KQkq - 0 1");
        NnueRecord.Write(record, position, -123, false, 1, 0x01020304);
        Assert.Equal(0xFFFF_0000_0000_FFFFUL, BinaryPrimitives.ReadUInt64LittleEndian(record));
        // a1 rook (4) low, b1 knight (2) high; ... e1 king (6), f1 bishop (3); then white pawns; black's pieces.
        Assert.Equal(new byte[] { 0x24, 0x53, 0x36, 0x42, 0x11, 0x11, 0x11, 0x11, 0x99, 0x99, 0x99, 0x99, 0xAC, 0xDB, 0xBE, 0xCA },
                     record[8..24]);
        Assert.Equal(NnueRecord.BlackToMoveFlag, record[24]);
        Assert.Equal(1, record[25]);
        Assert.Equal(-123, BinaryPrimitives.ReadInt16LittleEndian(record.AsSpan(26)));
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, record[28..32]);
    }

    [Theory]
    [InlineData("152", true, 152, false, false)]
    [InlineData("-37", true, -37, false, false)]
    [InlineData("0", true, 0, false, false)]
    [InlineData("M3", true, 32000, true, false)]
    [InlineData("M-1", true, -32000, true, false)]
    [InlineData("40000", true, 31999, false, true)]
    [InlineData("-40000", true, -31999, false, true)]
    [InlineData("31999", true, 31999, false, false)]
    [InlineData("?", false, 0, false, false)]
    [InlineData("M0", false, 0, false, false)]
    [InlineData("", false, 0, false, false)]
    public void ScoresReadAsEvalGamesWritesThem(string text, bool ok, int score, bool mate, bool clamped)
    {
        Assert.Equal((ok, score, mate, clamped),
                     (NnueRecord.TryParseScore(text, out short s, out bool m, out bool c), (int)s, m, c));
    }

    [Fact]
    public void AScoreAndItsMateFlagMustAgree()
    {
        var record = new byte[NnueRecord.Size];
        Assert.Throws<ArgumentException>(() => NnueRecord.Write(record, Position.Start(), 32000, false, 1, 1));
        Assert.Throws<ArgumentException>(() => NnueRecord.Write(record, Position.Start(), 500, true, 1, 1));
        Assert.Throws<ArgumentException>(() => NnueRecord.Write(record, Position.Start(), 0, false, 3, 1));
        Assert.Equal(new byte?[] { 2, 1, 0, null }, new[] { "1-0", "1/2-1/2", "0-1", "*" }.Select(NnueRecord.ResultCode));
    }

    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ChessBruteforcer.sln")))
                return dir.FullName;
        throw new DirectoryNotFoundException("No ChessBruteforcer.sln above the test's folder.");
    }

    [Fact]
    public void ThePythonReadersFixtureIsWhatCSharpWrites()
    {
        // scripts/nnue/test-records.txt lists positions and labels as `nnue show` prints them; .bin is C#'s
        // records of them. scripts/nnue/test-records.py checks that records.py reads the .bin back to the .txt,
        // so together the two say the Python reader decodes C#'s records to the same pieces. Missing, the two
        // files are written here once (commit them).
        string dir = Path.Combine(RepoRoot(), "scripts", "nnue");
        string text = Path.Combine(dir, "test-records.txt"), bin = Path.Combine(dir, "test-records.bin");
        if (!File.Exists(text))
        {
            var rng = new Random(1016);
            var lines = new List<string>();
            var record = new byte[NnueRecord.Size];
            while (lines.Count < 1000)
            {
                var position = Position.Start();
                RandomGame(rng, position, rng.Next(0, 250));
                bool mate = rng.Next(8) == 0;
                short score = mate ? (rng.Next(2) == 0 ? NnueRecord.MateScore : (short)-NnueRecord.MateScore)
                                   : (short)rng.Next(-NnueRecord.MaxScore, NnueRecord.MaxScore + 1);
                NnueRecord.Write(record, position, score, mate, (byte)rng.Next(3), (uint)rng.NextInt64(0, (long)uint.MaxValue + 1));
                var r = NnueRecord.Read(record);
                lines.Add($"{NnueRecord.ToFen(record)}\t{r.WhiteScore}\t{(r.Mate ? 1 : 0)}\t{r.Result}\t{r.Game}");
            }
            File.WriteAllText(text, string.Join("\n", lines) + "\n");
            File.WriteAllBytes(bin, Encode(lines));
            Assert.Fail($"Wrote {text} and {bin}: commit them, then run again.");
        }
        var expected = File.ReadAllText(text).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(1000, expected.Length);
        Assert.Equal(File.ReadAllBytes(bin), Encode(expected));
    }

    private static byte[] Encode(IReadOnlyList<string> lines)
    {
        var bytes = new byte[lines.Count * NnueRecord.Size];
        for (int i = 0; i < lines.Count; i++)
        {
            string[] f = lines[i].Split('\t');
            NnueRecord.Write(bytes.AsSpan(i * NnueRecord.Size, NnueRecord.Size), Position.FromFen(f[0]), short.Parse(f[1]),
                             f[2] == "1", byte.Parse(f[3]), uint.Parse(f[4], CultureInfo.InvariantCulture));
        }
        return bytes;
    }

    /// <summary>
    /// Games as scripts/eval-games.py leaves them: Evals tags of centipawns, mates and gaps over random play,
    /// and the cases both commands must leave out (casual games, a FEN start, no Evals, no rating, no result),
    /// at every speed.
    /// </summary>
    internal static List<StoredGame> AnalysedGames(int count, int seed)
    {
        var rng = new Random(seed);
        // ultrabullet, bullet, blitz, blitz (300 + 40 x 3 = 420 s), rapid, classical, none
        string[] controls = { "15+0", "60+0", "180+0", "300+3", "600+5", "1800+0", "-" };
        string[] results = { "1-0", "0-1", "1/2-1/2" };
        const string fenStart = "r1bqkbnr/pppp1ppp/2n5/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 3";
        var games = new List<StoredGame>();
        for (int g = 1; g <= count; g++)
        {
            bool fromFen = g % 41 == 0;
            var moves = RandomGame(rng, Position.FromFen(fromFen ? fenStart : Fen.StartPosition), rng.Next(10, 220));
            string Eval() => rng.Next(40) switch
            {
                0 or 1 => "?",
                2 => $"M{rng.Next(1, 12)}",
                3 => $"M-{rng.Next(1, 12)}",
                4 => rng.Next(2) == 0 ? "40000" : "-40000",
                _ => rng.Next(-1500, 1501).ToString(CultureInfo.InvariantCulture),
            };
            var tags = new List<(string, string)>
            {
                ("Event", g % 23 == 0 ? "Casual Blitz game" : "Rated Blitz game"),
                ("Site", $"https://lichess.org/test{g}"),
                ("Result", g % 29 == 0 ? "*" : results[rng.Next(3)]),
                ("WhiteElo", g % 31 == 0 ? "?" : rng.Next(800, 2900).ToString(CultureInfo.InvariantCulture)),
                ("BlackElo", rng.Next(800, 2900).ToString(CultureInfo.InvariantCulture)),
                ("TimeControl", controls[rng.Next(controls.Length)]),
                ("Termination", rng.Next(4) == 0 ? "Time forfeit" : "Normal"),
            };
            if (fromFen)
                tags.Add(("FEN", fenStart));
            if (g % 37 != 0)
                tags.Add(("Evals", string.Join(' ', moves.Select(_ => Eval()))));
            games.Add(new StoredGame(tags, moves, tags[2].Item2));
        }
        return games;
    }

    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } = Directory.CreateTempSubdirectory("nnue-data-test-").FullName;
        public string this[string name] => Path.Combine(Dir, name);
        public void Dispose() => Directory.Delete(Dir, recursive: true);
    }

    private static List<byte[]> ReadRecords(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0, bytes.Length % NnueRecord.Size);
        return Enumerable.Range(0, bytes.Length / NnueRecord.Size).Select(i => bytes[(i * NnueRecord.Size)..((i + 1) * NnueRecord.Size)]).ToList();
    }

    private static (JsonElement Meta, List<byte[]> Training, List<byte[]> Held) RunNnueData(string outDir, params string[] args)
    {
        Assert.Equal(0, Cli.Run(["nnue", "data", outDir, .. args]));
        var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "data.json"))).RootElement.Clone();
        var training = new List<byte[]>();
        var files = meta.GetProperty("bucket_files").EnumerateArray().Select(f => f.GetString()!).ToList();
        var counts = meta.GetProperty("bucket_counts").EnumerateArray().Select(c => c.GetInt64()).ToList();
        for (int b = 0; b < files.Count; b++)
        {
            var records = ReadRecords(Path.Combine(outDir, files[b]));
            Assert.Equal(counts[b], records.Count);
            training.AddRange(records);
        }
        var held = ReadRecords(Path.Combine(outDir, meta.GetProperty("held_back").GetProperty("file").GetString()!));
        var c = meta.GetProperty("counts");
        Assert.Equal(c.GetProperty("training").GetInt64(), training.Count);
        Assert.Equal(c.GetProperty("held_back").GetInt64(), held.Count);
        Assert.Equal(c.GetProperty("positions").GetInt64(), training.Count + held.Count);
        Assert.Equal(c.GetProperty("positions").GetInt64(), c.GetProperty("numeric").GetInt64() + c.GetProperty("mate").GetInt64());
        Assert.Equal(c.GetProperty("mate").GetInt64(), training.Concat(held).Count(r => NnueRecord.Read(r).Mate));
        return (meta, training, held);
    }

    [Fact]
    public void BlitzOrSlowerGivesTunePositionsCountAndPositions()
    {
        // `nnue data --speeds blitz,rapid,classical` against `tune positions --min-elo 0 --per-game 1000
        // --lambda 0` on the same games: the records scored in centipawns are the tuner's positions, one for
        // one (game number, board, side to move, and the tuner's target from the score); the mates are extra.
        using var scratch = new Scratch();
        var games = AnalysedGames(400, 7);
        GameFile.Write(scratch["games.cbg"], games);
        Assert.Equal(0, Cli.Run(["tune", "positions", scratch["tune.tsv"], scratch["games.cbg"], "--min-elo", "0", "--per-game", "1000", "--lambda", "0"]));
        var tuned = File.ReadAllLines(scratch["tune.tsv"])
            .Select(line => line.Split('\t'))
            .Select(f => $"{f[2]} {string.Join(' ', f[0].Split(' ')[..2])} {f[1]}")
            .Order(StringComparer.Ordinal).ToList();
        var (meta, training, held) = RunNnueData(scratch["out"], scratch["games.cbg"], "--speeds", "blitz,rapid,classical", "--buckets", "8");

        var all = training.Concat(held).Select(r => NnueRecord.Read(r)).ToList();
        var fens = training.Concat(held).Select(r => NnueRecord.ToFen(r)).ToList();
        var mine = all.Select((r, i) => (r, fen: fens[i])).Where(x => !x.r.Mate)
            .Select(x => $"{x.r.Game} {string.Join(' ', x.fen.Split(' ')[..2])} " +
                         (1 / (1 + Math.Pow(10, -x.r.WhiteScore / 400.0))).ToString("0.#####", CultureInfo.InvariantCulture))
            .Order(StringComparer.Ordinal).ToList();
        Assert.True(tuned.Count > 5000, $"{tuned.Count} positions from the tuner");
        Assert.Equal(tuned.Count, meta.GetProperty("counts").GetProperty("numeric").GetInt64());
        Assert.Equal(tuned, mine);

        // Every record's result is its game's, and every game is blitz or slower.
        var speeds = new Dictionary<string, string> { ["15+0"] = "ultrabullet", ["60+0"] = "bullet", ["180+0"] = "blitz", ["300+3"] = "blitz",
                                                      ["600+5"] = "rapid", ["1800+0"] = "classical", ["-"] = "other" };
        foreach (var r in all)
        {
            var game = games[(int)r.Game - 1];
            Assert.Equal(NnueRecord.ResultCode(game.Result), r.Result);
            Assert.Contains(speeds[game.Tag("TimeControl")!], new[] { "blitz", "rapid", "classical" });
        }
        Assert.True(all.Count(r => r.Mate) > 100);
    }

    [Fact]
    public void EverySpeedByDefaultAndHeldBackGamesNeverInTheBuckets()
    {
        using var scratch = new Scratch();
        var games = AnalysedGames(400, 8);
        GameFile.Write(scratch["games.cbg"], games);
        var (meta, training, held) = RunNnueData(scratch["out"], scratch["games.cbg"]);

        Assert.All(training, r => Assert.NotEqual(0u, NnueRecord.Read(r).Game % 50));
        Assert.All(held, r => Assert.Equal(0u, NnueRecord.Read(r).Game % 50));
        var heldGames = held.Select(r => NnueRecord.Read(r).Game).Distinct().ToList();
        Assert.True(heldGames.Count >= 5, $"{heldGames.Count} held-back games");
        Assert.Equal(meta.GetProperty("held_back").GetProperty("games").GetInt64(), heldGames.Count);
        Assert.Empty(training.Select(r => NnueRecord.Read(r).Game).Intersect(heldGames));

        // Every speed is in, ultrabullet and "other" included; the buckets are all used.
        var bySpeed = meta.GetProperty("counts").GetProperty("positions_by_speed");
        foreach (string speed in new[] { "ultrabullet", "bullet", "blitz", "rapid", "classical", "other" })
            Assert.True(bySpeed.GetProperty(speed).GetInt64() > 0, speed);
        Assert.Equal(64, meta.GetProperty("bucket_counts").GetArrayLength());
        Assert.All(meta.GetProperty("bucket_counts").EnumerateArray(), c => Assert.True(c.GetInt64() > 0));
        // The games left out: casual, from a FEN, without Evals, without a rating, without a result.
        var left = meta.GetProperty("counts").GetProperty("games_left_out");
        foreach (string why in new[] { "not rated", "not from the standard start", "not analysed", "no rating", "no result" })
            Assert.True(left.GetProperty(why).GetInt64() > 0, why);
        Assert.Equal(games.Count, meta.GetProperty("counts").GetProperty("games_read").GetInt64());
    }

    [Fact]
    public void PerGameTakesThatManyAtMost()
    {
        using var scratch = new Scratch();
        GameFile.Write(scratch["games.cbg"], AnalysedGames(200, 9));
        var (_, allTraining, allHeld) = RunNnueData(scratch["all"], scratch["games.cbg"]);
        var (_, training, held) = RunNnueData(scratch["three"], scratch["games.cbg"], "--per-game", "3");
        var full = allTraining.Concat(allHeld).GroupBy(r => NnueRecord.Read(r).Game).ToDictionary(g => g.Key, g => g.Count());
        var some = training.Concat(held).GroupBy(r => NnueRecord.Read(r).Game).ToDictionary(g => g.Key, g => g.ToList());
        Assert.Equal(full.Keys.Order(), some.Keys.Order());
        foreach (var (game, records) in some)
        {
            Assert.Equal(Math.Min(3, full[game]), records.Count);
            var everyOne = allTraining.Concat(allHeld).Where(r => NnueRecord.Read(r).Game == game).Select(r => Convert.ToHexString(r)).ToHashSet();
            Assert.All(records, r => Assert.Contains(Convert.ToHexString(r), everyOne));
        }
        Assert.Contains(full.Values, n => n > 3);
    }

    [Fact]
    public void BadArgumentsAreRefused()
    {
        using var scratch = new Scratch();
        GameFile.Write(scratch["games.cbg"], AnalysedGames(3, 10));
        Assert.Equal(1, Cli.Run(["nnue", "data", scratch["out"], scratch["games.cbg"], "--speeds", "blitz,quick"]));
        Assert.Equal(1, Cli.Run(["nnue", "data", scratch["out"], scratch["games.cbg"], "--per-game", "0"]));
        Assert.Equal(1, Cli.Run(["nnue", "data", scratch["out"], scratch["games.cbg"], "--buckets"]));
        Assert.Equal(1, Cli.Run(["nnue", "data", scratch["out"], scratch["missing.cbg"]]));
        Assert.False(File.Exists(scratch["out/data.json"]));
    }
}
