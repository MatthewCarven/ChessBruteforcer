using System.Buffers.Binary;
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
///   u32       hidden H (a multiple of 16, 16 to 1024)
///   i32       QA (255), QB (64), scale (400)
///   int16     feature weights [768][H], feature-major: one feature's column of H is contiguous
///   int16     feature biases [H]
///   int16     output weights [2H]: the side to move's H, then the other side's
///   i32       output bias
///   32 bytes  SHA-256 of everything above
/// </code>
/// Score for the side to move, in centipawns: (sum / QA + output bias) x scale / (QA x QB), each
/// division truncating towards zero (C#'s), clamped to +-<see cref="MaxScore"/>.
/// </summary>
public sealed class Network
{
    public const int Version = 1;
    public const int Inputs = 768;
    public const int MaxHidden = 1024;

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

    public override string ToString() => $"768 -> {Hidden} x2 -> 1, sha256 {Sha256[..12]}";

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
        uint inputs = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        uint hidden = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        int qa = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        int qb = BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]);
        int scale = BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]);
        if (version != Version)
            throw new FormatException($"{name}: version {version}; this engine reads version {Version}.");
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
    internal int Forward(ReadOnlySpan<short> us, ReadOnlySpan<short> them) => Kernels.Width switch
    {
        512 => Forward512(us, them),
        256 => Forward256(us, them),
        _ => ForwardScalar(us, them),
    };

    /// <summary>
    /// SCReLU 16 lanes at a time: v = clamp(a, 0, QA); (v x w) in 16 bits (|w| x QA fits, checked on
    /// load), then x v and pairs summed into int32 by <see cref="Avx2.MultiplyAddAdjacent(Vector256{short}, Vector256{short})"/>.
    /// A lane collects H / 8 products of at most 255 x 32,767, so it can't overflow for H up to 1024.
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
