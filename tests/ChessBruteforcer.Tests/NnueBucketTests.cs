using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Engine.Nnue;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Tests;

/// <summary>
/// Version 3 networks, output buckets (NNUE.md, session 7): a board of n pieces takes bucket (n - 1) x K / 32, and every
/// evaluation path picks it; every SIMD width equals the scalar definition in every bucket; a position and its
/// colour-mirror score the same; move by move equals a fresh evaluation as pieces come off; the file round-trips and the
/// loader refuses what it can't run; the search and UCI run on one. The feature layer and its accumulators are version
/// 1's, which NnueTests checks.
/// </summary>
public class NnueBucketTests
{
    private static string DataPath(string name) => Path.Combine(AppContext.BaseDirectory, "Data", name);

    /// <summary>0 (one number at a time) and the SIMD widths this CPU runs.</summary>
    private static readonly int[] Widths = new[] { 0, 256, 512 }.Where(width => width <= Kernels.Widest).ToArray();

    /// <summary>A width's pass by the way the engine reaches it (the dispatch, which on this CPU otherwise only ever takes the widest).</summary>
    private static int ForwardAt(int width, Network network, ReadOnlySpan<short> us, ReadOnlySpan<short> them, int bucket) =>
        network.Forward(width, us, them, bucket);

    /// <summary>The spec's bucket, written out here apart from the engine's table.</summary>
    private static int BucketOf(int pieces, int buckets) => (pieces - 1) * buckets / 32;

    /// <summary>
    /// A board of exactly <paramref name="pieces"/> pieces, kings included (2 to 32): kings apart, the rest of random kinds
    /// and colours on random squares, pawns off the first and last ranks, either side to move. Not always a legal
    /// position, which no evaluation needs.
    /// </summary>
    private static Position Board(Random rng, int pieces)
    {
        var position = Position.Empty(rng.Next(2) == 0 ? Colour.White : Colour.Black);
        int white = rng.Next(64), black;
        do black = rng.Next(64);
        while (Math.Max(Math.Abs(white % 8 - black % 8), Math.Abs(white / 8 - black / 8)) <= 1);
        position.SetPiece(white, new Piece(PieceType.King, Colour.White));
        position.SetPiece(black, new Piece(PieceType.King, Colour.Black));
        for (int placed = 2; placed < pieces;)
        {
            int square = rng.Next(64);
            var type = (PieceType)rng.Next((int)PieceType.Pawn, (int)PieceType.King);
            if (!position[square].IsEmpty || (type == PieceType.Pawn && square / 8 is 0 or 7))
                continue;
            position.SetPiece(square, new Piece(type, rng.Next(2) == 0 ? Colour.White : Colour.Black));
            placed++;
        }
        return Position.FromFen(position.ToFen());
    }

    private static IEnumerable<Position> RandomGamePositions(int count, int seed)
    {
        var rng = new Random(seed);
        var starts = new[] { Fen.StartPosition, MoveGeneratorTests.Kiwipete, MoveGeneratorTests.Promotions };
        for (int made = 0; made < count; made++)
        {
            var position = Position.FromFen(starts[made % starts.Length]);
            int length = rng.Next(0, 120);
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

    // ---- the bucket ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(16)]
    public void ABoardOfNPiecesTakesBucketNMinusOneTimesKOver32(int buckets)
    {
        var network = Network.RandomBuckets(16, buckets, seed: buckets);
        Assert.Equal(buckets, network.Buckets);
        for (int n = 2; n <= 32; n++)
            Assert.Equal(BucketOf(n, buckets), network.Bucket(n));
        Assert.Equal(0, network.Bucket(2));
        Assert.Equal(buckets - 1, network.Bucket(32));
        // No real board has fewer than 2 or more than 32; the table still answers, at the ends.
        Assert.Equal((0, 0), (network.Bucket(0), network.Bucket(1)));
        for (int n = 33; n <= 64; n++)
            Assert.Equal(buckets - 1, network.Bucket(n));
    }

    [Fact]
    public void EightBucketsAreStockfishsFourPiecesEach()
    {
        var network = Network.RandomBuckets(16, 8, seed: 1);
        for (int n = 2; n <= 32; n++)
            Assert.Equal((n - 1) / 4, network.Bucket(n));
        Assert.Equal((0, 0, 1, 1, 7), (network.Bucket(2), network.Bucket(4), network.Bucket(5), network.Bucket(6), network.Bucket(32)));
        // Versions 1 and 2 have the one row.
        Assert.All(Enumerable.Range(0, 65), n => Assert.Equal(0, Network.Random(16, 1).Bucket(n)));
        Assert.Equal(1, Network.Random(16, 1).Buckets);
        Assert.Equal(1, Network.RandomDeep(16, [8], seed: 1).Buckets);
    }

    /// <summary>A version 3 net that says 1000 + 100 b for the side to move in bucket b, whatever the board: only the biases.</summary>
    private static byte[] BucketNumberNet(int buckets) =>
        CraftBuckets(16, buckets, f: _ => 0, b: _ => 0, o: (_, _) => 0, bias: bucket => (1000 + 100 * bucket) * 255 * 64 / 400);

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(3)]
    public void EveryEvaluationPicksTheBucketByThePieceCount(int buckets)
    {
        // The score names the bucket, by every path that evaluates: kept accumulators, a fresh refresh, and the search's
        // own leaves (a search to depth 0 is its capture search, which evaluates the root first).
        var network = Network.Read(BucketNumberNet(buckets));
        var rng = new Random(buckets);
        for (int n = 2; n <= 32; n++)
            for (int trial = 0; trial < 4; trial++)
            {
                var board = Board(rng, n);
                Assert.Equal(n, BitOperations.PopCount(board.Occupied));
                int expected = 1000 + 100 * BucketOf(n, buckets);
                Assert.Equal(expected, Evaluation.Evaluate(Position.FromFen(board.ToFen(), network)));
                Assert.Equal(expected, network.Evaluate(board));
            }

        // As pieces come off in a game, so the score follows.
        var game = Position.FromFen(MoveGeneratorTests.Kiwipete, network);
        var seen = new HashSet<int>();
        for (int ply = 0; ply < 300; ply++)
        {
            int n = BitOperations.PopCount(game.Occupied);
            Assert.Equal(1000 + 100 * BucketOf(n, buckets), Evaluation.Evaluate(game));
            seen.Add(BucketOf(n, buckets));
            var moves = MoveGenerator.Legal(game);
            if (moves.Count == 0)
                break;
            var captures = moves.Where(move => move.IsCapture).ToList();
            game.MakeMove(captures.Count > 0 && ply % 3 == 0 ? captures[ply % captures.Count] : moves[ply * 7 % moves.Count]);
        }
        Assert.True(seen.Count >= Math.Min(buckets, 4), $"buckets seen: {string.Join(", ", seen.Order())}");
    }

    // ---- SIMD against the definition ----

    [Theory]
    [InlineData(16, 8)]
    [InlineData(32, 8)]
    [InlineData(48, 3)]       // 512 bits' 16-lane tail
    [InlineData(256, 8)]
    [InlineData(1024, 8)]     // h1024-3m's shape with Stockfish's buckets
    [InlineData(1024, 16)]
    [InlineData(1536, 8)]
    [InlineData(2048, 16)]    // MaxHidden, MaxBuckets
    public void EveryWidthEqualsTheDefinitionInEveryBucket(int hidden, int buckets)
    {
        // (Smaller output weights on the wide nets, so that few scores clamp at MaxScore, where buckets would agree.)
        var network = Network.RandomBuckets(hidden, buckets, seed: hidden + buckets, featureRange: hidden >= 1024 ? 100 : 200,
                                            outputRange: hidden >= 1024 ? 16 : 127);
        var white = new short[hidden];
        var black = new short[hidden];
        var rng = new Random(hidden * 17 + buckets);
        int differentFromBucketZero = 0, compared = 0;
        foreach (int n in new[] { 2, 3, 4, 5, 6, 8, 9, 16, 17, 31, 32 }.Concat(Enumerable.Range(2, 31)))
        {
            var board = Board(rng, n);
            network.Refresh(board, white, black);
            int bucket = BucketOf(n, buckets);
            foreach (var (us, them) in new[] { (white, black), (black, white) })
            {
                int expected = network.ForwardScalar(us, them, bucket);
                foreach (int width in Widths)
                    if (ForwardAt(width, network, us, them, bucket) != expected)
                        Assert.Fail($"{width} bits, bucket {bucket}: {ForwardAt(width, network, us, them, bucket)}, the definition {expected} ({network})");
                // Every other bucket at every width too, on the same accumulators.
                for (int other = 0; other < buckets; other++)
                    foreach (int width in Widths)
                        Assert.Equal(network.ForwardScalar(us, them, other), ForwardAt(width, network, us, them, other));
                if (bucket != 0 && expected != network.ForwardScalar(us, them, 0))
                    differentFromBucketZero++;
                compared++;
            }
        }
        Assert.True(differentFromBucketZero > compared / 3, $"{differentFromBucketZero} of {compared} differ from bucket 0");

        // Every int16 an accumulator can hold, either side of the clamp, in each bucket.
        for (int trial = 0; trial < 300; trial++)
        {
            for (int i = 0; i < hidden; i++)
            {
                white[i] = (short)rng.Next(short.MinValue, short.MaxValue + 1);
                black[i] = (short)(trial % 2 == 0 ? rng.Next(-300, 300) : rng.Next(short.MinValue, short.MaxValue + 1));
            }
            int bucket = trial % buckets;
            int expected = network.ForwardScalar(white, black, bucket);
            foreach (int width in Widths)
                Assert.Equal(expected, ForwardAt(width, network, white, black, bucket));
        }
    }

    [Fact]
    public void EveryWidthEqualsTheDefinitionAtTheLargestSumsInTheLastBucket()
    {
        // MaxHidden and MaxBuckets, every output weight of the last bucket the largest the loader allows and every
        // accumulator past QA (NnueTests' largest sums, at the far end of the weights); the other buckets 0. QB 2^20 keeps
        // the score off the clamp: 255^2 x 128 x (2,730 - 1,366) / 255 = 44,520,960, x 400 / (255 x 2^20) = 66.
        const int hidden = Network.MaxHidden, buckets = Network.MaxBuckets;
        var network = Network.Read(CraftBuckets(hidden, buckets, f: _ => 0, b: _ => 0,
            o: (bucket, i) => bucket < buckets - 1 ? (short)0 : i % 3 == 0 ? (short)-128 : (short)128, bias: bucket => bucket, qb: 1 << 20));
        var full = Enumerable.Repeat((short)1000, hidden).ToArray();
        Assert.Equal(66, network.ForwardScalar(full, full, buckets - 1));
        Assert.Equal(0, network.ForwardScalar(full, full, buckets - 2));   // (its bias alone, 14, truncates to 0)
        foreach (int width in Widths)
        {
            Assert.Equal(66, ForwardAt(width, network, full, full, buckets - 1));
            Assert.Equal(0, ForwardAt(width, network, full, full, 0));
        }
    }

    [Fact]
    public void NoBucketPastTheLastIsRead()
    {
        // The rows and biases are checked spans and arrays: a wrong bucket throws rather than reading past the weights (the
        // row, or where the array's alignment room holds one more row of zeros, the bias).
        var network = Network.RandomBuckets(16, 8, seed: 3);
        var accumulator = new short[16];
        foreach (int width in Widths)
        {
            Assert.ThrowsAny<SystemException>(() => ForwardAt(width, network, accumulator, accumulator, 8));
            Assert.ThrowsAny<SystemException>(() => ForwardAt(width, network, accumulator, accumulator, -1));
            Assert.ThrowsAny<SystemException>(() => ForwardAt(width, Network.Random(16, 1), accumulator, accumulator, 1));
        }
    }

    [Fact]
    public void EveryBucketStartsOnACacheLine()
    {
        // For speed only, as NnueTests' alignment test: each bucket's 2H numbers are 4H bytes, a whole number of lines.
        foreach (var network in new[] { Network.RandomBuckets(32, 8, 1), Network.RandomBuckets(256, 5, 2), Network.RandomBuckets(1024, 8, 3) })
            for (int bucket = 0; bucket < network.Buckets; bucket++)
            {
                Assert.Equal(0, Aligned.Offset(network.BucketWeights(bucket)));
                Assert.Equal(0, Aligned.Offset(network.BucketWeights(bucket)[network.Hidden..]));
            }
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
        var network = Network.RandomBuckets(64, 8, seed: 7);
        var rng = new Random(7);
        int differentFromZero = 0, count = 0;
        foreach (var position in RandomGamePositions(300, 7).Concat(Enumerable.Range(0, 310).Select(k => Board(rng, 2 + k % 31))))
        {
            int score = Evaluation.Evaluate(Position.FromFen(position.ToFen(), network));
            Assert.Equal(score, Evaluation.Evaluate(Position.FromFen(Mirror(position), network)));
            if (score != 0)
                differentFromZero++;
            count++;
        }
        Assert.True(differentFromZero > count * 9 / 10, $"{differentFromZero} of {count} not 0");
    }

    [Fact]
    public void MoveByMoveScoresEqualAFreshEvaluation()
    {
        // The accumulators are version 1's, the bucket the board's: at every ply of a game, as pieces come off, the kept
        // accumulators and their bucket must score as a fresh evaluation does. Null moves and take-backs included.
        var network = Network.RandomBuckets(64, 8, seed: 11);
        var rng = new Random(12);
        var seen = new HashSet<int>();
        for (int game = 0; game < 30; game++)
        {
            var position = Position.FromFen(game % 2 == 0 ? MoveGeneratorTests.Kiwipete : Fen.StartPosition, network);
            var line = new Stack<(Move? Move, Undo Undo)>();
            for (int ply = 0; ply < 200; ply++)
            {
                Assert.Equal(network.Evaluate(Position.FromFen(position.ToFen())), Evaluation.Evaluate(position));
                seen.Add(network.Bucket(BitOperations.PopCount(position.Occupied)));
                double roll = rng.NextDouble();
                if (roll < 0.05 && !position.InCheck())
                {
                    line.Push((null, position.MakeNullMove()));
                    continue;
                }
                if (roll < 0.1 && line.Count > 0)
                {
                    var (move, undo) = line.Pop();
                    if (move is Move made) position.UnmakeMove(made, undo);
                    else position.UnmakeNullMove(undo);
                    continue;
                }
                var moves = MoveGenerator.Legal(position);
                if (moves.Count == 0)
                    break;
                var captures = moves.Where(m => m.IsCapture).ToList();
                var chosen = captures.Count > 0 && rng.NextDouble() < 0.5 ? captures[rng.Next(captures.Count)] : moves[rng.Next(moves.Count)];
                line.Push((chosen, position.MakeMove(chosen)));
            }
        }
        Assert.True(seen.Count >= 6, $"buckets seen: {string.Join(", ", seen.Order())}");
    }

    [Fact]
    public void OneBucketScoresAsVersionOne()
    {
        // The same numbers as a version 1 file and as a version 3 file of K 1, or of K 8 with every bucket the same row:
        // the same score everywhere.
        Func<int, short> f = i => (short)(i * 37 % 61 - 30), b = i => (short)(i % 50);
        Func<int, short> o = i => (short)(i * 13 % 101 - 50);
        var one = Network.Read(CraftVersion1(64, f, b, o, outputBias: 2_345));
        var k1 = Network.Read(CraftBuckets(64, 1, f, b, (_, i) => o(i), _ => 2_345));
        var k8 = Network.Read(CraftBuckets(64, 8, f, b, (_, i) => o(i), _ => 2_345));
        Assert.Equal((Network.Version, Network.BucketVersion, Network.BucketVersion), (one.FileVersion, k1.FileVersion, k8.FileVersion));
        int nonZero = 0;
        foreach (var position in RandomGamePositions(200, 3))
        {
            int score = one.Evaluate(position);
            Assert.Equal(score, k1.Evaluate(position));
            Assert.Equal(score, k8.Evaluate(position));
            Assert.Equal(score, Evaluation.Evaluate(Position.FromFen(position.ToFen(), k8)));
            if (score != 0)
                nonZero++;
        }
        Assert.True(nonZero > 150);
    }

    // ---- the file ----

    /// <summary>
    /// A version 3 file written here, independently of <see cref="Network.ToBytes"/>, with a correct checksum:
    /// <paramref name="o"/>(bucket, i) the output weights bucket by bucket, <paramref name="bias"/>(bucket) the biases;
    /// <paramref name="headerBuckets"/> what the header says, if not the buckets written.
    /// </summary>
    private static byte[] CraftBuckets(int hidden, int buckets, Func<int, short> f, Func<int, short> b, Func<int, int, short> o,
                                       Func<int, int> bias, uint version = 3, uint inputs = 768, int qa = 255, int qb = 64,
                                       int scale = 400, uint? headerBuckets = null)
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
        U32(headerBuckets ?? (uint)buckets);
        for (int i = 0; i < (int)inputs * hidden; i++) I16(f(i));
        for (int i = 0; i < hidden; i++) I16(b(i));
        for (int bucket = 0; bucket < buckets; bucket++)
            for (int i = 0; i < 2 * hidden; i++)
                I16(o(bucket, i));
        for (int bucket = 0; bucket < buckets; bucket++)
            I32(bias(bucket));
        bytes.AddRange(SHA256.HashData(bytes.ToArray()));
        return bytes.ToArray();
    }

    /// <summary>NnueTests' version 1 craft, for <see cref="OneBucketScoresAsVersionOne"/>.</summary>
    private static byte[] CraftVersion1(int hidden, Func<int, short> f, Func<int, short> b, Func<int, short> o, int outputBias)
    {
        var bytes = new List<byte>("CBNNUE01"u8.ToArray());
        void U32(uint v) { var x = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(x, v); bytes.AddRange(x); }
        void I32(int v) { var x = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(x, v); bytes.AddRange(x); }
        void I16(short v) { var x = new byte[2]; BinaryPrimitives.WriteInt16LittleEndian(x, v); bytes.AddRange(x); }
        U32(1);
        U32(768);
        U32((uint)hidden);
        I32(255);
        I32(64);
        I32(400);
        for (int i = 0; i < 768 * hidden; i++) I16(f(i));
        for (int i = 0; i < hidden; i++) I16(b(i));
        for (int i = 0; i < 2 * hidden; i++) I16(o(i));
        I32(outputBias);
        bytes.AddRange(SHA256.HashData(bytes.ToArray()));
        return bytes.ToArray();
    }

    [Fact]
    public void TheFileRoundTrips()
    {
        var network = Network.RandomBuckets(32, 8, seed: 5);
        byte[] bytes = network.ToBytes();
        Assert.Equal(32 + 4 + 2 * (768 * 32 + 32 + 8 * 64) + 4 * 8 + 32, bytes.Length);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32)));
        var again = Network.Read(bytes);
        Assert.Equal(network.Sha256, again.Sha256);
        Assert.Equal(bytes, again.ToBytes());
        Assert.Equal((Network.BucketVersion, 8), (again.FileVersion, again.Buckets));
        foreach (var position in RandomGamePositions(50, 9))
            Assert.Equal(network.Evaluate(position), again.Evaluate(position));
        // K 1 stays version 3 (it says so in its header), and version 1 stays version 1.
        var k1 = Network.RandomBuckets(16, 1, seed: 6);
        Assert.Equal(k1.ToBytes(), Network.Read(k1.ToBytes()).ToBytes());
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(k1.ToBytes().AsSpan(8)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(Network.Random(16, 1).ToBytes().AsSpan(8)));

        // The craft above writes the format as NNUE.md gives it; the reader agrees with it, bucket by bucket, and the writer
        // writes it back byte for byte (a writer and reader wrong the same way would round-trip each other).
        byte[] craftedBytes = CraftBuckets(32, 5, f: i => (short)(i % 7 - 3), b: i => (short)(i % 11),
                                           o: (bucket, i) => (short)(20 * bucket + i % 9 - 4), bias: bucket => -1000 * bucket - 7);
        var crafted = Network.Read(craftedBytes);
        Assert.Equal(craftedBytes, crafted.ToBytes());
        Assert.Equal((32, 5, Network.BucketVersion), (crafted.Hidden, crafted.Buckets, crafted.FileVersion));
        Assert.Equal((short)(1000 % 7 - 3), crafted.FeatureWeights[1000]);
        Assert.Equal((short)(31 % 11), crafted.FeatureBiases[31]);
        Assert.Equal(5 * 64, crafted.OutputWeights.Length);
        for (int bucket = 0; bucket < 5; bucket++)
        {
            Assert.Equal(-1000 * bucket - 7, crafted.OutputBiases[bucket]);
            for (int i = 0; i < 64; i++)
                Assert.Equal((short)(20 * bucket + i % 9 - 4), crafted.BucketWeights(bucket)[i]);
        }
        Assert.Equal(-7, crafted.OutputBias);
        Assert.StartsWith("768 -> 32 x2 -> 1, 5 output buckets, sha256 ", crafted.ToString());
        Assert.StartsWith("768 -> 32 x2 -> 1, sha256 ", Network.Random(32, 1).ToString());
    }

    [Fact]
    public void TheLoaderRefusesWhatItCannotRun()
    {
        Func<int, short> zero = _ => 0;
        Func<int, int, short> none = (_, _) => 0;
        Func<int, int> noBias = _ => 0;
        FormatException Refused(byte[] bytes) => Assert.Throws<FormatException>(() => Network.Read(bytes));
        Assert.Contains("output buckets", Refused(CraftBuckets(16, 0, zero, zero, none, noBias)).Message);
        Assert.Contains("output buckets", Refused(CraftBuckets(16, 17, zero, zero, none, noBias)).Message);
        Assert.Equal(16, Network.Read(CraftBuckets(16, 16, zero, zero, none, noBias)).Buckets);
        Assert.Equal(1, Network.Read(CraftBuckets(16, 1, zero, zero, none, noBias)).Buckets);
        // A header that says 8 over 7 buckets' numbers, or 7 over 8: the length gives it away.
        Assert.Contains("bytes", Refused(CraftBuckets(16, 7, zero, zero, none, noBias, headerBuckets: 8)).Message);
        Assert.Contains("bytes", Refused(CraftBuckets(16, 8, zero, zero, none, noBias, headerBuckets: 7)).Message);
        // Version 1's checks, on the version 3 path.
        Assert.Contains("inputs", Refused(CraftBuckets(16, 8, zero, zero, none, noBias, inputs: 640)).Message);
        Assert.Contains("hidden size", Refused(CraftBuckets(24, 8, zero, zero, none, noBias)).Message);
        Assert.Contains("QA", Refused(CraftBuckets(16, 8, zero, zero, none, noBias, qa: 256)).Message);
        Assert.Contains("QB", Refused(CraftBuckets(16, 8, zero, zero, none, noBias, qb: 0)).Message);
        Assert.Contains("hidden unit", Refused(CraftBuckets(16, 8, f: i => i % 16 == 3 ? (short)1100 : (short)0, zero, none, noBias)).Message);
        // An output weight whose product with QA overflows 16 bits, in any bucket: each one checked.
        for (int bucket = 0; bucket < 8; bucket++)
        {
            int at = bucket;
            string message = Refused(CraftBuckets(16, 8, zero, zero, (k, i) => k == at && i == 5 + at ? (short)129 : (short)128, noBias)).Message;
            Assert.Contains($"bucket {bucket}'s output weight 129", message);
        }
        Assert.Contains("bucket 7's output weight -129",
                        Refused(CraftBuckets(16, 8, zero, zero, (k, i) => k == 7 && i == 31 ? (short)-129 : (short)-128, noBias)).Message);
        Assert.Equal(8, Network.Read(CraftBuckets(16, 8, zero, zero, (_, _) => -128, noBias)).Buckets);   // (128 x 255 fits)
        // Cut, damaged, too short for its count.
        byte[] good = CraftBuckets(16, 8, zero, zero, none, noBias);
        Assert.Throws<FormatException>(() => Network.Read(good[..^1]));
        Assert.Throws<FormatException>(() => Network.Read(good[..36]));
        byte[] damaged = (byte[])good.Clone();
        damaged[^40] ^= 1;   // in the biases
        Assert.Contains("SHA-256", Refused(damaged).Message);
        Assert.Equal(Network.BucketVersion, Network.Read(good).FileVersion);
    }

    // ---- in the engine ----

    [Theory]
    [InlineData(1, ParallelMode.LazySmp)]
    [InlineData(3, ParallelMode.LazySmp)]
    [InlineData(3, ParallelMode.Ranges)]
    public void TheSearchRunsOnABucketedNet(int threads, ParallelMode split)
    {
        // Every bucket says +1000 for the side to move, so the root's score is exactly +-1000 however the counts fall;
        // a helper on another evaluation, or a bucket read past the last, would show.
        var network = Network.Read(CraftBuckets(16, 8, f: _ => 0, b: _ => 0, o: (_, _) => 0, bias: _ => 1000 * 255 * 64 / 400));
        Assert.Equal(1000, network.Evaluate(Position.Start()));
        var search = new Search(new TranspositionTable(8)) { Threads = threads, Split = split };
        var root = Position.FromFen(MoveGeneratorTests.Middlegame, network);
        var result = search.Run(root, new SearchLimits { Depth = 5 });
        Assert.Equal(1000, Math.Abs(result.Score));
        foreach (var helperRoot in search.HelperRoots)
            Assert.Same(network, helperRoot!.Network);

        // On a net whose buckets differ, a search from a trading position runs and leaves the root as it found it. (The
        // piece-square net's buckets: a random net makes the capture search explode, as NnueTests says, the more so on a
        // mutant's scores.)
        var squares = PieceSquareBuckets();
        var kiwipete = Position.FromFen(MoveGeneratorTests.Kiwipete, squares);
        var deeper = new Search(new TranspositionTable(8)) { Threads = threads, Split = split };
        var found = deeper.Run(kiwipete, new SearchLimits { Depth = 5 });
        Assert.Contains(found.BestMove!.Value, MoveGenerator.Legal(Position.FromFen(MoveGeneratorTests.Kiwipete)));
        Assert.Equal(MoveGeneratorTests.Kiwipete, kiwipete.ToFen());
        Assert.Equal(squares.Evaluate(Position.FromFen(MoveGeneratorTests.Kiwipete)), Evaluation.Evaluate(kiwipete));
    }

    /// <summary>
    /// <see cref="Network.FromPieceSquares"/>'s net with 8 buckets, each its row and a bias of 100 x b centipawns: a tree like a
    /// real evaluation's, and a score that says the bucket.
    /// </summary>
    private static Network PieceSquareBuckets()
    {
        var squares = Network.FromPieceSquares(EvalParams.Original());
        var weights = squares.FeatureWeights.ToArray();
        var outputs = squares.OutputWeights.ToArray();
        return Network.Read(CraftBuckets(squares.Hidden, 8, f: i => weights[i], b: i => squares.FeatureBiases[i], o: (_, i) => outputs[i],
                                         bias: bucket => (100 * bucket * squares.QA * squares.QB + squares.Scale - 1) / squares.Scale,   // (rounded up, so the score's truncation lands on 100 b)
                                         qb: squares.QB));
    }

    [Fact]
    public void UciEvalFileTakesABucketedNet()
    {
        var network = PieceSquareBuckets();
        Assert.Equal(700, network.Evaluate(Position.Start()));   // (the start is level: bucket 7's bias alone)
        string path = Path.Combine(Path.GetTempPath(), $"buckets-{Guid.NewGuid():N}.nnue");
        try
        {
            network.Save(path);
            var output = new StringWriter();
            var engine = new UciEngine(output);
            engine.Handle($"setoption name EvalFile value {path}");
            Assert.Contains("evaluation: network", output.ToString());
            Assert.Contains($"768 -> 256 x2 -> 1, 8 output buckets, sha256 {network.Sha256[..12]}", output.ToString());
            // Two boards of different buckets: the start (32 pieces, bucket 7) and kings and a rook (3, bucket 0).
            engine.Handle("position startpos");
            engine.Handle("eval");
            Assert.Contains($"eval {network.Evaluate(Position.Start())} cp", output.ToString());
            const string few = "8/8/4k3/8/8/3K4/8/R7 w - - 0 1";
            engine.Handle($"position fen {few}");
            engine.Handle("eval");
            Assert.Contains($"eval {network.Evaluate(Position.FromFen(few))} cp", output.ToString());
            engine.Handle("position startpos");
            engine.Handle("go depth 3");
            engine.WaitForSearch();
            Assert.Contains("bestmove ", output.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
