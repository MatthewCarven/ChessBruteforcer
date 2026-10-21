using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Engine.Nnue;

/// <summary>
/// A 768 -> H x2 -> 1 network with SCReLU, in int16 (NNUE.md, "The network"): one input for each of the
/// 12 pieces on each of the 64 squares; H hidden numbers per perspective, the sum of the bias and one
/// weight column per piece on the board (the accumulator, which <see cref="AccumulatorStack"/> keeps up
/// to date move by move); then each of the 2H numbers, the side to move's half first, clamped to
/// [0, QA], squared, times an output weight, summed. The hidden size is the file's.
///
/// The file (.nnue, ours), little-endian throughout:
/// <code>
///   8 bytes   "CBNNUE01"
///   u32       version (1)
///   u32       inputs (768)
///   u32       hidden H (a multiple of 16, 16 to 2048)
///   i32       QA (255), QB (64), scale (400)
///   int16     feature weights [768][H], feature-major: one feature's column of H is contiguous
///   int16     feature biases [H]
///   int16     output weights [2H]: the side to move's H, then the other side's
///   i32       output bias
///   32 bytes  SHA-256 of everything above
/// </code>
/// Score for the side to move, in centipawns: (sum / QA + output bias) x scale / (QA x QB), each
/// division truncating towards zero (C#'s), clamped to +-<see cref="MaxScore"/>.
///
/// Version 2 (2026-10-21, NNUE.md session 6: the deeper nets) keeps the feature layer and puts 1-4 small
/// layers between it and the score:
/// <code>
///   8 bytes   "CBNNUE01"
///   u32       version (2), inputs (768), hidden H
///   i32       QA (255), scale (400)
///   u32       L, the number of small layers (1-4), then L sizes (1-256 each)
///   int16     feature weights [768][H], feature biases [H]     (as version 1)
///   per layer: i32 Q (a power of two), int16 weights [size][inputs] (one output's inputs contiguous), i32 biases [size]
///   the output: i32 Q, int16 weights [the last layer's size], i32 bias
///   32 bytes  SHA-256 of everything above
/// </code>
/// The first layer's 2H inputs are x = (v x v + QA / 2) / QA, v each accumulator number clamped to [0, QA]
/// (the side to move's H first); each layer's outputs are clamp((bias + sum x w + Q / 2) / Q, 0, QA); the
/// score is (bias + sum x w) x scale / (QA x Q) of the output's. Divisions truncate towards zero, sums
/// fit in int32 (the loader checks every row at the largest inputs), and the score is clamped as version 1's.
/// scripts/nnue-reference/nnue_reference.py is the reference these must equal exactly.
/// </summary>
public sealed class Network
{
    public const int Version = 1;
    public const int DeepVersion = 2;
    public const int Inputs = 768;
    public const int MaxHidden = 2048;      // 1024 to 2026-10-22; 2048 still can't overflow Forward256 (see there)
    public const int MaxLayers = 4;
    public const int MaxLayerSize = 256;
    public const int MaxLayerQ = 1 << 24;

    /// <summary>No network score may read as a mate.</summary>
    public const int MaxScore = Search.MateBound - 1;

    private static ReadOnlySpan<byte> Magic => "CBNNUE01"u8;
    private const int HeaderBytes = 8 + 6 * 4;
    private const int HashBytes = 32;

    public int Hidden { get; }
    public int QA { get; }
    public int QB { get; }
    public int Scale { get; }
    public int OutputBias { get; }

    /// <summary>The file's SHA-256, in hex: says which net a log or a match used.</summary>
    public string Sha256 { get; }

    /// <summary>[768][H], feature-major, from a 64-byte boundary on (so every column starts on one when H is a multiple of 32).</summary>
    internal ReadOnlySpan<short> FeatureWeights => _featureWeights.AsSpan(_featureWeightsAt, Inputs * Hidden);

    /// <summary>[H].</summary>
    internal short[] FeatureBiases { get; }

    /// <summary>[2H]: the side to move's half first; from a 64-byte boundary on.</summary>
    internal ReadOnlySpan<short> OutputWeights => _outputWeights.AsSpan(_outputWeightsAt, 2 * Hidden);

    // Pinned (Aligned), each holding its numbers from ...At on.
    private readonly short[] _featureWeights, _outputWeights;
    private readonly int _featureWeightsAt, _outputWeightsAt;

    /// <summary>The file's version: 1, or 2 for a net with small layers.</summary>
    public int FileVersion => Layers.Length == 0 ? Version : DeepVersion;

    /// <summary>Version 2's small layers, in order (none in version 1).</summary>
    internal Layer[] Layers { get; } = [];

    /// <summary>Version 2's output: one row over the last layer's outputs (null in version 1).</summary>
    internal Layer? Output { get; }

    /// <summary>
    /// A small layer of a version 2 net, or its output (one row): int16 weights, one row of inputs per output,
    /// each row from a 64-byte boundary when its length allows; int32 biases; the power-of-two scale Q. Beside the
    /// rows, the same weights by pairs of inputs for the SIMD pass (<see cref="LayerByPairs"/>).
    /// </summary>
    internal sealed class Layer
    {
        public int Inputs { get; }
        public int Outputs { get; }
        public int Q { get; }
        public int Shift { get; }
        public int[] Biases { get; }

        /// <summary>The inputs rounded up to even and the outputs to a multiple of 16: the pairs' sizes.</summary>
        public int PaddedInputs { get; }
        public int PaddedOutputs { get; }

        /// <summary>[PaddedOutputs]: each bias plus Q / 2, the sum a SIMD lane starts from (0 for the padding).</summary>
        public int[] StartingSums { get; }

        private readonly short[] _weights, _pairs;
        private readonly int _weightsAt, _pairsAt;

        public Layer(int inputs, int outputs, int q, ReadOnlySpan<short> weights, int[] biases)
        {
            Inputs = inputs;
            Outputs = outputs;
            Q = q;
            Shift = BitOperations.Log2((uint)q);
            _weights = Aligned.Allocate(inputs * outputs, out _weightsAt);
            weights.CopyTo(_weights.AsSpan(_weightsAt));
            Biases = biases;

            PaddedInputs = (inputs + 1) & ~1;
            PaddedOutputs = (outputs + 15) & ~15;
            _pairs = Aligned.Allocate(PaddedInputs * PaddedOutputs, out _pairsAt);
            var pairs = _pairs.AsSpan(_pairsAt);
            for (int j = 0; j < outputs; j++)
                for (int i = 0; i < inputs; i++)
                    pairs[(i / 2 * PaddedOutputs + j) * 2 + (i & 1)] = weights[j * inputs + i];
            StartingSums = new int[PaddedOutputs];
            for (int j = 0; j < outputs; j++)
                StartingSums[j] = biases[j] + q / 2;
        }

        public ReadOnlySpan<short> Weights => _weights.AsSpan(_weightsAt, Inputs * Outputs);
        public ReadOnlySpan<short> Row(int output) => _weights.AsSpan(_weightsAt + output * Inputs, Inputs);

        /// <summary>
        /// [PaddedInputs / 2][PaddedOutputs][2]: for each pair of inputs, every output's two weights side by side, so a
        /// MultiplyAddAdjacent of the pair broadcast against 16 (or 8) of them adds the pair to 16 (or 8) outputs' sums.
        /// Each pair's block is a whole number of 64-byte lines from a boundary; the padding is 0.
        /// </summary>
        public ReadOnlySpan<short> Pairs => _pairs.AsSpan(_pairsAt, PaddedInputs * PaddedOutputs);
    }

    private Network(int hidden, int qa, int qb, int scale, ReadOnlySpan<short> featureWeights, short[] featureBiases,
                    ReadOnlySpan<short> outputWeights, int outputBias, string sha256)
    {
        Hidden = hidden;
        QA = qa;
        QB = qb;
        Scale = scale;
        _featureWeights = Aligned.Allocate(featureWeights.Length, out _featureWeightsAt);
        featureWeights.CopyTo(_featureWeights.AsSpan(_featureWeightsAt));
        FeatureBiases = featureBiases;
        _outputWeights = Aligned.Allocate(outputWeights.Length, out _outputWeightsAt);
        outputWeights.CopyTo(_outputWeights.AsSpan(_outputWeightsAt));
        OutputBias = outputBias;
        Sha256 = sha256;
    }

    /// <summary>Version 2: the feature layer, then small layers and the output (QB and the output weights unused).</summary>
    private Network(int hidden, int qa, int scale, ReadOnlySpan<short> featureWeights, short[] featureBiases,
                    Layer[] layers, Layer output, string sha256)
        : this(hidden, qa, 0, scale, featureWeights, featureBiases, ReadOnlySpan<short>.Empty, 0, sha256)
    {
        Layers = layers;
        Output = output;
    }

    public override string ToString() => Layers.Length == 0
        ? $"768 -> {Hidden} x2 -> 1, sha256 {Sha256[..12]}"
        : $"768 -> {Hidden} x2 -> {string.Join(" -> ", Layers.Select(l => l.Outputs))} -> 1, sha256 {Sha256[..12]}";

    /// <summary>
    /// The input for <paramref name="piece"/> on <paramref name="square"/> (a1 = 0) as
    /// <paramref name="perspective"/> (0 white, 1 black) sees it: its own pieces first, and black's
    /// board flipped top to bottom. The contract with the trainer (NNUE.md).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Feature(Piece piece, int square, int perspective) =>
        (((int)piece.Colour == perspective ? 0 : 6) + (int)piece.Type - 1) * 64 + (perspective == 0 ? square : square ^ 56);

    // ---- the file ----

    /// <summary>
    /// Does the file start with the .nnue magic? (EvalFile takes either kind of file.) An empty path is
    /// no network; a missing file throws FileNotFoundException, for the caller to report.
    /// </summary>
    public static bool IsNetworkFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        Span<byte> start = stackalloc byte[8];
        using var file = File.OpenRead(path);
        return file.ReadAtLeast(start, 8, throwOnEndOfStream: false) == 8 && start.SequenceEqual(Magic);
    }

    public static Network Load(string path) => Read(File.ReadAllBytes(path), path);

    /// <summary>A network from a .nnue file's bytes, refused (FormatException) unless every check holds.</summary>
    public static Network Read(ReadOnlySpan<byte> bytes, string name = "network")
    {
        if (bytes.Length < Magic.Length || !bytes[..Magic.Length].SequenceEqual(Magic))
            throw new FormatException($"{name}: not a .nnue file (it doesn't start with CBNNUE01).");
        if (bytes.Length < HeaderBytes + HashBytes)
            throw new FormatException($"{name}: {bytes.Length} bytes is too short for a .nnue file.");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (version == DeepVersion)
            return ReadDeep(bytes, name);
        uint inputs = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        uint hidden = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        int qa = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        int qb = BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]);
        int scale = BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]);
        if (version != Version)
            throw new FormatException($"{name}: version {version}; this engine reads versions {Version} and {DeepVersion}.");
        if (inputs != Inputs)
            throw new FormatException($"{name}: {inputs} inputs; this engine's are the {Inputs} piece-square ones.");
        if (hidden is < 16 or > MaxHidden || hidden % 16 != 0)
            throw new FormatException($"{name}: hidden size {hidden}; it must be a multiple of 16 from 16 to {MaxHidden}.");
        if (qa is < 1 or > 255 || qb < 1 || scale < 1)
            throw new FormatException($"{name}: QA {qa}, QB {qb}, scale {scale}; QA must be 1-255, the others positive.");
        int h = (int)hidden;
        long expected = HeaderBytes + 2L * (Inputs * h + h + 2 * h) + 4 + HashBytes;
        if (bytes.Length != expected)
            throw new FormatException($"{name}: {bytes.Length:N0} bytes; a hidden size of {h} makes {expected:N0}.");
        var body = bytes[..^HashBytes];
        byte[] hash = SHA256.HashData(body);
        if (!bytes[^HashBytes..].SequenceEqual(hash))
            throw new FormatException($"{name}: the SHA-256 at the end doesn't match the contents (damaged or cut).");

        int at = HeaderBytes;
        short[] ReadShorts(ReadOnlySpan<byte> source, int count)
        {
            var values = new short[count];
            for (int i = 0; i < count; i++)
                values[i] = BinaryPrimitives.ReadInt16LittleEndian(source[(at + 2 * i)..]);
            at += 2 * count;
            return values;
        }
        var featureWeights = ReadShorts(bytes, Inputs * h);
        var featureBiases = ReadShorts(bytes, h);
        var outputWeights = ReadShorts(bytes, 2 * h);
        int outputBias = BinaryPrimitives.ReadInt32LittleEndian(bytes[at..]);

        var network = new Network(h, qa, qb, scale, featureWeights, featureBiases, outputWeights, outputBias,
                                  Convert.ToHexString(hash).ToLowerInvariant());
        network.CheckRanges(name);
        return network;
    }

    /// <summary>Version 2 (the class's summary has the layout), with every check <see cref="Read"/> makes and the layers' own.</summary>
    private static Network ReadDeep(ReadOnlySpan<byte> bytes, string name)
    {
        uint inputs = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        uint hidden = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        int qa = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        int scale = BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]);
        if (inputs != Inputs)
            throw new FormatException($"{name}: {inputs} inputs; this engine's are the {Inputs} piece-square ones.");
        if (hidden is < 16 or > MaxHidden || hidden % 16 != 0)
            throw new FormatException($"{name}: hidden size {hidden}; it must be a multiple of 16 from 16 to {MaxHidden}.");
        if (qa is < 1 or > 255 || scale < 1)
            throw new FormatException($"{name}: QA {qa}, scale {scale}; QA must be 1-255, the scale positive.");
        if (count is < 1 or > MaxLayers)
            throw new FormatException($"{name}: {count} small layers; version {DeepVersion} has 1 to {MaxLayers}.");
        int layerCount = (int)count;
        if (bytes.Length < HeaderBytes + 4 * layerCount + HashBytes)
            throw new FormatException($"{name}: {bytes.Length} bytes is too short for a .nnue file.");
        var sizes = new int[layerCount];
        for (int k = 0; k < layerCount; k++)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(HeaderBytes + 4 * k)..]);
            if (size is < 1 or > MaxLayerSize)
                throw new FormatException($"{name}: layer {k + 1} has {size} numbers; a layer has 1 to {MaxLayerSize}.");
            sizes[k] = (int)size;
        }
        int h = (int)hidden;
        long expected = HeaderBytes + 4L * layerCount + 2L * (Inputs * h + h) + HashBytes;
        for (int k = 0, from = 2 * h; k < layerCount; from = sizes[k], k++)
            expected += 4 + 2L * sizes[k] * from + 4L * sizes[k];
        expected += 4 + 2L * sizes[^1] + 4;
        if (bytes.Length != expected)
            throw new FormatException($"{name}: {bytes.Length:N0} bytes; hidden {h} and layers {string.Join("/", sizes)} make {expected:N0}.");
        byte[] hash = SHA256.HashData(bytes[..^HashBytes]);
        if (!bytes[^HashBytes..].SequenceEqual(hash))
            throw new FormatException($"{name}: the SHA-256 at the end doesn't match the contents (damaged or cut).");

        int at = HeaderBytes + 4 * layerCount;
        short[] ReadShorts(ReadOnlySpan<byte> source, int n)
        {
            var values = new short[n];
            for (int i = 0; i < n; i++)
                values[i] = BinaryPrimitives.ReadInt16LittleEndian(source[(at + 2 * i)..]);
            at += 2 * n;
            return values;
        }
        int[] ReadInts(ReadOnlySpan<byte> source, int n)
        {
            var values = new int[n];
            for (int i = 0; i < n; i++)
                values[i] = BinaryPrimitives.ReadInt32LittleEndian(source[(at + 4 * i)..]);
            at += 4 * n;
            return values;
        }
        Layer ReadLayer(ReadOnlySpan<byte> source, int from, int to, string what)
        {
            int q = ReadInts(source, 1)[0];
            if (q < 1 || q > MaxLayerQ || (q & (q - 1)) != 0)
                throw new FormatException($"{name}: {what}'s Q is {q}; it must be a power of two from 1 to {MaxLayerQ}.");
            var weights = ReadShorts(source, from * to);
            return new Layer(from, to, q, weights, ReadInts(source, to));
        }
        var featureWeights = ReadShorts(bytes, Inputs * h);
        var featureBiases = ReadShorts(bytes, h);
        var layers = new Layer[layerCount];
        for (int k = 0, from = 2 * h; k < layerCount; from = sizes[k], k++)
            layers[k] = ReadLayer(bytes, from, sizes[k], $"layer {k + 1}");
        var output = ReadLayer(bytes, sizes[^1], 1, "the output");

        var network = new Network(h, qa, scale, featureWeights, featureBiases, layers, output,
                                  Convert.ToHexString(hash).ToLowerInvariant());
        network.CheckFeatureRanges(name);
        network.CheckLayerRanges(name);
        return network;
    }

    /// <summary>
    /// Version 2's sums stay in int32 at any inputs: for every row, QA x the sum of |weight| plus |bias|, plus Q / 2 for a
    /// small layer (its rounding), at most int.MaxValue. The trainer's layer_q picks each Q to keep it so.
    /// </summary>
    private void CheckLayerRanges(string name)
    {
        foreach (var (layer, what, half) in Layers.Select((l, k) => (l, $"layer {k + 1}", l.Q / 2)).Append((Output!, "the output", 0)))
            for (int j = 0; j < layer.Outputs; j++)
            {
                long most = Math.Abs((long)layer.Biases[j]) + half;
                foreach (short w in layer.Row(j))
                    most += (long)QA * Math.Abs((int)w);
                if (most > int.MaxValue)
                    throw new FormatException($"{name}: {what}'s row {j} can reach {most:N0}, past 32 bits.");
            }
    }

    /// <summary>
    /// What the int16 arithmetic needs: an output weight times QA fits in 16 bits (the SIMD pass's
    /// (v x w) step), and no accumulator can overflow on a real board (bias plus the 32 largest weights
    /// of one sign, per hidden unit: the trainer's export makes the same check).
    /// </summary>
    private void CheckRanges(string name)
    {
        foreach (short w in OutputWeights)
            if (Math.Abs(w) * QA > short.MaxValue)
                throw new FormatException($"{name}: output weight {w} times QA {QA} doesn't fit in 16 bits.");
        CheckFeatureRanges(name);
    }

    /// <summary>No accumulator can overflow on a real board (both versions).</summary>
    private void CheckFeatureRanges(string name)
    {
        var weights = FeatureWeights;
        var column = new int[Inputs];
        for (int j = 0; j < Hidden; j++)
        {
            for (int f = 0; f < Inputs; f++)
                column[f] = weights[f * Hidden + j];
            Array.Sort(column);
            long low = FeatureBiases[j], high = FeatureBiases[j];
            for (int k = 0; k < 32; k++)
            {
                low += Math.Min(0, column[k]);
                high += Math.Max(0, column[Inputs - 1 - k]);
            }
            if (low < short.MinValue || high > short.MaxValue)
                throw new FormatException($"{name}: hidden unit {j} can reach {low}..{high} on a full board, past 16 bits.");
        }
    }

    /// <summary>The .nnue file's bytes, checksum included.</summary>
    public byte[] ToBytes()
    {
        if (Layers.Length > 0)
            return ToBytesDeep();
        int h = Hidden;
        var bytes = new byte[HeaderBytes + 2 * (Inputs * h + h + 2 * h) + 4 + HashBytes];
        var span = bytes.AsSpan();
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], Inputs);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)h);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], QA);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], QB);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], Scale);
        int at = Write(span, HeaderBytes, FeatureWeights);
        at = Write(span, at, FeatureBiases);
        at = Write(span, at, OutputWeights);
        BinaryPrimitives.WriteInt32LittleEndian(span[at..], OutputBias);
        SHA256.HashData(span[..^HashBytes], span[^HashBytes..]);
        return bytes;

        static int Write(Span<byte> span, int at, ReadOnlySpan<short> values)
        {
            foreach (short value in values)
            {
                BinaryPrimitives.WriteInt16LittleEndian(span[at..], value);
                at += 2;
            }
            return at;
        }
    }

    /// <summary>Version 2's bytes.</summary>
    private byte[] ToBytesDeep()
    {
        var bytes = new List<byte>(Magic.ToArray());
        void U32(uint v) { Span<byte> x = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(x, v); bytes.AddRange(x); }
        void I32(int v) { Span<byte> x = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(x, v); bytes.AddRange(x); }
        void I16s(ReadOnlySpan<short> values)
        {
            Span<byte> x = stackalloc byte[2];
            foreach (short v in values)
            {
                BinaryPrimitives.WriteInt16LittleEndian(x, v);
                bytes.AddRange(x);
            }
        }
        U32(DeepVersion);
        U32(Inputs);
        U32((uint)Hidden);
        I32(QA);
        I32(Scale);
        U32((uint)Layers.Length);
        foreach (var layer in Layers)
            U32((uint)layer.Outputs);
        I16s(FeatureWeights);
        I16s(FeatureBiases);
        foreach (var layer in Layers.Append(Output!))
        {
            I32(layer.Q);
            I16s(layer.Weights);
            foreach (int b in layer.Biases)
                I32(b);
        }
        bytes.AddRange(SHA256.HashData(bytes.ToArray()));
        return bytes.ToArray();
    }

    public void Save(string path) => File.WriteAllBytes(path, ToBytes());

    /// <summary>
    /// Random weights, for tests and speed measurements until a trained net exists: feature weights in
    /// +-<paramref name="featureRange"/>, biases in [0, 2 x featureRange), output weights in
    /// +-<paramref name="outputRange"/>, output bias 0. The same seed gives the same net.
    /// </summary>
    public static Network Random(int hidden, int seed, int featureRange = 64, int outputRange = 127)
    {
        var rng = new Random(seed);
        var featureWeights = new short[Inputs * hidden];
        for (int i = 0; i < featureWeights.Length; i++)
            featureWeights[i] = (short)rng.Next(-featureRange, featureRange + 1);
        var featureBiases = new short[hidden];
        for (int i = 0; i < hidden; i++)
            featureBiases[i] = (short)rng.Next(0, 2 * featureRange);
        var outputWeights = new short[2 * hidden];
        for (int i = 0; i < outputWeights.Length; i++)
            outputWeights[i] = (short)rng.Next(-outputRange, outputRange + 1);
        var draft = new Network(hidden, 255, 64, 400, featureWeights, featureBiases, outputWeights, 0, "");
        return Read(draft.ToBytes(), $"random net {hidden}/{seed}");   // the same checks as a file, and its checksum
    }

    /// <summary>
    /// A random version 2 net: <see cref="Random"/>'s feature layer, then layers of <paramref name="sizes"/> and the
    /// output, weights in +-<paramref name="weightRange"/> at scale <paramref name="q"/>, biases spread so that every
    /// layer's numbers land on both sides of its clamp. The same seed gives the same net.
    /// </summary>
    public static Network RandomDeep(int hidden, int[] sizes, int seed, int featureRange = 64, int weightRange = 64, int q = 64)
    {
        var rng = new Random(seed);
        var featureWeights = new short[Inputs * hidden];
        for (int i = 0; i < featureWeights.Length; i++)
            featureWeights[i] = (short)rng.Next(-featureRange, featureRange + 1);
        var featureBiases = new short[hidden];
        for (int i = 0; i < hidden; i++)
            featureBiases[i] = (short)rng.Next(0, 2 * featureRange);
        Layer Random(int from, int to)
        {
            var weights = new short[from * to];
            for (int i = 0; i < weights.Length; i++)
                weights[i] = (short)rng.Next(-weightRange, weightRange + 1);
            var biases = new int[to];
            for (int j = 0; j < to; j++)
                biases[j] = rng.Next(-255 * q, 255 * q);
            return new Layer(from, to, q, weights, biases);
        }
        var layers = new Layer[sizes.Length];
        for (int k = 0, from = 2 * hidden; k < sizes.Length; from = sizes[k], k++)
            layers[k] = Random(from, sizes[k]);
        var draft = new Network(hidden, 255, 400, featureWeights, featureBiases, layers, Random(sizes[^1], 1), "");
        return Read(draft.ToBytes(), $"random deep net {hidden}/{string.Join("-", sizes)}/{seed}");
    }

    /// <summary>
    /// A network that computes the numbers' material and piece-square values, middlegame and endgame
    /// averaged, for the side to move (no bishop pair, no phase). Each hidden unit j holds 127 + d_j for the
    /// side to move and 127 - d_j for the other, d_j being the difference over 32 (dithered by j, so the
    /// units' roundings average out); (127 + d)^2 - (127 - d)^2 = 508 d, so the output is linear in the
    /// difference while it stays within about +-4,000. Its speed is any network's of its size (the arithmetic
    /// doesn't depend on the weights), on a search tree shaped like a real evaluation's, which random weights
    /// don't give; and it checks that the inputs mean what they should. Not for play.
    /// </summary>
    public static Network FromPieceSquares(EvalParams p, int hidden = 256)
    {
        const int bias = 127, step = 32, qa = 255, scale = 400;
        // score = sum / QA x scale / (QA x QB) with sum = 4 x bias x (the sum of d_j) and the sum of d_j ~ hidden x diff / step.
        int qb = (int)Math.Round(4.0 * bias * hidden * scale / ((double)qa * qa * step));
        var featureWeights = new short[Inputs * hidden];
        for (int type = 1; type <= 6; type++)
            for (int square = 0; square < 64; square++)   // as the piece's own side sees the board: white's way up
            {
                int table = square ^ 56;   // (the numbers are written rank 8 first)
                double value = (p.Mg[type][table] + p.Eg[type][table]) / 2.0;
                for (int j = 0; j < hidden; j++)
                {
                    var own = (short)Math.Floor(value / step + (j + 0.5) / hidden);
                    featureWeights[((type - 1) * 64 + square) * hidden + j] = own;               // ours, where we see it
                    featureWeights[((6 + type - 1) * 64 + (square ^ 56)) * hidden + j] = (short)-own;   // theirs, flipped to our view
                }
            }
        var featureBiases = Enumerable.Repeat((short)bias, hidden).ToArray();
        var outputWeights = Enumerable.Range(0, 2 * hidden).Select(i => (short)(i < hidden ? 1 : -1)).ToArray();
        var draft = new Network(hidden, qa, Math.Max(1, qb), scale, featureWeights, featureBiases, outputWeights, 0, "");
        return Read(draft.ToBytes(), $"piece-square net {hidden}");
    }

    // ---- accumulators from scratch ----

    /// <summary>Both perspectives' accumulators for the board, from scratch (SIMD where the column adds have it).</summary>
    internal void Refresh(Position position, Span<short> white, Span<short> black)
    {
        white = white[..Hidden];
        black = black[..Hidden];
        FeatureBiases.CopyTo(white);
        FeatureBiases.CopyTo(black);
        var weights = FeatureWeights;
        for (int square = 0; square < 64; square++)
        {
            var piece = position[square];
            if (piece.IsEmpty)
                continue;
            Kernels.Add(white, weights.Slice(Feature(piece, square, 0) * Hidden, Hidden));
            Kernels.Add(black, weights.Slice(Feature(piece, square, 1) * Hidden, Hidden));
        }
    }

    /// <summary>The same as <see cref="Refresh"/>, one number at a time: the tests' reference.</summary>
    internal void RefreshScalar(Position position, Span<short> white, Span<short> black)
    {
        var weights = FeatureWeights;
        for (int i = 0; i < Hidden; i++)
            white[i] = black[i] = FeatureBiases[i];
        for (int square = 0; square < 64; square++)
        {
            var piece = position[square];
            if (piece.IsEmpty)
                continue;
            int wo = Feature(piece, square, 0) * Hidden, bo = Feature(piece, square, 1) * Hidden;
            for (int i = 0; i < Hidden; i++)
            {
                white[i] = (short)(white[i] + weights[wo + i]);
                black[i] = (short)(black[i] + weights[bo + i]);
            }
        }
    }

    /// <summary>The score for the side to move of a position that carries no accumulators: a refresh, then the pass.</summary>
    public int Evaluate(Position position)
    {
        var white = new short[Hidden];
        var black = new short[Hidden];
        Refresh(position, white, black);
        return position.SideToMove == Colour.White ? Forward(white, black) : Forward(black, white);
    }

    // ---- the forward pass ----

    /// <summary>The score for the side to move from its accumulator and the other side's, at <see cref="Kernels.Width"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Forward(ReadOnlySpan<short> us, ReadOnlySpan<short> them) => Layers.Length != 0
        ? ForwardDeep(Kernels.Width, us, them)
        : Kernels.Width switch
        {
            512 => Forward512(us, them),
            256 => Forward256(us, them),
            _ => ForwardScalar(us, them),
        };

    // ---- version 2's pass ----

    /// <summary>
    /// Version 2's pass at <paramref name="width"/> bits (0: one number at a time): the activations as int16 (0 to QA);
    /// at 256 or 512 bits each small layer by pairs of inputs (<see cref="LayerByPairs"/>), at 0 each row a dot
    /// product; int32 sums throughout (the loader's row check keeps every one there), then a shift for Q, which equals
    /// the truncating division once clamped (a negative sum clamps to 0 either way); the output a dot product.
    /// <see cref="ForwardDeepScalar"/> is the definition it must equal.
    /// </summary>
    internal int ForwardDeep(int width, ReadOnlySpan<short> us, ReadOnlySpan<short> them)
    {
        CheckAccumulators(us, them);
        int h = Hidden;
        var numbers = t_numbers ??= new short[2 * MaxHidden + 2 * MaxLayerSize];
        var x = numbers.AsSpan(0, 2 * h);
        var a = numbers.AsSpan(2 * MaxHidden, MaxLayerSize);
        var b = numbers.AsSpan(2 * MaxHidden + MaxLayerSize, MaxLayerSize);
        Activate(width, us[..h], x[..h]);
        Activate(width, them[..h], x[h..]);
        ReadOnlySpan<short> input = x;
        for (int k = 0; k < Layers.Length; k++)
        {
            var layer = Layers[k];
            var outputs = k % 2 == 0 ? a : b;
            if (width >= 256)
                LayerByPairs(width, input, layer, outputs);   // writes PaddedOutputs, the padding as 0
            else
            {
                int half = layer.Q >> 1;
                for (int j = 0; j < layer.Outputs; j++)
                {
                    int s = layer.Biases[j] + half + Dot(width, input, layer.Row(j));
                    outputs[j] = (short)Math.Clamp(s >> layer.Shift, 0, QA);
                }
                if ((layer.Outputs & 1) != 0)
                    outputs[layer.Outputs] = 0;   // the next layer's pairs read one past an odd count
            }
            input = outputs[..layer.Outputs];
        }
        return FinishDeep(Output!.Biases[0] + Dot(width, input, Output.Row(0)));
    }

    /// <summary>
    /// One small layer at 256 or 512 bits, the inputs taken two at a time and only where either is non-zero (82% of the
    /// first layer's 2H are zero on real positions, 2026-10-21): every output's sum in an int32 lane, from its bias +
    /// Q / 2, plus MultiplyAddAdjacent of the pair broadcast against the pair's weights for all outputs; then the shift
    /// and the clamp, written to <paramref name="outputs"/>[..PaddedOutputs]. The same integers as the rows' dot
    /// products in another order, so the same sums (int32 holds every partial sum). <paramref name="x"/> must hold
    /// PaddedInputs numbers (an odd count's last pair reads one past it: the caller's padding, which is 0).
    /// </summary>
    private void LayerByPairs(int width, ReadOnlySpan<short> x, Layer layer, Span<short> outputs)
    {
        int pairs = layer.PaddedInputs / 2, n = layer.PaddedOutputs;
        ref short xs = ref MemoryMarshal.GetReference(x);
        ref short weights = ref MemoryMarshal.GetReference(layer.Pairs);
        ref int starts = ref MemoryMarshal.GetArrayDataReference(layer.StartingSums);
        ref short to = ref MemoryMarshal.GetReference(outputs);
        if (outputs.Length < n)
            throw new ArgumentException($"room for {outputs.Length} outputs, {n} needed");
        if (n <= 32)
        {
            ByListedPairs(width, x, layer, outputs);   // every layer of the nets trained so far
            return;
        }
        if (width == 512)
        {
            Span<Vector512<int>> sums = stackalloc Vector512<int>[MaxLayerSize / 16];
            int vectors = n / 16;
            for (int v = 0; v < vectors; v++)
                sums[v] = Vector512.LoadUnsafe(ref starts, (nuint)(16 * v));
            int p = 0;
            for (; p + 16 <= pairs; p += 16)
            {
                var chunk = Vector512.LoadUnsafe(ref xs, (nuint)(2 * p)).AsInt32();
                ulong mask = (~Vector512.Equals(chunk, Vector512<int>.Zero)).ExtractMostSignificantBits();
                for (; mask != 0; mask &= mask - 1)
                    AddPair512(ref xs, ref weights, sums, p + BitOperations.TrailingZeroCount(mask), n, vectors);
            }
            for (; p < pairs; p++)
                if (Pair(ref xs, p) != 0)
                    AddPair512(ref xs, ref weights, sums, p, n, vectors);
            var zero = Vector512<int>.Zero;
            var qa = Vector512.Create(QA);
            for (int v = 0; v < vectors; v++)
            {
                var clamped = Vector512.Min(Vector512.Max(Vector512.ShiftRightArithmetic(sums[v], layer.Shift), zero), qa);
                Avx512F.ConvertToVector256Int16(clamped).StoreUnsafe(ref to, (nuint)(16 * v));
            }
        }
        else
        {
            Span<Vector256<int>> sums = stackalloc Vector256<int>[MaxLayerSize / 8];
            int vectors = n / 8;
            for (int v = 0; v < vectors; v++)
                sums[v] = Vector256.LoadUnsafe(ref starts, (nuint)(8 * v));
            int p = 0;
            for (; p + 8 <= pairs; p += 8)
            {
                var chunk = Vector256.LoadUnsafe(ref xs, (nuint)(2 * p)).AsInt32();
                uint mask = (~Vector256.Equals(chunk, Vector256<int>.Zero)).ExtractMostSignificantBits();
                for (; mask != 0; mask &= mask - 1)
                    AddPair256(ref xs, ref weights, sums, p + BitOperations.TrailingZeroCount(mask), n, vectors);
            }
            for (; p < pairs; p++)
                if (Pair(ref xs, p) != 0)
                    AddPair256(ref xs, ref weights, sums, p, n, vectors);
            var zero = Vector256<int>.Zero;
            var qa = Vector256.Create(QA);
            for (int v = 0; v < vectors; v += 2)
            {
                var low = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(sums[v], layer.Shift), zero), qa);
                var high = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(sums[v + 1], layer.Shift), zero), qa);
                Vector256.Narrow(low, high).StoreUnsafe(ref to, (nuint)(8 * v));
            }
        }
    }

    /// <summary>Inputs 2p and 2p + 1 as one int32: the first in the low half (little-endian), as MultiplyAddAdjacent pairs them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Pair(ref short x, int pair) =>
        Unsafe.ReadUnaligned<int>(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref x, 2 * pair)));

    /// <summary>One pair of inputs into every output's sum, 16 outputs a vector.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddPair512(ref short x, ref short weights, Span<Vector512<int>> sums, int pair, int outputs, int vectors)
    {
        var both = Vector512.Create(Pair(ref x, pair)).AsInt16();
        nuint at = (nuint)(pair * outputs * 2);
        for (int v = 0; v < vectors; v++)
            sums[v] += Avx512BW.MultiplyAddAdjacent(both, Vector512.LoadUnsafe(ref weights, at + (nuint)(32 * v)));
    }

    /// <summary>One pair of inputs into every output's sum, 8 outputs a vector.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddPair256(ref short x, ref short weights, Span<Vector256<int>> sums, int pair, int outputs, int vectors)
    {
        var both = Vector256.Create(Pair(ref x, pair)).AsInt16();
        nuint at = (nuint)(pair * outputs * 2);
        for (int v = 0; v < vectors; v++)
            sums[v] += Avx2.MultiplyAddAdjacent(both, Vector256.LoadUnsafe(ref weights, at + (nuint)(16 * v)));
    }

    /// <summary>For each byte, the positions of its set bits, 8 to a row (the rest 0): a list 8 at a time with no branch a bit.</summary>
    private static readonly ushort[] BitPositions = BuildBitPositions();

    private static ushort[] BuildBitPositions()
    {
        var table = new ushort[256 * 8];
        for (int bits = 0; bits < 256; bits++)
            for (int bit = 0, k = 0; bit < 8; bit++)
                if ((bits >> bit & 1) != 0)
                    table[bits * 8 + k++] = (ushort)bit;
        return table;
    }

    /// <summary>
    /// The pairs of <paramref name="x"/> (two int16 as one int32) that aren't zero, in order, into <paramref name="list"/>,
    /// which needs 8 to spare (each byte of a mask is written as 8 positions, of which its bit count are kept); returns
    /// how many. Walking a mask bit by bit instead mispredicts the loop's exit about once a chunk, which cost more than
    /// the pairs' arithmetic (2026-10-21).
    /// </summary>
    private static int NonZeroPairs(int width, ReadOnlySpan<short> x, int pairs, Span<ushort> list)
    {
        if (list.Length < pairs + 8)
            throw new ArgumentException($"room for {list.Length} pairs, {pairs + 8} needed");
        ref short xs = ref MemoryMarshal.GetReference(x);
        ref ushort into = ref MemoryMarshal.GetReference(list);
        ref ushort table = ref MemoryMarshal.GetArrayDataReference(BitPositions);
        int count = 0, p = 0;
        if (width == 512)
            for (; p + 16 <= pairs; p += 16)
            {
                uint mask = ~(uint)Vector512.Equals(Vector512.LoadUnsafe(ref xs, (nuint)(2 * p)).AsInt32(), Vector512<int>.Zero).ExtractMostSignificantBits() & 0xFFFF;
                count = Append(ref into, count, ref table, mask & 0xFF, p);
                count = Append(ref into, count, ref table, mask >> 8, p + 8);
            }
        for (; p + 8 <= pairs; p += 8)
        {
            uint mask = ~Vector256.Equals(Vector256.LoadUnsafe(ref xs, (nuint)(2 * p)).AsInt32(), Vector256<int>.Zero).ExtractMostSignificantBits() & 0xFF;
            count = Append(ref into, count, ref table, mask, p);
        }
        for (; p < pairs; p++)
            if (Pair(ref xs, p) != 0)
                Unsafe.Add(ref into, count++) = (ushort)p;
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Append(ref ushort list, int count, ref ushort table, uint bits, int at)
    {
        (Vector128.LoadUnsafe(ref table, (nuint)(bits * 8)) + Vector128.Create((ushort)at)).StoreUnsafe(ref list, (nuint)count);
        return count + BitOperations.PopCount(bits);
    }

    // Per-thread room for version 2's pass (a stackalloc would be zeroed on every call): the activations, two layers'
    // outputs, and the non-zero pairs' list. Nothing in the pass calls back into it on the same thread.
    [ThreadStatic] private static short[]? t_numbers;
    [ThreadStatic] private static ushort[]? t_pairs;

    /// <summary>Fills this thread's scratch with <paramref name="value"/>: the tests' way to show no pass leans on what the last one left.</summary>
    internal static void FillScratch(short value)
    {
        (t_numbers ??= new short[2 * MaxHidden + 2 * MaxLayerSize]).AsSpan().Fill(value);
        (t_pairs ??= new ushort[MaxHidden + 16]).AsSpan().Fill(unchecked((ushort)value));
    }

    /// <summary>
    /// A small layer of at most 32 outputs at 256 or 512 bits: the non-zero pairs listed first (<see cref="NonZeroPairs"/>),
    /// then each listed pair broadcast and added to every output's sum, two pairs at a time into separate registers;
    /// then the shift and the clamp into <paramref name="outputs"/>[..PaddedOutputs].
    /// </summary>
    private void ByListedPairs(int width, ReadOnlySpan<short> x, Layer layer, Span<short> outputs)
    {
        var list = t_pairs ??= new ushort[MaxHidden + 16];
        int count = NonZeroPairs(width, x, layer.PaddedInputs / 2, list);
        ref short xs = ref MemoryMarshal.GetReference(x);
        ref short weights = ref MemoryMarshal.GetReference(layer.Pairs);
        ref ushort at = ref MemoryMarshal.GetArrayDataReference(list);
        ref int starts = ref MemoryMarshal.GetArrayDataReference(layer.StartingSums);
        ref short to = ref MemoryMarshal.GetReference(outputs);
        int shift = layer.Shift;
        if (width == 512)
        {
            var zero = Vector512<int>.Zero;
            var qa = Vector512.Create(QA);
            if (layer.PaddedOutputs == 32)
            {
                Vector512<int> s0 = Vector512.LoadUnsafe(ref starts), s1 = Vector512.LoadUnsafe(ref starts, 16), t0 = zero, t1 = zero;
                int i = 0;
                for (; i + 2 <= count; i += 2)
                {
                    int p = Unsafe.Add(ref at, i), q = Unsafe.Add(ref at, i + 1);
                    var bp = Vector512.Create(Pair(ref xs, p)).AsInt16();
                    var bq = Vector512.Create(Pair(ref xs, q)).AsInt16();
                    s0 += Avx512BW.MultiplyAddAdjacent(bp, Vector512.LoadUnsafe(ref weights, (nuint)(p * 64)));
                    s1 += Avx512BW.MultiplyAddAdjacent(bp, Vector512.LoadUnsafe(ref weights, (nuint)(p * 64 + 32)));
                    t0 += Avx512BW.MultiplyAddAdjacent(bq, Vector512.LoadUnsafe(ref weights, (nuint)(q * 64)));
                    t1 += Avx512BW.MultiplyAddAdjacent(bq, Vector512.LoadUnsafe(ref weights, (nuint)(q * 64 + 32)));
                }
                if (i < count)
                {
                    int p = Unsafe.Add(ref at, i);
                    var bp = Vector512.Create(Pair(ref xs, p)).AsInt16();
                    s0 += Avx512BW.MultiplyAddAdjacent(bp, Vector512.LoadUnsafe(ref weights, (nuint)(p * 64)));
                    s1 += Avx512BW.MultiplyAddAdjacent(bp, Vector512.LoadUnsafe(ref weights, (nuint)(p * 64 + 32)));
                }
                Avx512F.ConvertToVector256Int16(Vector512.Min(Vector512.Max(Vector512.ShiftRightArithmetic(s0 + t0, shift), zero), qa)).StoreUnsafe(ref to);
                Avx512F.ConvertToVector256Int16(Vector512.Min(Vector512.Max(Vector512.ShiftRightArithmetic(s1 + t1, shift), zero), qa)).StoreUnsafe(ref to, 16);
            }
            else
            {
                Vector512<int> s0 = Vector512.LoadUnsafe(ref starts), t0 = zero;
                int i = 0;
                for (; i + 2 <= count; i += 2)
                {
                    int p = Unsafe.Add(ref at, i), q = Unsafe.Add(ref at, i + 1);
                    s0 += Avx512BW.MultiplyAddAdjacent(Vector512.Create(Pair(ref xs, p)).AsInt16(), Vector512.LoadUnsafe(ref weights, (nuint)(p * 32)));
                    t0 += Avx512BW.MultiplyAddAdjacent(Vector512.Create(Pair(ref xs, q)).AsInt16(), Vector512.LoadUnsafe(ref weights, (nuint)(q * 32)));
                }
                if (i < count)
                {
                    int p = Unsafe.Add(ref at, i);
                    s0 += Avx512BW.MultiplyAddAdjacent(Vector512.Create(Pair(ref xs, p)).AsInt16(), Vector512.LoadUnsafe(ref weights, (nuint)(p * 32)));
                }
                Avx512F.ConvertToVector256Int16(Vector512.Min(Vector512.Max(Vector512.ShiftRightArithmetic(s0 + t0, shift), zero), qa)).StoreUnsafe(ref to);
            }
        }
        else
        {
            var zero = Vector256<int>.Zero;
            var qa = Vector256.Create(QA);
            bool two = layer.PaddedOutputs == 32;
            int stride = layer.PaddedOutputs * 2;
            Vector256<int> s0 = Vector256.LoadUnsafe(ref starts), s1 = Vector256.LoadUnsafe(ref starts, 8);
            Vector256<int> s2 = two ? Vector256.LoadUnsafe(ref starts, 16) : zero, s3 = two ? Vector256.LoadUnsafe(ref starts, 24) : zero;
            for (int i = 0; i < count; i++)
            {
                int p = Unsafe.Add(ref at, i);
                var both = Vector256.Create(Pair(ref xs, p)).AsInt16();
                nuint w = (nuint)(p * stride);
                s0 += Avx2.MultiplyAddAdjacent(both, Vector256.LoadUnsafe(ref weights, w));
                s1 += Avx2.MultiplyAddAdjacent(both, Vector256.LoadUnsafe(ref weights, w + 16));
                if (two)
                {
                    s2 += Avx2.MultiplyAddAdjacent(both, Vector256.LoadUnsafe(ref weights, w + 32));
                    s3 += Avx2.MultiplyAddAdjacent(both, Vector256.LoadUnsafe(ref weights, w + 48));
                }
            }
            Vector256.Narrow(Out(s0), Out(s1)).StoreUnsafe(ref to);
            if (two)
                Vector256.Narrow(Out(s2), Out(s3)).StoreUnsafe(ref to, 16);

            Vector256<int> Out(Vector256<int> s) => Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(s, shift), zero), qa);
        }
    }

    /// <summary>
    /// x = (v x v + QA / 2) / QA for each accumulator number v clamped to [0, QA]. With QA 255, 16 at a time: v x v + 127
    /// stays below 65,536 as unsigned 16 bits, and (n x 32,897) >> 23 is n / 255 for every such n (checked for all of them).
    /// </summary>
    private void Activate(int width, ReadOnlySpan<short> accumulator, Span<short> x)
    {
        int n = accumulator.Length, i = 0;
        if (width == 512 && QA == 255)
        {
            ref short from = ref MemoryMarshal.GetReference(accumulator);
            ref short to = ref MemoryMarshal.GetReference(x);
            var zero = Vector512<short>.Zero;
            var qa = Vector512.Create((short)255);
            var half = Vector512.Create((ushort)127);
            var magic = Vector512.Create((ushort)32897);
            for (; i + 32 <= n; i += 32)
            {
                var v = Vector512.Min(Vector512.Max(Vector512.LoadUnsafe(ref from, (nuint)i), zero), qa).AsUInt16();
                var quotient = Vector512.ShiftRightLogical(Avx512BW.MultiplyHigh(v * v + half, magic), 7);
                quotient.AsInt16().StoreUnsafe(ref to, (nuint)i);
            }
        }
        if (width >= 256 && QA == 255)
        {
            ref short from = ref MemoryMarshal.GetReference(accumulator);
            ref short to = ref MemoryMarshal.GetReference(x);
            var zero = Vector256<short>.Zero;
            var qa = Vector256.Create((short)255);
            var half = Vector256.Create((ushort)127);
            var magic = Vector256.Create((ushort)32897);
            for (; i + 16 <= n; i += 16)
            {
                var v = Vector256.Min(Vector256.Max(Vector256.LoadUnsafe(ref from, (nuint)i), zero), qa).AsUInt16();
                var quotient = Vector256.ShiftRightLogical(Avx2.MultiplyHigh(v * v + half, magic), 7);
                quotient.AsInt16().StoreUnsafe(ref to, (nuint)i);
            }
        }
        for (; i < n; i++)
        {
            int v = Math.Clamp((int)accumulator[i], 0, QA);
            x[i] = (short)((v * v + QA / 2) / QA);
        }
    }

    /// <summary>The sum of x x w over two equal-length int16 rows, in int32, at <paramref name="width"/> bits then one at a time.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Dot(int width, ReadOnlySpan<short> x, ReadOnlySpan<short> w)
    {
        int n = x.Length, i = 0, sum = 0;
        if (w.Length != n)
            throw new ArgumentException($"a row of {w.Length} against {n} inputs");
        ref short xs = ref MemoryMarshal.GetReference(x);
        ref short ws = ref MemoryMarshal.GetReference(w);
        if (width == 512 && n >= 32)
        {
            var lanes = Vector512<int>.Zero;
            for (; i + 32 <= n; i += 32)
                lanes += Avx512BW.MultiplyAddAdjacent(Vector512.LoadUnsafe(ref xs, (nuint)i), Vector512.LoadUnsafe(ref ws, (nuint)i));
            sum = Vector512.Sum(lanes);
        }
        if (width >= 256 && i + 16 <= n)
        {
            var lanes = Vector256<int>.Zero;
            for (; i + 16 <= n; i += 16)
                lanes += Avx2.MultiplyAddAdjacent(Vector256.LoadUnsafe(ref xs, (nuint)i), Vector256.LoadUnsafe(ref ws, (nuint)i));
            sum += Vector256.Sum(lanes);
        }
        for (; i < n; i++)
            sum += x[i] * w[i];
        return sum;
    }

    /// <summary>Version 2's pass by its definition, one number at a time in 64 bits with truncating divisions: the reference.</summary>
    internal int ForwardDeepScalar(ReadOnlySpan<short> us, ReadOnlySpan<short> them)
    {
        CheckAccumulators(us, them);
        int h = Hidden;
        var x = new long[2 * h];
        for (int i = 0; i < h; i++)
        {
            long v = Math.Clamp((int)us[i], 0, QA), u = Math.Clamp((int)them[i], 0, QA);
            x[i] = (v * v + QA / 2) / QA;
            x[h + i] = (u * u + QA / 2) / QA;
        }
        foreach (var layer in Layers)
        {
            var next = new long[layer.Outputs];
            for (int j = 0; j < layer.Outputs; j++)
            {
                long s = layer.Biases[j];
                var row = layer.Row(j);
                for (int i = 0; i < layer.Inputs; i++)
                    s += x[i] * row[i];
                next[j] = Math.Clamp((s + layer.Q / 2) / layer.Q, 0, QA);
            }
            x = next;
        }
        long total = Output!.Biases[0];
        var last = Output.Row(0);
        for (int i = 0; i < Output.Inputs; i++)
            total += x[i] * last[i];
        return FinishDeep(total);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FinishDeep(long total)
    {
        long score = total * Scale / ((long)QA * Output!.Q);
        return (int)Math.Clamp(score, -MaxScore, MaxScore);
    }

    /// <summary>
    /// SCReLU 16 lanes at a time: v = clamp(a, 0, QA); (v x w) in 16 bits (|w| x QA fits, checked on
    /// load), then x v and pairs summed into int32 by <see cref="Avx2.MultiplyAddAdjacent(Vector256{short}, Vector256{short})"/>.
    /// A lane collects H / 8 products of at most 255 x 32,767 = 8,355,585, so it can't overflow for H up to
    /// 2048: 256 of them are 2,138,909,760, under 2^31 (2,056 is the last H that fits).
    /// </summary>
    internal int Forward256(ReadOnlySpan<short> us, ReadOnlySpan<short> them)
    {
        CheckAccumulators(us, them);
        ref short ow = ref MemoryMarshal.GetReference(OutputWeights);
        long sum = Half256(ref MemoryMarshal.GetReference(us), ref ow, 0)
                 + Half256(ref MemoryMarshal.GetReference(them), ref Unsafe.Add(ref ow, Hidden), 0);
        return Finish(sum);
    }

    /// <summary>
    /// The same 32 lanes at a time (AVX-512's <see cref="Avx512BW.MultiplyAddAdjacent(Vector512{short}, Vector512{short})"/>),
    /// the last 16 as <see cref="Forward256"/> does when H isn't a multiple of 32. A lane collects H / 16
    /// products, so it can't overflow for H up to 4096.
    /// </summary>
    internal int Forward512(ReadOnlySpan<short> us, ReadOnlySpan<short> them)
    {
        CheckAccumulators(us, them);
        ref short ow = ref MemoryMarshal.GetReference(OutputWeights);
        long sum = Half512(ref MemoryMarshal.GetReference(us), ref ow)
                 + Half512(ref MemoryMarshal.GetReference(them), ref Unsafe.Add(ref ow, Hidden));
        return Finish(sum);
    }

    private void CheckAccumulators(ReadOnlySpan<short> us, ReadOnlySpan<short> them)
    {
        if (us.Length < Hidden || them.Length < Hidden)
            throw new ArgumentOutOfRangeException(nameof(us), $"accumulators of {us.Length} and {them.Length} for a hidden size of {Hidden}");
    }

    /// <summary>One perspective's sum, 16 lanes at a time from <paramref name="from"/> to H.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long Half256(ref short accumulator, ref short weights, int from)
    {
        var zero = Vector256<short>.Zero;
        var qa = Vector256.Create((short)QA);
        var lanes = Vector256<int>.Zero;
        for (int i = from; i < Hidden; i += 16)
        {
            var v = Vector256.Min(Vector256.Max(Vector256.LoadUnsafe(ref accumulator, (nuint)i), zero), qa);
            var w = Vector256.LoadUnsafe(ref weights, (nuint)i);
            lanes += Avx2.MultiplyAddAdjacent(v * w, v);
        }
        var (low, high) = Vector256.Widen(lanes);
        return Vector256.Sum(low + high);
    }

    /// <summary>One perspective's sum, 32 lanes at a time, then any last 16.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long Half512(ref short accumulator, ref short weights)
    {
        var zero = Vector512<short>.Zero;
        var qa = Vector512.Create((short)QA);
        var lanes = Vector512<int>.Zero;
        int i = 0;
        for (; i + 32 <= Hidden; i += 32)
        {
            var v = Vector512.Min(Vector512.Max(Vector512.LoadUnsafe(ref accumulator, (nuint)i), zero), qa);
            var w = Vector512.LoadUnsafe(ref weights, (nuint)i);
            lanes += Avx512BW.MultiplyAddAdjacent(v * w, v);
        }
        var (low, high) = Vector512.Widen(lanes);
        long sum = Vector512.Sum(low + high);
        return i < Hidden ? sum + Half256(ref accumulator, ref weights, i) : sum;
    }

    /// <summary>The forward pass one number at a time, in 64 bits: the reference the SIMD passes must equal.</summary>
    internal int ForwardScalar(ReadOnlySpan<short> us, ReadOnlySpan<short> them)
    {
        var weights = OutputWeights;
        long sum = 0;
        for (int i = 0; i < Hidden; i++)
        {
            long v = Math.Clamp((int)us[i], 0, QA);
            sum += v * v * weights[i];
            long u = Math.Clamp((int)them[i], 0, QA);
            sum += u * u * weights[Hidden + i];
        }
        return Finish(sum);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Finish(long sum)
    {
        long score = (sum / QA + OutputBias) * Scale / ((long)QA * QB);
        return (int)Math.Clamp(score, -MaxScore, MaxScore);
    }
}
