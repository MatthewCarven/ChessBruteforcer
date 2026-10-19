using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Engine.Nnue;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Tests;

/// <summary>
/// The network evaluation (NNUE.md, session 3): accumulators kept move by move equal a refresh from the
/// board everywhere; SIMD equals scalar; the net is colour-symmetric; C# equals the numpy reference
/// exactly; the loader refuses a damaged file. No test sets anything process-wide: a network rides on
/// its own positions.
/// </summary>
public class NnueTests
{
    private const string EnPassant = "rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq f6 0 3";
    private const string PromotionCaptures = "1r2k2r/P1P3P1/8/8/8/8/p1p3p1/1R2K2R w Kk - 0 1";

    private static readonly Network Net32 = Network.Random(32, 1);
    private static readonly Network Net256 = Network.Random(256, 2);

    private static string DataPath(string name) => Path.Combine(AppContext.BaseDirectory, "Data", name);

    /// <summary>The position's accumulators against a refresh from its board, one number at a time.</summary>
    private static void AssertMatchesRefresh(Position position)
    {
        var accumulators = position.Accumulators!;
        var network = accumulators.Network;
        var white = new short[network.Hidden];
        var black = new short[network.Hidden];
        network.RefreshScalar(position, white, black);
        if (!accumulators.White.SequenceEqual(white) || !accumulators.Black.SequenceEqual(black))
            Assert.Fail($"the accumulators differ from a refresh at {position.ToFen()} (depth {accumulators.Depth})");
    }

    private sealed class Coverage
    {
        public long Nodes, Castles, EnPassants, Promotions, Captures;
    }

    /// <summary>Perft, checking the accumulators at every node.</summary>
    private static long Walk(Position position, int depth, Coverage seen)
    {
        AssertMatchesRefresh(position);
        seen.Nodes++;
        if (depth == 0)
            return 1;
        long leaves = 0;
        foreach (var move in MoveGenerator.Legal(position))
        {
            if ((move.Flags & MoveFlags.Castle) != 0) seen.Castles++;
            if ((move.Flags & MoveFlags.EnPassant) != 0) seen.EnPassants++;
            if (move.IsPromotion) seen.Promotions++;
            if (move.IsCapture) seen.Captures++;
            var undo = position.MakeMove(move);
            leaves += Walk(position, depth - 1, seen);
            position.UnmakeMove(move, undo);
        }
        AssertMatchesRefresh(position);
        return leaves;
    }

    [Theory]
    [InlineData(Fen.StartPosition, 4, 197_281L)]
    [InlineData(MoveGeneratorTests.Kiwipete, 3, 97_862L)]
    [InlineData(MoveGeneratorTests.EnPassantPins, 4, 43_238L)]
    [InlineData(MoveGeneratorTests.Promotions, 3, 9_467L)]
    [InlineData(MoveGeneratorTests.CastlingDiscovery, 3, 62_379L)]
    [InlineData(EnPassant, 3, -1L)]
    [InlineData(PromotionCaptures, 3, -1L)]
    public void IncrementalEqualsRefreshAtEveryNodeOfAPerftWalk(string fen, int depth, long published)
    {
        var position = Position.FromFen(fen, Net32);
        var seen = new Coverage();
        long leaves = Walk(position, depth, seen);
        if (published >= 0)
            Assert.Equal(published, leaves);   // the walk went everywhere perft goes
        Assert.Equal(0, position.Accumulators!.Depth);
        Assert.Equal(fen, position.ToFen());
    }

    [Fact]
    public void ThePerftWalksMakeEveryKindOfMove()
    {
        // So the walks above test the castling rook, the en passant pawn and the promotion.
        var seen = new Coverage();
        foreach (var (fen, depth) in new[]
                 {
                     (MoveGeneratorTests.Kiwipete, 2), (MoveGeneratorTests.Promotions, 2), (EnPassant, 1),
                     (PromotionCaptures, 1),
                 })
            Walk(Position.FromFen(fen, Net32), depth, seen);
        Assert.True(seen.Castles > 0 && seen.EnPassants > 0 && seen.Promotions > 0 && seen.Captures > 0,
                    $"castles {seen.Castles}, en passant {seen.EnPassants}, promotions {seen.Promotions}");
    }

    [Theory]
    [InlineData(MoveGeneratorTests.Kiwipete, 2)]
    [InlineData(MoveGeneratorTests.CastlingDiscovery, 2)]
    [InlineData(PromotionCaptures, 2)]
    public void IncrementalEqualsRefreshAtTheRealHiddenSize(string fen, int depth)
    {
        Walk(Position.FromFen(fen, Net256), depth, new Coverage());
    }

    [Fact]
    public void IncrementalEqualsRefreshOverRandomGamesWithNullMovesAndTakeBacks()
    {
        var (nulls, takeBacks, plies, _) = PlayRandomGames(20261016, games: 12, steps: 150);
        Assert.True(nulls > 100 && takeBacks > 100 && plies > 5_000, $"nulls {nulls}, take-backs {takeBacks}, plies {plies}");
    }

    [Fact]
    public void TheStackGrowsAsDeepAsTheGameGoes()
    {
        // No take-backs: the stack starts with 32 slots and must double (64, 128, 256, 512) under the game.
        var (_, _, plies, deepest) = PlayRandomGames(7, games: 3, steps: 400, takeBackChance: 0, nullChance: 0.02);
        Assert.True(deepest > 256, $"deepest {deepest} of {plies} plies");
    }

    [Fact]
    public void AMoveFromAnEmptySquareThrowsRatherThanReadingPastTheWeights()
    {
        // The square's piece picks the weight column; an empty square has none. The column is a checked
        // span, so this throws (before, an unchecked read went 64 columns before the weights).
        var position = Position.FromFen(Fen.StartPosition, Net32);
        var nothing = new Move((byte)Position.ParseSquare("e4"), (byte)Position.ParseSquare("e5"));
        Assert.Throws<ArgumentOutOfRangeException>(() => position.MakeMove(nothing));
    }

    /// <summary>
    /// Random games from six starts, checking the accumulators against a refresh at every step: now and
    /// then a null move, or 1-3 moves taken back; then the whole line unwound.
    /// </summary>
    internal static (int Nulls, int TakeBacks, int Plies, int Deepest) PlayRandomGames(
        int seed, int games, int steps, double takeBackChance = 0.1, double nullChance = 0.1)
    {
        var rng = new Random(seed);
        int nulls = 0, takeBacks = 0, plies = 0, deepest = 0;
        foreach (string start in new[] { Fen.StartPosition, MoveGeneratorTests.Kiwipete, MoveGeneratorTests.Promotions,
                                         MoveGeneratorTests.CastlingDiscovery, EnPassant, PromotionCaptures })
        {
            for (int game = 0; game < games; game++)
            {
                var position = Position.FromFen(start, game % 3 == 0 ? Net256 : Net32);
                var line = new Stack<(Move? Move, Undo Undo)>();
                for (int step = 0; step < steps; step++)
                {
                    AssertMatchesRefresh(position);
                    deepest = Math.Max(deepest, position.Accumulators!.Depth);
                    double roll = rng.NextDouble();
                    if (roll < nullChance && !position.InCheck())
                    {
                        line.Push((null, position.MakeNullMove()));
                        nulls++;
                        continue;
                    }
                    if (roll < nullChance + takeBackChance && line.Count > 0)
                    {
                        for (int back = rng.Next(1, 4); back > 0 && line.Count > 0; back--)
                        {
                            var (move, undo) = line.Pop();
                            if (move is Move made) position.UnmakeMove(made, undo);
                            else position.UnmakeNullMove(undo);
                            AssertMatchesRefresh(position);
                        }
                        takeBacks++;
                        continue;
                    }
                    var moves = MoveGenerator.Legal(position);
                    if (moves.Count == 0)
                        break;
                    var chosen = moves[rng.Next(moves.Count)];
                    line.Push((chosen, position.MakeMove(chosen)));
                    plies++;
                }
                while (line.Count > 0)
                {
                    var (move, undo) = line.Pop();
                    if (move is Move made) position.UnmakeMove(made, undo);
                    else position.UnmakeNullMove(undo);
                }
                AssertMatchesRefresh(position);
                Assert.Equal(0, position.Accumulators!.Depth);
                Assert.Equal(Position.FromFen(start).ToFen(), position.ToFen());
            }
        }
        return (nulls, takeBacks, plies, deepest);
    }

    [Fact]
    public void SetPieceAndAttachingRefresh()
    {
        var position = Position.FromFen(MoveGeneratorTests.Kiwipete, Net32);
        position.SetPiece(Position.ParseSquare("d4"), new Piece(PieceType.Queen, Colour.Black));
        AssertMatchesRefresh(position);
        position.SetPiece(Position.ParseSquare("e5"), Piece.Empty);
        AssertMatchesRefresh(position);
        var move = MoveGenerator.Legal(position)[0];
        var undo = position.MakeMove(move);
        AssertMatchesRefresh(position);
        position.UnmakeMove(move, undo);
        AssertMatchesRefresh(position);

        // Attached mid-game, then taken back past where the stack starts: it refreshes from the board.
        var game = Position.Start();
        var e4 = game.ParseUciMove("e2e4")!.Value;
        var made = game.MakeMove(e4);
        game.UseNetwork(Net32);
        AssertMatchesRefresh(game);
        game.UnmakeMove(e4, made);
        AssertMatchesRefresh(game);

        // Another network, then none.
        game.UseNetwork(Net256);
        Assert.Same(Net256, game.Network);
        AssertMatchesRefresh(game);
        game.UseNetwork(null);
        Assert.Null(game.Network);
        Assert.Equal(Evaluation.Evaluate(game, Evaluation.Parameters), Evaluation.Evaluate(game));
    }

    /// <summary>Positions from random games, for the checks that don't walk.</summary>
    private static IEnumerable<Position> RandomPositions(int count, int seed)
    {
        var rng = new Random(seed);
        var starts = new[] { Fen.StartPosition, MoveGeneratorTests.Kiwipete, MoveGeneratorTests.Promotions, PromotionCaptures };
        int made = 0;
        while (made < count)
        {
            var position = Position.FromFen(starts[made % starts.Length]);
            int length = rng.Next(0, 80);
            for (int ply = 0; ply < length && made < count; ply++)
            {
                var moves = MoveGenerator.Legal(position);
                if (moves.Count == 0)
                    break;
                position.MakeMove(moves[rng.Next(moves.Count)]);
            }
            made++;
            yield return position;
        }
    }

    /// <summary>The SIMD widths this CPU runs: the forward pass and every kernel are checked at each.</summary>
    private static readonly int[] SimdWidths = new[] { 256, 512 }.Where(width => width <= Kernels.Widest).ToArray();

    private static int ForwardAt(int width, Network network, ReadOnlySpan<short> us, ReadOnlySpan<short> them) =>
        width == 512 ? network.Forward512(us, them) : network.Forward256(us, them);

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(256)]
    [InlineData(1024)]
    public void SimdEqualsScalar(int hidden)
    {
        Assert.True(Avx2.IsSupported, "this test is about the SIMD passes");
        var network = Network.Random(hidden, 100 + hidden, featureRange: 200 / (hidden >= 1024 ? 2 : 1));
        var white = new short[hidden];
        var black = new short[hidden];
        var whiteScalar = new short[hidden];
        var blackScalar = new short[hidden];
        foreach (var position in RandomPositions(300, hidden))
        {
            network.Refresh(position, white, black);
            network.RefreshScalar(position, whiteScalar, blackScalar);
            Assert.True(white.AsSpan().SequenceEqual(whiteScalar) && black.AsSpan().SequenceEqual(blackScalar));
            foreach (int width in SimdWidths)
            {
                Assert.Equal(network.ForwardScalar(white, black), ForwardAt(width, network, white, black));
                Assert.Equal(network.ForwardScalar(black, white), ForwardAt(width, network, black, white));
            }
        }

        // Every int16 an accumulator can hold, either side of the clamp.
        var rng = new Random(hidden);
        for (int trial = 0; trial < 2_000; trial++)
        {
            for (int i = 0; i < hidden; i++)
            {
                white[i] = (short)rng.Next(short.MinValue, short.MaxValue + 1);
                black[i] = (short)(trial % 2 == 0 ? rng.Next(-300, 300) : rng.Next(short.MinValue, short.MaxValue + 1));
            }
            foreach (int width in SimdWidths)
                Assert.Equal(network.ForwardScalar(white, black), ForwardAt(width, network, white, black));
        }
    }

    [Fact]
    public void SimdEqualsScalarAtTheLargestSums()
    {
        // H = 1024, every output weight the largest the loader allows, every accumulator past QA: each
        // int32 lane of the 256-bit pass holds 128 products of 255 x 255 x 128, its largest (512 bits: 64).
        var network = Network.Read(Craft(1024, f: _ => 0, b: _ => 0, o: i => i % 3 == 0 ? (short)-128 : (short)128, outputBias: 0));
        var full = Enumerable.Repeat((short)1000, 1024).ToArray();
        var limited = Network.Read(Craft(1024, f: _ => 0, b: _ => 0, o: _ => 128, outputBias: 0));
        Assert.Equal(Network.MaxScore, limited.ForwardScalar(full, full));
        foreach (int width in SimdWidths)
        {
            Assert.Equal(network.ForwardScalar(full, full), ForwardAt(width, network, full, full));
            Assert.Equal(Network.MaxScore, ForwardAt(width, limited, full, full));
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(48)]
    [InlineData(1024)]
    public void EveryKernelEqualsScalarAtEveryWidth(int n)
    {
        // Wrapping int16 arithmetic, so any values at all; each operand at several offsets from a 64-byte
        // boundary (alignment is for speed: no result may depend on it). 16 and 48 take the 512-bit
        // kernels' 16-lane tail.
        var rng = new Random(n);
        short[] Numbers(int length)
        {
            var numbers = new short[length];
            for (int i = 0; i < length; i++)
                numbers[i] = (short)rng.Next(short.MinValue, short.MaxValue + 1);
            return numbers;
        }
        foreach (int offset in new[] { 0, 4, 12, 16 })
        {
            var o = Enumerable.Range(0, 5).Select(_ => Numbers(n + offset)).ToArray();
            ReadOnlySpan<short> Op(int k) => o[k].AsSpan(offset, n);
            var start = Numbers(n);
            var expected = new short[4][];
            for (int kernel = 0; kernel < 4; kernel++)
                expected[kernel] = Run(0, kernel);
            foreach (int width in SimdWidths)
                for (int kernel = 0; kernel < 4; kernel++)
                    Assert.True(Run(width, kernel).AsSpan().SequenceEqual(expected[kernel]), $"kernel {kernel} at {width} bits, n {n}, offset {offset}");

            short[] Run(int width, int kernel)
            {
                var dst = new short[n + offset];
                var d = dst.AsSpan(offset, n);
                switch (kernel)
                {
                    case 0: start.CopyTo(d); Kernels.Add(width, d, Op(0)); break;
                    case 1: Kernels.AddSub(width, d, Op(0), Op(1), Op(2)); break;
                    case 2: Kernels.AddSubSub(width, d, Op(0), Op(1), Op(2), Op(3)); break;
                    default: Kernels.AddAddSubSub(width, d, Op(0), Op(1), Op(2), Op(3), Op(4)); break;
                }
                return d.ToArray();
            }
        }
    }

    [Fact]
    public void TheWeightsAndEverySlotStartOnACacheLine()
    {
        // For speed only (a load across a 64-byte line costs two), so no other test would notice.
        foreach (var network in new[] { Net32, Net256, Network.Random(1024, 3) })
        {
            Assert.Equal(0, Aligned.Offset(network.FeatureWeights));
            Assert.Equal(0, Aligned.Offset(network.OutputWeights));
            Assert.Equal(0, Aligned.Offset(network.OutputWeights[network.Hidden..]));
            var stack = new AccumulatorStack(network, slots: 2);
            stack.Reset(Position.Start());
            var knight = new Piece(PieceType.Knight, Colour.White);
            for (int depth = 0; depth < 40; depth++)   // past 2, 4, 8, 16 and 32 slots: it grows, still aligned
            {
                Assert.Equal(depth, stack.Depth);
                Assert.Equal(0, Aligned.Offset(stack.White));
                Assert.Equal(0, Aligned.Offset(stack.Black));
                stack.PushQuiet(knight, depth % 2 == 0 ? 6 : 21, knight, depth % 2 == 0 ? 21 : 6);
            }
        }

        // CB_NNUE_SIMD can hold the width narrower, never wider than the CPU.
        Assert.Equal(Kernels.Widest, Kernels.ChooseWidth(null));
        Assert.Equal(Kernels.Widest, Kernels.ChooseWidth("512"));
        Assert.Equal(Kernels.Widest, Kernels.ChooseWidth("1024"));
        Assert.Equal(0, Kernels.ChooseWidth("0"));
        Assert.Equal(Math.Min(256, Kernels.Widest), Kernels.ChooseWidth("256"));
    }

    /// <summary>The colour-mirror: the board flipped top to bottom, colours and the side to move swapped.</summary>
    private static string Mirror(Position position)
    {
        var mirrored = Position.Empty(Position.Opponent(position.SideToMove));
        for (int square = 0; square < 64; square++)
            if (!position[square].IsEmpty)
                mirrored.SetPiece(square ^ 56, position[square] with { Colour = Position.Opponent(position[square].Colour) });
        return mirrored.ToFen();
    }

    [Fact]
    public void APositionAndItsColourMirrorScoreExactlyTheSame()
    {
        int differentFromZero = 0;
        foreach (var position in RandomPositions(1_000, 5))
        {
            string fen = position.ToFen(), mirror = Mirror(position);
            foreach (var network in new[] { Net32, Net256 })
            {
                int score = Evaluation.Evaluate(Position.FromFen(fen, network));
                Assert.Equal(score, Evaluation.Evaluate(Position.FromFen(mirror, network)));
                if (score != 0) differentFromZero++;
            }
        }
        Assert.True(differentFromZero > 1_500);   // (a net that always says 0 is symmetric too)
    }

    [Fact]
    public void MatchesTheNumpyReference()
    {
        var network = Network.Load(DataPath("nnue-random-256.nnue"));
        Assert.Equal((256, 255, 64, 400), (network.Hidden, network.QA, network.QB, network.Scale));
        int checkedPositions = 0, differentFromBuiltIn = 0;
        foreach (string line in File.ReadLines(DataPath("nnue-random-256.tsv")))
        {
            if (line.StartsWith('#'))
                continue;
            string[] parts = line.Split('\t');
            int expected = int.Parse(parts[1], CultureInfo.InvariantCulture);
            var position = Position.FromFen(parts[0], network);
            int score = Evaluation.Evaluate(position);
            if (score != expected)
                Assert.Fail($"{parts[0]}: numpy {expected}, C# {score}");
            Assert.Equal(expected, network.Evaluate(Position.FromFen(parts[0])));   // with no accumulators attached
            Assert.Equal(expected, position.SideToMove == Colour.White
                ? network.ForwardScalar(position.Accumulators!.White, position.Accumulators.Black)
                : network.ForwardScalar(position.Accumulators!.Black, position.Accumulators.White));
            if (score != Evaluation.Evaluate(position, Evaluation.Parameters)) differentFromBuiltIn++;
            checkedPositions++;
        }
        Assert.Equal(1_000, checkedPositions);
        Assert.True(differentFromBuiltIn > 900);   // Evaluate really is the network's
    }

    [Fact]
    public void TheNumpyFixtureStillMatchesItsChecksum()
    {
        // A cheap guard against the fixture being regenerated with different weights but not its scores.
        var network = Network.Load(DataPath("nnue-random-256.nnue"));
        Assert.Equal("f50d0032e8906c94b065cf94ed28974a898818ddf19e93cf1dd6400f52f6e81b", network.Sha256);
    }

    // ---- the file ----

    /// <summary>A .nnue file written here, independently of <see cref="Network.ToBytes"/>, with a correct checksum.</summary>
    private static byte[] Craft(int hidden, Func<int, short> f, Func<int, short> b, Func<int, short> o, int outputBias,
                                uint version = 1, uint inputs = 768, int qa = 255, int qb = 64, int scale = 400)
    {
        var bytes = new List<byte>("CBNNUE01"u8.ToArray());
        void U32(uint v) { var x = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(x, v); bytes.AddRange(x); }
        void I32(int v) { var x = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(x, v); bytes.AddRange(x); }
        void I16(short v) { var x = new byte[2]; BinaryPrimitives.WriteInt16LittleEndian(x, v); bytes.AddRange(x); }
        U32(version);
        U32(inputs);
        U32((uint)hidden);
        I32(qa);
        I32(qb);
        I32(scale);
        for (int i = 0; i < (int)inputs * hidden; i++) I16(f(i));
        for (int i = 0; i < hidden; i++) I16(b(i));
        for (int i = 0; i < 2 * hidden; i++) I16(o(i));
        I32(outputBias);
        bytes.AddRange(SHA256.HashData(bytes.ToArray()));
        return bytes.ToArray();
    }

    private static byte[] Rehash(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        SHA256.HashData(copy.AsSpan(0, copy.Length - 32), copy.AsSpan(copy.Length - 32));
        return copy;
    }

    [Fact]
    public void TheFileRoundTrips()
    {
        byte[] bytes = Net32.ToBytes();
        Assert.Equal(32 + 2 * (768 * 32 + 32 + 64) + 4 + 32, bytes.Length);
        var again = Network.Read(bytes);
        Assert.Equal(Net32.Sha256, again.Sha256);
        Assert.Equal(bytes, again.ToBytes());
        foreach (var position in RandomPositions(50, 9))
            Assert.Equal(Net32.Evaluate(position), again.Evaluate(position));

        // The craft above writes the format as NNUE.md gives it; the reader agrees with it.
        var crafted = Network.Read(Craft(32, f: i => (short)(i % 7 - 3), b: i => (short)i, o: i => (short)(i % 5 - 2), outputBias: 1234));
        Assert.Equal((32, 1234), (crafted.Hidden, crafted.OutputBias));
        Assert.Equal((short)(1000 % 7 - 3), crafted.FeatureWeights[1000]);
        Assert.Equal((short)31, crafted.FeatureBiases[31]);
        Assert.Equal((short)(63 % 5 - 2), crafted.OutputWeights[63]);
    }

    [Fact]
    public void TheLoaderRefusesABadMagic()
    {
        byte[] bytes = Net32.ToBytes();
        bytes[3] ^= 1;
        Assert.Contains("CBNNUE01", Assert.Throws<FormatException>(() => Network.Read(bytes)).Message);
        Assert.Contains("CBNNUE01", Assert.Throws<FormatException>(() => Network.Read(Rehash(bytes))).Message);
        Assert.Throws<FormatException>(() => Network.Read("CBNN"u8));
    }

    [Theory]
    [InlineData(24)]          // QB in the header: 64 becomes 80, which its own check allows
    [InlineData(5_000)]       // a feature weight
    [InlineData(-35)]         // the output bias (counted from the end)
    [InlineData(-1)]          // the checksum itself
    public void TheLoaderRefusesABadChecksum(int at)
    {
        byte[] bytes = Net32.ToBytes();
        bytes[at >= 0 ? at : bytes.Length + at] ^= 0x10;
        Assert.Contains("SHA-256", Assert.Throws<FormatException>(() => Network.Read(bytes)).Message);
    }

    [Fact]
    public void TheLoaderRefusesWhatItCannotRun()
    {
        Func<int, short> zero = _ => 0;
        Assert.Contains("version", Assert.Throws<FormatException>(() => Network.Read(Craft(16, zero, zero, zero, 0, version: 2))).Message);
        Assert.Contains("inputs", Assert.Throws<FormatException>(() => Network.Read(Craft(16, zero, zero, zero, 0, inputs: 640))).Message);
        Assert.Contains("hidden size", Assert.Throws<FormatException>(() => Network.Read(Craft(24, zero, zero, zero, 0))).Message);
        Assert.Contains("QA", Assert.Throws<FormatException>(() => Network.Read(Craft(16, zero, zero, zero, 0, qa: 256))).Message);
        byte[] cut = Net32.ToBytes()[..^1];
        Assert.Throws<FormatException>(() => Network.Read(cut));
        // An output weight whose product with QA overflows 16 bits in the SIMD pass.
        Assert.Contains("output weight", Assert.Throws<FormatException>(() => Network.Read(Craft(16, zero, zero, o: i => i == 5 ? (short)129 : (short)0, 0))).Message);
        // A hidden unit that could overflow on a full board: 32 x 1,100 > 32,767.
        Assert.Contains("hidden unit", Assert.Throws<FormatException>(() => Network.Read(Craft(16, f: i => i % 16 == 3 ? (short)1100 : (short)0, zero, zero, 0))).Message);
        Assert.Contains("hidden unit", Assert.Throws<FormatException>(() => Network.Read(Craft(16, f: i => i % 16 == 3 ? (short)-1100 : (short)0, zero, zero, 0))).Message);
    }

    [Fact]
    public void AScoreNeverReadsAsAMate()
    {
        Func<int, short> zero = _ => 0;
        var high = Network.Read(Craft(16, zero, zero, zero, int.MaxValue / 2));
        var low = Network.Read(Craft(16, zero, zero, zero, int.MinValue / 2));
        var position = Position.Start();
        Assert.Equal(Network.MaxScore, high.Evaluate(position));
        Assert.Equal(-Network.MaxScore, low.Evaluate(position));
        Assert.True(Network.MaxScore < Search.MateBound);
    }

    [Fact]
    public void IsNetworkFileTellsTheKindsApart()
    {
        string network = Path.Combine(Path.GetTempPath(), $"net-{Guid.NewGuid():N}.nnue");
        string numbers = Path.Combine(Path.GetTempPath(), $"eval-{Guid.NewGuid():N}.txt");
        try
        {
            Net32.Save(network);
            EvalParams.Original().Save(numbers);
            Assert.True(Network.IsNetworkFile(network));
            Assert.False(Network.IsNetworkFile(numbers));
            Assert.False(Network.IsNetworkFile(""));
            Assert.False(Network.IsNetworkFile("  "));
            Assert.Throws<FileNotFoundException>(() => Network.IsNetworkFile(network + ".missing"));
            Assert.Equal(Net32.Sha256, Network.Load(network).Sha256);
        }
        finally
        {
            File.Delete(network);
            File.Delete(numbers);
        }
    }

    // ---- meaning ----

    /// <summary>Material and piece-square values, middlegame and endgame averaged, for the side to move.</summary>
    private static double PieceSquareAverage(Position position, EvalParams p)
    {
        double total = 0;
        for (int square = 0; square < 64; square++)
        {
            var piece = position[square];
            if (piece.IsEmpty)
                continue;
            int table = piece.Colour == Colour.White ? square ^ 56 : square;
            double value = (p.Mg[(int)piece.Type][table] + p.Eg[(int)piece.Type][table]) / 2.0;
            total += piece.Colour == Colour.White ? value : -value;
        }
        return position.SideToMove == Colour.White ? total : -total;
    }

    [Fact]
    public void ANetworkBuiltFromPieceSquareValuesScoresThem()
    {
        // Own pieces first, the other side's board flipped: if the inputs meant anything else, a net
        // built on that reading would score real positions differently from the numbers it was built from.
        var p = EvalParams.Original();
        var network = Network.FromPieceSquares(p);
        int compared = 0, blackToMove = 0;
        foreach (var position in RandomPositions(500, 77))
        {
            double expected = PieceSquareAverage(position, p);
            if (Math.Abs(expected) > 3_500)
                continue;   // (past about 4,000 the hidden units saturate, by design)
            int score = Evaluation.Evaluate(Position.FromFen(position.ToFen(), network));
            Assert.True(Math.Abs(score - expected) <= 3, $"{position.ToFen()}: the numbers say {expected}, the net {score}");
            compared++;
            if (position.SideToMove == Colour.Black) blackToMove++;
        }
        Assert.True(compared > 400 && blackToMove > 150, $"{compared} compared, {blackToMove} with black to move");
    }

    // ---- in the engine ----

    [Fact]
    public void TheSearchRunsOnTheNetworkWithHelperThreads()
    {
        // (A net with random weights makes the capture search explode: thousands of times the nodes on
        // Kiwipete. The piece-square net gives a tree like a real evaluation's.)
        var network = Network.FromPieceSquares(EvalParams.Original());
        foreach (var split in new[] { ParallelMode.LazySmp, ParallelMode.Ranges })
        {
            var root = Position.FromFen(MoveGeneratorTests.Kiwipete, network);
            var search = new Search(new TranspositionTable(8)) { Threads = 3, Split = split };
            var result = search.Run(root, new SearchLimits { Depth = 6 });
            Assert.NotNull(result.BestMove);
            Assert.Contains(result.BestMove!.Value, MoveGenerator.Legal(Position.FromFen(MoveGeneratorTests.Kiwipete)));
            Assert.Equal(MoveGeneratorTests.Kiwipete, root.ToFen());
            Assert.Equal(0, root.Accumulators!.Depth);
            AssertMatchesRefresh(root);
        }
    }

    /// <summary>A net that says +1000 for the side to move, whatever the board: 1000 x QA x QB / scale as the output bias.</summary>
    private static byte[] ConstantNet() => Craft(16, f: _ => 0, b: _ => 0, o: _ => 0, outputBias: 1000 * 255 * 64 / 400);

    [Theory]
    [InlineData(1, ParallelMode.LazySmp)]
    [InlineData(3, ParallelMode.LazySmp)]
    [InlineData(3, ParallelMode.Ranges)]
    public void EveryThreadSearchesWithTheRootsNetwork(int threads, ParallelMode split)
    {
        // Every leaf is +1000 for whoever is to move there, so the root's score is exactly +-1000 (by the
        // depth's parity); a helper on the hand-made evaluation would bring in scores near 0 (Ranges takes
        // its answer from a helper outright).
        var network = Network.Read(ConstantNet());
        Assert.Equal(1000, network.Evaluate(Position.Start()));
        for (int depth = 4; depth <= 5; depth++)
        {
            var search = new Search(new TranspositionTable(8)) { Threads = threads, Split = split };
            var root = Position.FromFen(MoveGeneratorTests.Middlegame, network);
            var result = search.Run(root, new SearchLimits { Depth = depth });
            Assert.Equal(1000, Math.Abs(result.Score));
            // (Lazy SMP's answer is the main thread's, which a helper reaches only through the table, so
            // the score alone may miss a helper on the wrong evaluation: look at each one's position.)
            var helpers = search.HelperRoots.ToList();
            Assert.Equal(threads == 1 ? 0 : split == ParallelMode.LazySmp ? threads - 1 : threads, helpers.Count);
            foreach (var helperRoot in helpers)
            {
                Assert.NotSame(root, helperRoot);
                Assert.Same(network, helperRoot!.Network);
            }
        }
    }

    [Fact]
    public void UciEvalFileTakesANetworkByItsMagic()
    {
        var output = new StringWriter();
        var engine = new UciEngine(output);
        string path = DataPath("nnue-random-256.nnue");
        engine.Handle($"setoption name EvalFile value {path}");
        Assert.Contains("evaluation: network", output.ToString());
        engine.Handle("position startpos");
        engine.Handle("eval");
        Assert.Contains("eval 37 cp", output.ToString());   // the numpy reference's score for the start
        engine.Handle("go depth 4");
        engine.WaitForSearch();
        Assert.Contains("bestmove ", output.ToString());

        // The search's own copy of the position carries the network too.
        string constant = Path.Combine(Path.GetTempPath(), $"constant-{Guid.NewGuid():N}.nnue");
        try
        {
            File.WriteAllBytes(constant, ConstantNet());
            engine.Handle($"setoption name EvalFile value {constant}");
            engine.Handle("setoption name Threads value 2");
            engine.Handle("go depth 4");
            engine.WaitForSearch();
            Assert.Contains("info depth 4 score cp 1000 ", output.ToString());
            engine.Handle("setoption name Threads value 1");
            engine.Handle($"setoption name EvalFile value {path}");
        }
        finally
        {
            File.Delete(constant);
        }

        // A damaged network is refused, and the one loaded stays.
        string damaged = Path.Combine(Path.GetTempPath(), $"damaged-{Guid.NewGuid():N}.nnue");
        var loaded = engine.Position.Network;
        Assert.NotNull(loaded);
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            bytes[1000] ^= 1;
            File.WriteAllBytes(damaged, bytes);
            engine.Handle($"setoption name EvalFile value {damaged}");
            Assert.Contains("SHA-256", output.ToString());
            Assert.Same(loaded, engine.Position.Network);
            engine.Handle("setoption name EvalFile value no-such-file.nnue");
            Assert.Contains("info string error", output.ToString());
            Assert.Same(loaded, engine.Position.Network);
        }
        finally
        {
            File.Delete(damaged);
        }

        engine.Handle("setoption name EvalFile value <empty>");
        Assert.Contains("evaluation: built in", output.ToString());
        Assert.Null(engine.Position.Network);
        // An empty value is the built-in evaluation too, as is no value at all.
        engine.Handle($"setoption name EvalFile value {path}");
        engine.Handle("setoption name EvalFile value");
        Assert.Null(engine.Position.Network);
        engine.Handle($"setoption name EvalFile value {path}");
        engine.Handle("setoption name EvalFile");
        Assert.Null(engine.Position.Network);
        Assert.DoesNotContain("info string error: The value cannot be an empty string", output.ToString());
        engine.Handle("eval");
        Assert.Contains($"eval {Evaluation.Evaluate(Position.Start(), EvalParams.Original())} cp", output.ToString());
    }
}
