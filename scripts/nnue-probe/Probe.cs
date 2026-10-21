using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Engine;

/// <summary>
/// Scratch-only cost probe (not engine code): counts evaluations, and in modes 1-3 adds work beside the
/// real evaluation so its cost can be read off the same search tree.
/// Mode 0: nothing extra. 1: the evaluation a second time. 2: a 768 -> H x2 -> 1 SCReLU network, its
/// accumulators updated on every board change. 3: HalfKP 40960 -> 256 x2 -> 32 -> 32 -> 1, int8 hidden layers,
/// a perspective refreshed (from the board, at the next evaluation) whenever its own king moves.
/// Updates run on make and unmake alike (a real engine pops a stack on unmake), so modes 2-3 are upper bounds.
/// </summary>
public static class Probe
{
    public static int Mode;
    public static long Evals, Updates, Refreshes;
    public static int Sink;

    private static int _h = 256;
    private static short[] _w = Array.Empty<short>();     // feature weights, feature-major, _h each
    private static short[] _bias = Array.Empty<short>();
    private static short[] _outW = Array.Empty<short>();  // 768 net: 2H output weights
    private static readonly short[][] _acc = { Array.Empty<short>(), Array.Empty<short>() };
    private static readonly bool[] _dirty = { true, true };
    // HalfKP hidden layers
    private static sbyte[] _w1 = Array.Empty<sbyte>(), _w2 = Array.Empty<sbyte>(), _w3 = Array.Empty<sbyte>();
    private static readonly byte[] _in = new byte[512];
    private static readonly byte[] _h1 = new byte[32];
    private static readonly byte[] _h2 = new byte[32];

    public static void Setup(int mode, int hidden)
    {
        Mode = mode;
        _h = mode == 3 ? 256 : hidden;
        int features = mode == 3 ? 64 * 640 : 768;
        var rng = new Random(12345);
        _w = new short[features * _h];
        for (int i = 0; i < _w.Length; i++) _w[i] = (short)rng.Next(-64, 65);
        _bias = new short[_h];
        for (int i = 0; i < _h; i++) _bias[i] = (short)rng.Next(0, 128);
        _outW = new short[2 * _h];
        for (int i = 0; i < _outW.Length; i++) _outW[i] = (short)rng.Next(-127, 128);
        _acc[0] = new short[_h];
        _acc[1] = new short[_h];
        _w1 = RandomBytes(rng, 32 * 512);
        _w2 = RandomBytes(rng, 32 * 32);
        _w3 = RandomBytes(rng, 32);
        _dirty[0] = _dirty[1] = true;
    }

    private static sbyte[] RandomBytes(Random rng, int n)
    {
        var a = new sbyte[n];
        for (int i = 0; i < n; i++) a[i] = (sbyte)rng.Next(-64, 65);
        return a;
    }

    public static void Reset() => _dirty[0] = _dirty[1] = true;

    public static void Extra(Position position)
    {
        if (Mode == 1)
            Sink ^= Evaluation.Evaluate(position, Evaluation.Parameters);
        else if (Mode == 2)
            Sink ^= Forward768(position);
        else if (Mode == 3)
            Sink ^= ForwardHalfKp(position);
    }

    // ---- features ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Feature768(Piece p, int square, int perspective)
    {
        int rel = (int)p.Colour == perspective ? 0 : 1;
        int sq = perspective == 0 ? square : square ^ 56;
        return ((rel * 6 + (int)p.Type - 1) * 64 + sq) * _h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FeatureHalfKp(int kingSquare, Piece p, int square, int perspective)
    {
        int rel = (int)p.Colour == perspective ? 0 : 1;
        int sq = perspective == 0 ? square : square ^ 56;
        int k = perspective == 0 ? kingSquare : kingSquare ^ 56;
        return (k * 640 + (rel * 5 + (int)p.Type - 1) * 64 + sq) * _h;
    }

    /// <summary>Called from Position.Put after the square changes.</summary>
    public static void OnPut(Position position, Piece old, Piece piece, int square)
    {
        for (int persp = 0; persp < 2; persp++)
        {
            if (Mode == 3 && ((old.Type == PieceType.King && (int)old.Colour == persp)
                              || (piece.Type == PieceType.King && (int)piece.Colour == persp)))
                _dirty[persp] = true;
            if (_dirty[persp])
                continue;
            if (Mode == 2)
            {
                if (!old.IsEmpty) { Sub(_acc[persp], Feature768(old, square, persp)); Updates++; }
                if (!piece.IsEmpty) { Add(_acc[persp], Feature768(piece, square, persp)); Updates++; }
            }
            else
            {
                int k = position.KingSquare((Colour)persp);
                if (k < 0) { _dirty[persp] = true; continue; }
                if (!old.IsEmpty && old.Type != PieceType.King) { Sub(_acc[persp], FeatureHalfKp(k, old, square, persp)); Updates++; }
                if (!piece.IsEmpty && piece.Type != PieceType.King) { Add(_acc[persp], FeatureHalfKp(k, piece, square, persp)); Updates++; }
            }
        }
    }

    private static void Refresh(Position position, int persp)
    {
        Refreshes++;
        Array.Copy(_bias, _acc[persp], _h);
        int k = position.KingSquare((Colour)persp);
        for (int sq = 0; sq < 64; sq++)
        {
            var p = position[sq];
            if (p.IsEmpty) continue;
            if (Mode == 2) Add(_acc[persp], Feature768(p, sq, persp));
            else if (p.Type != PieceType.King) Add(_acc[persp], FeatureHalfKp(k, p, sq, persp));
        }
        _dirty[persp] = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Add(short[] acc, int offset)
    {
        ref short a = ref MemoryMarshal.GetArrayDataReference(acc);
        ref short c = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_w), offset);
        for (int i = 0; i < _h; i += 16)
            (Vector256.LoadUnsafe(ref a, (nuint)i) + Vector256.LoadUnsafe(ref c, (nuint)i)).StoreUnsafe(ref a, (nuint)i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Sub(short[] acc, int offset)
    {
        ref short a = ref MemoryMarshal.GetArrayDataReference(acc);
        ref short c = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_w), offset);
        for (int i = 0; i < _h; i += 16)
            (Vector256.LoadUnsafe(ref a, (nuint)i) - Vector256.LoadUnsafe(ref c, (nuint)i)).StoreUnsafe(ref a, (nuint)i);
    }

    // ---- forward passes ----

    private static int Forward768(Position position)
    {
        if (_dirty[0]) Refresh(position, 0);
        if (_dirty[1]) Refresh(position, 1);
        int us = (int)position.SideToMove;
        var zero = Vector256<short>.Zero;
        var qa = Vector256.Create((short)255);
        var sum = Vector256<int>.Zero;
        ref short ow = ref MemoryMarshal.GetArrayDataReference(_outW);
        for (int side = 0; side < 2; side++)
        {
            ref short a = ref MemoryMarshal.GetArrayDataReference(_acc[side == 0 ? us : 1 - us]);
            int wo = side * _h;
            for (int i = 0; i < _h; i += 16)
            {
                var v = Vector256.Min(Vector256.Max(Vector256.LoadUnsafe(ref a, (nuint)i), zero), qa);
                var w = Vector256.LoadUnsafe(ref ow, (nuint)(wo + i));
                sum += Avx2.MultiplyAddAdjacent(v * w, v);   // SCReLU: clamp(x)^2 * w, as (v*w)*v
            }
        }
        return Vector256.Sum(sum) / 255 * 400 / (255 * 64);
    }

    private static int ForwardHalfKp(Position position)
    {
        if (_dirty[0]) Refresh(position, 0);
        if (_dirty[1]) Refresh(position, 1);
        int us = (int)position.SideToMove;
        ref byte input = ref MemoryMarshal.GetArrayDataReference(_in);
        for (int side = 0; side < 2; side++)
        {
            ref short a = ref MemoryMarshal.GetArrayDataReference(_acc[side == 0 ? us : 1 - us]);
            for (int i = 0; i < 256; i += 32)
            {
                var lo = Vector256.LoadUnsafe(ref a, (nuint)i);
                var hi = Vector256.LoadUnsafe(ref a, (nuint)(i + 16));
                var packed = Avx2.PackUnsignedSaturate(Vector256.Min(lo, Vector256.Create((short)127)),
                                                        Vector256.Min(hi, Vector256.Create((short)127)));
                packed.StoreUnsafe(ref input, (nuint)(side * 256 + i));
            }
        }
        Layer(_in, 512, _w1, _h1);
        Layer(_h1, 32, _w2, _h2);
        var x = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_h2));
        var w = Vector256.LoadUnsafe(ref Unsafe.As<sbyte, byte>(ref MemoryMarshal.GetArrayDataReference(_w3))).AsSByte();
        var s = Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(x, w), Vector256.Create((short)1));
        return Vector256.Sum(s) / 16;
    }

    private static void Layer(byte[] input, int n, sbyte[] weights, byte[] output)
    {
        ref byte x0 = ref MemoryMarshal.GetArrayDataReference(input);
        ref byte w0 = ref Unsafe.As<sbyte, byte>(ref MemoryMarshal.GetArrayDataReference(weights));
        var ones = Vector256.Create((short)1);
        for (int o = 0; o < 32; o++)
        {
            var acc = Vector256<int>.Zero;
            for (int j = 0; j < n; j += 32)
            {
                var x = Vector256.LoadUnsafe(ref x0, (nuint)j);
                var w = Vector256.LoadUnsafe(ref w0, (nuint)(o * n + j)).AsSByte();
                acc += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(x, w), ones);
            }
            output[o] = (byte)Math.Clamp(Vector256.Sum(acc) >> 6, 0, 127);
        }
    }
}
