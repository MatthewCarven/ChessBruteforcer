using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Engine.Nnue;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Tests;

/// <summary>
/// Version 2 networks, the deeper ones (NNUE.md, session 6): C# equals the numpy reference exactly; every SIMD
/// width equals the scalar definition, the largest sums included; a position and its colour-mirror score the
/// same; the file round-trips and the loader refuses what it can't run; the search runs on one. The feature
/// layer and its accumulators are version 1's, which NnueTests checks.
/// </summary>
public class NnueDeepTests
{
    private static string DataPath(string name) => Path.Combine(AppContext.BaseDirectory, "Data", name);

    /// <summary>0 (one number at a time) and the SIMD widths this CPU runs.</summary>
    private static readonly int[] Widths = new[] { 0, 256, 512 }.Where(width => width <= Kernels.Widest).ToArray();

    private static IEnumerable<Position> RandomPositions(int count, int seed)
    {
        var rng = new Random(seed);
        var starts = new[] { Fen.StartPosition, MoveGeneratorTests.Kiwipete, MoveGeneratorTests.Promotions };
        for (int made = 0; made < count; made++)
        {
            var position = Position.FromFen(starts[made % starts.Length]);
            int length = rng.Next(0, 80);
            for (int ply = 0; ply < length; ply++)
            {
                var moves = MoveGenerator.Legal(position);
                if (moves.Count == 0)
                    break;
                position.MakeMove(moves[rng.Next(moves.Count)]);
            }
            yield return position;
        }
    }

    private static int[] Sizes(string shape) => shape.Split('-').Select(int.Parse).ToArray();

    /// <summary>
    /// Every width against the definition. The pass's per-thread scratch is filled with junk before each, so a width that
    /// read numbers it hadn't written would show (with the scratch as the last width left it, it could pass on them).
    /// </summary>
    private static void AssertWidthsAgree(Network network, short[] us, short[] them)
    {
        int expected = network.ForwardDeepScalar(us, them);
        foreach (int width in Widths)
        {
            Network.FillScratch(0x5A5A);
            int score = network.ForwardDeep(width, us, them);
            if (score != expected)
                Assert.Fail($"{width} bits: {score}, the definition {expected} ({network})");
        }
    }

    // ---- against the numpy reference ----

    [Theory]
    [InlineData("nnue-random-deep-32", "c97452fefe22f7974c23877e81f761e2718f5089765adf72b63394150a2b1230", "32")]
    [InlineData("nnue-random-deep-16-32", "cd050210f991811bee3fea4693a9a0e6e159dccbf3c093600542cfae714652e9", "16-32")]
    public void MatchesTheNumpyReference(string name, string sha256, string shape)
    {
        var network = Network.Load(DataPath(name + ".nnue"));
        Assert.Equal(sha256, network.Sha256);   // (a guard against the fixture being regenerated without its scores)
        Assert.Equal(Network.DeepVersion, network.FileVersion);
        Assert.Equal(Sizes(shape), network.Layers.Select(layer => layer.Outputs));
        Assert.Equal((128, 255, 400), (network.Hidden, network.QA, network.Scale));
        Assert.StartsWith($"768 -> 128 x2 -> {shape.Replace("-", " -> ")} -> 1, sha256 ", network.ToString());
        int checkedPositions = 0, nonZero = 0;
        foreach (string line in File.ReadLines(DataPath(name + ".tsv")))
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
            var accumulators = position.Accumulators!;
            short[] white = accumulators.White.ToArray(), black = accumulators.Black.ToArray();
            short[] us = position.SideToMove == Colour.White ? white : black;
            short[] them = position.SideToMove == Colour.White ? black : white;
            Assert.Equal(expected, network.ForwardDeepScalar(us, them));
            foreach (int width in Widths)
                Assert.Equal(expected, network.ForwardDeep(width, us, them));
            if (expected != 0)
                nonZero++;
            checkedPositions++;
        }
        Assert.Equal(1_000, checkedPositions);
        Assert.True(nonZero > 950);
    }

    // ---- SIMD against the definition ----

    [Theory]
    [InlineData(16, "32")]
    [InlineData(48, "16-32")]
    [InlineData(48, "48-8")]          // 48 inputs to the second layer
    [InlineData(32, "16-48")]         // 48 into the output's dot product: 32 at 512 bits, then 16 at 256
    [InlineData(64, "256-3-17-5")]    // the most layers, the largest and odd sizes: every tail
    [InlineData(256, "1")]
    [InlineData(1024, "32")]
    [InlineData(1024, "16-32")]
    [InlineData(1536, "32")]          // h1536-3m-l32's shape
    [InlineData(2048, "16-32")]       // MaxHidden
    public void EveryWidthEqualsTheDefinition(int hidden, string shape)
    {
        var network = Network.RandomDeep(hidden, Sizes(shape), seed: hidden + shape.Length, featureRange: hidden >= 1024 ? 100 : 200);
        var white = new short[hidden];
        var black = new short[hidden];
        foreach (var position in RandomPositions(150, hidden))
        {
            network.Refresh(position, white, black);
            AssertWidthsAgree(network, white, black);
            AssertWidthsAgree(network, black, white);
        }

        // Every int16 an accumulator can hold, either side of the clamp: the activations' 16-bit arithmetic included.
        var rng = new Random(hidden);
        for (int trial = 0; trial < 1_000; trial++)
        {
            for (int i = 0; i < hidden; i++)
            {
                white[i] = (short)rng.Next(short.MinValue, short.MaxValue + 1);
                black[i] = (short)(trial % 2 == 0 ? rng.Next(-300, 300) : rng.Next(short.MinValue, short.MaxValue + 1));
            }
            AssertWidthsAgree(network, white, black);
        }

        // Every activation the first layer can see: each accumulator value from -2 to QA + 2 in turn.
        for (int value = -2; value <= 257; value++)
        {
            Array.Fill(white, (short)value);
            Array.Fill(black, (short)(255 - value));
            AssertWidthsAgree(network, white, black);
        }
    }

    [Fact]
    public void EveryWidthEqualsTheDefinitionAtTheLargestSums()
    {
        // H 1024, every activation at QA and every first-layer weight the largest its row check allows: each row's
        // int32 sum within 0.03% of int.MaxValue (or of -int.MaxValue, on the rows of negative weights).
        const int hidden = 1024, q = 1 << 14;
        short most = (short)((int.MaxValue - q / 2) / (255L * 2 * hidden));
        byte[] bytes = CraftDeep(hidden, [32, 32], (layer, j, i) => layer switch
        {
            0 => j % 2 == 0 ? most : (short)-most,
            1 => (short)(i % 3 == 0 ? -32767 : 32767),
            _ => (short)(i % 4 == 0 ? -32767 : 32767),   // (alternating signs would cancel to 0)
        }, (_, _) => 0, [q, 1 << 10, 1 << 10]);
        var network = Network.Read(bytes);
        var full = Enumerable.Repeat((short)1000, hidden).ToArray();
        AssertWidthsAgree(network, full, full);
        Assert.NotEqual(0, network.ForwardDeepScalar(full, full));

        // A little more on one weight of one row (the floor above left ~546,800 spare, 2,145 steps of QA), and that row
        // could pass 32 bits: refused.
        byte[] over = CraftDeep(hidden, [32, 32], (layer, j, i) => layer == 0 && j == 5 && i == 0 ? (short)(most + 2_200)
            : layer == 0 ? most : (short)1, (_, _) => 0, [q, 1 << 10, 1 << 10]);
        Assert.Contains("past 32 bits", Assert.Throws<FormatException>(() => Network.Read(over)).Message);
    }

    [Fact]
    public void TheRowCheckCountsTheRoundingsHalfStep()
    {
        // A row whose weights and bias come to exactly int.MaxValue - Q / 2 fits; one more and the + Q / 2 could overflow.
        const int q = 1 << 16;
        int Bias(int extra) => int.MaxValue - q / 2 + extra;
        Assert.Equal(Network.DeepVersion, Network.Read(CraftDeep(16, [8], (_, _, _) => 0, (layer, j) => layer == 0 && j == 0 ? Bias(0) : 0, [q, 64])).FileVersion);
        Assert.Contains("past 32 bits", Assert.Throws<FormatException>(() =>
            Network.Read(CraftDeep(16, [8], (_, _, _) => 0, (layer, j) => layer == 0 && j == 0 ? Bias(1) : 0, [q, 64]))).Message);
    }

    // ---- the net as a whole ----

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
        var network = Network.Load(DataPath("nnue-random-deep-16-32.nnue"));
        int differentFromZero = 0;
        foreach (var position in RandomPositions(500, 7))
        {
            string fen = position.ToFen();
            int score = Evaluation.Evaluate(Position.FromFen(fen, network));
            Assert.Equal(score, Evaluation.Evaluate(Position.FromFen(Mirror(position), network)));
            if (score != 0)
                differentFromZero++;
        }
        Assert.True(differentFromZero > 450);
    }

    [Fact]
    public void MoveByMoveScoresEqualAFreshEvaluation()
    {
        // The accumulators are version 1's; the deeper pass must read them the same at every ply of a game.
        var network = Network.RandomDeep(64, [16, 8], seed: 11);
        var rng = new Random(12);
        for (int game = 0; game < 20; game++)
        {
            var position = Position.FromFen(MoveGeneratorTests.Kiwipete, network);
            for (int ply = 0; ply < 60; ply++)
            {
                Assert.Equal(network.Evaluate(Position.FromFen(position.ToFen())), Evaluation.Evaluate(position));
                var moves = MoveGenerator.Legal(position);
                if (moves.Count == 0)
                    break;
                position.MakeMove(moves[rng.Next(moves.Count)]);
            }
        }
    }

    // ---- the file ----

    /// <summary>
    /// A version 2 file written here, independently of <see cref="Network.ToBytes"/>, with a correct checksum: feature
    /// weights i % 7 - 3, biases i % 11; <paramref name="weight"/>(layer, row, input) and <paramref name="bias"/>(layer, row) for the
    /// small layers and then the output (layer = sizes.Length); <paramref name="q"/> one per layer then the output's.
    /// </summary>
    private static byte[] CraftDeep(int hidden, int[] sizes, Func<int, int, int, short> weight, Func<int, int, int> bias, int[] q,
                                    uint version = 2, uint inputs = 768, int qa = 255, int scale = 400, uint? count = null,
                                    int[]? headerSizes = null)
    {
        var bytes = new List<byte>("CBNNUE01"u8.ToArray());
        void U32(uint v) { var x = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(x, v); bytes.AddRange(x); }
        void I32(int v) { var x = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(x, v); bytes.AddRange(x); }
        void I16(short v) { var x = new byte[2]; BinaryPrimitives.WriteInt16LittleEndian(x, v); bytes.AddRange(x); }
        U32(version);
        U32(inputs);
        U32((uint)hidden);
        I32(qa);
        I32(scale);
        U32(count ?? (uint)sizes.Length);
        foreach (int size in headerSizes ?? sizes)
            U32((uint)size);
        for (int i = 0; i < (int)inputs * hidden; i++) I16((short)(i % 7 - 3));
        for (int i = 0; i < hidden; i++) I16((short)(i % 11));
        int from = 2 * hidden;
        for (int layer = 0; layer <= sizes.Length; layer++)
        {
            int to = layer < sizes.Length ? sizes[layer] : 1;
            I32(q[layer]);
            for (int j = 0; j < to; j++)
                for (int i = 0; i < from; i++)
                    I16(weight(layer, j, i));
            for (int j = 0; j < to; j++)
                I32(bias(layer, j));
            from = to;
        }
        bytes.AddRange(SHA256.HashData(bytes.ToArray()));
        return bytes.ToArray();
    }

    [Fact]
    public void TheFileRoundTrips()
    {
        var network = Network.RandomDeep(32, [8, 4], seed: 5);
        byte[] bytes = network.ToBytes();
        var again = Network.Read(bytes);
        Assert.Equal(network.Sha256, again.Sha256);
        Assert.Equal(bytes, again.ToBytes());
        foreach (var position in RandomPositions(50, 9))
            Assert.Equal(network.Evaluate(position), again.Evaluate(position));

        // The craft above writes the format as NNUE.md gives it; the reader agrees with it, row by row.
        var crafted = Network.Read(CraftDeep(32, [8, 4], (layer, j, i) => (short)(100 * layer + 10 * j + i % 10),
                                             (layer, j) => -1000 * layer - j, [64, 128, 256]));
        Assert.Equal(new[] { 64, 128 }, crafted.Layers.Select(layer => layer.Q));
        Assert.Equal(256, crafted.Output!.Q);
        Assert.Equal((6, 7, 8), (crafted.Layers[0].Shift, crafted.Layers[1].Shift, crafted.Output.Shift));
        Assert.Equal((short)(1000 % 7 - 3), crafted.FeatureWeights[1000]);
        Assert.Equal((short)(31 % 11), crafted.FeatureBiases[31]);
        Assert.Equal((short)(10 * 3 + 5), crafted.Layers[0].Row(3)[5]);
        Assert.Equal((short)(100 + 10 * 2 + 7), crafted.Layers[1].Row(2)[7]);
        Assert.Equal((short)(200 + 3), crafted.Output.Row(0)[3]);
        Assert.Equal(-1002, crafted.Layers[1].Biases[2]);
        Assert.Equal(-2000, crafted.Output.Biases[0]);
        Assert.Equal((64, 8, 8, 4, 4, 1), (crafted.Layers[0].Inputs, crafted.Layers[0].Outputs, crafted.Layers[1].Inputs,
                                           crafted.Layers[1].Outputs, crafted.Output.Inputs, crafted.Output.Outputs));
    }

    [Fact]
    public void TheLoaderRefusesWhatItCannotRun()
    {
        Func<int, int, int, short> zero = (_, _, _) => 0;
        Func<int, int, int> none = (_, _) => 0;
        FormatException Refused(byte[] bytes) => Assert.Throws<FormatException>(() => Network.Read(bytes));
        Assert.Contains("version", Refused(CraftDeep(16, [8], zero, none, [64, 64], version: 4)).Message);   // (3 is the bucketed nets')
        Assert.Contains("inputs", Refused(CraftDeep(16, [8], zero, none, [64, 64], inputs: 640)).Message);
        Assert.Contains("hidden size", Refused(CraftDeep(24, [8], zero, none, [64, 64])).Message);
        Assert.Contains("QA", Refused(CraftDeep(16, [8], zero, none, [64, 64], qa: 256)).Message);
        Assert.Contains("small layers", Refused(CraftDeep(16, [8], zero, none, [64, 64], count: 0, headerSizes: [])).Message);
        Assert.Contains("small layers", Refused(CraftDeep(16, [8, 8, 8, 8, 8], zero, none, [64, 64, 64, 64, 64, 64])).Message);
        Assert.Contains("numbers", Refused(CraftDeep(16, [0], zero, none, [64, 64])).Message);
        Assert.Contains("numbers", Refused(CraftDeep(16, [257], zero, none, [64, 64])).Message);
        foreach (int q in new[] { 0, -64, 3, 96, 1 << 25 })
            Assert.Contains("power of two", Refused(CraftDeep(16, [8], zero, none, [q, 64])).Message);
        Assert.Contains("power of two", Refused(CraftDeep(16, [8], zero, none, [64, 48])).Message);   // the output's Q too
        // The output's row is checked as the layers' are: 8 inputs at QA x 32,767 on top of a bias near the limit.
        Assert.Contains("the output", Refused(CraftDeep(16, [8], (layer, _, _) => layer == 1 ? (short)32767 : (short)0,
                                                        (layer, _) => layer == 1 ? int.MaxValue - 1_000 : 0, [64, 64])).Message);
        // Header sizes that don't match the layers written: the length gives it away.
        Assert.Contains("bytes", Refused(CraftDeep(16, [8], zero, none, [64, 64], headerSizes: [9])).Message);
        byte[] good = CraftDeep(16, [8], zero, none, [64, 64]);
        Assert.Throws<FormatException>(() => Network.Read(good[..^1]));
        byte[] damaged = (byte[])good.Clone();
        damaged[1_000] ^= 1;
        Assert.Contains("SHA-256", Refused(damaged).Message);
        Assert.Equal(Network.DeepVersion, Network.Read(good).FileVersion);
    }

    // ---- in the engine ----

    /// <summary>A version 2 net that says +1000 for the side to move whatever the board: only the output's bias.</summary>
    private static byte[] ConstantDeepNet() =>
        CraftDeep(16, [8], (_, _, _) => 0, (layer, _) => layer == 1 ? 1000 * 255 * 64 / 400 : 0, [64, 64]);

    [Theory]
    [InlineData(1, ParallelMode.LazySmp)]
    [InlineData(3, ParallelMode.LazySmp)]
    [InlineData(3, ParallelMode.Ranges)]
    public void TheSearchRunsOnADeepNet(int threads, ParallelMode split)
    {
        var network = Network.Read(ConstantDeepNet());
        Assert.Equal(1000, network.Evaluate(Position.Start()));
        var search = new Search(new TranspositionTable(8)) { Threads = threads, Split = split };
        var root = Position.FromFen(MoveGeneratorTests.Middlegame, network);
        var result = search.Run(root, new SearchLimits { Depth = 5 });
        Assert.Equal(1000, Math.Abs(result.Score));
        foreach (var helperRoot in search.HelperRoots)
            Assert.Same(network, helperRoot!.Network);
    }

    [Fact]
    public void UciEvalFileTakesADeepNet()
    {
        var output = new StringWriter();
        var engine = new UciEngine(output);
        engine.Handle($"setoption name EvalFile value {DataPath("nnue-random-deep-16-32.nnue")}");
        Assert.Contains("evaluation: network", output.ToString());
        Assert.Contains("768 -> 128 x2 -> 16 -> 32 -> 1, sha256 cd050210f991", output.ToString());
        engine.Handle("position startpos");
        engine.Handle("eval");
        string expected = File.ReadLines(DataPath("nnue-random-deep-16-32.tsv")).Skip(1).First().Split('\t')[1];
        Assert.Contains($"eval {expected} cp", output.ToString());   // the numpy reference's score for the start
        engine.Handle("go depth 3");
        engine.WaitForSearch();
        Assert.Contains("bestmove ", output.ToString());
    }
}
