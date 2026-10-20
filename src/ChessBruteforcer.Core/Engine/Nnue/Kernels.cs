using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ChessBruteforcer.Core.Engine.Nnue;

/// <summary>
/// The accumulator's column arithmetic: 32 int16 lanes at a time (AVX-512) where the CPU has it, with a last
/// 16 when the length isn't a multiple of 32; 16 at a time (AVX2); or one at a time. Wrapping 16-bit
/// arithmetic every way, so neither the width nor the order of adds and subtracts changes a result.
///
/// Every operand is a span, and its length is checked against the destination's before any lane is
/// touched, so a wrong slot or column throws instead of reading or writing past an array (the vector
/// loads inside are unchecked; these checks are what make them safe). The length is a multiple of 16
/// (the loader insists, and so does <see cref="Check"/>).
/// </summary>
internal static class Kernels
{
    /// <summary>
    /// The width the kernels and the forward pass use: 512 bits with AVX-512 (BW), 256 with AVX2, 0 for one
    /// number at a time. The environment variable CB_NNUE_SIMD (256 or 0) holds it narrower, for an A/B or if
    /// 512 bits ever cost a CPU more clock than they save; it can't ask for more than the CPU has.
    /// </summary>
    internal static readonly int Width = ChooseWidth(Environment.GetEnvironmentVariable("CB_NNUE_SIMD"));

    /// <summary>The widest this CPU runs.</summary>
    internal static int Widest => Vector512.IsHardwareAccelerated && Avx512BW.IsSupported ? 512 : Avx2.IsSupported ? 256 : 0;

    internal static int ChooseWidth(string? asked) =>
        int.TryParse(asked, out int width) && width is 0 or 256 or 512 && width <= Widest ? width : Widest;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Check(int n, int length)
    {
        if ((n & 15) != 0 || length < n)
            Throw(n, length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Throw(int n, int length) =>
        throw new ArgumentOutOfRangeException(nameof(length), $"a column of {n} against an operand of {length}");

    /// <summary>acc += a.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add(Span<short> acc, ReadOnlySpan<short> a) => Add(Width, acc, a);

    /// <summary>acc += a, at the given width (the tests try each).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Add(int width, Span<short> acc, ReadOnlySpan<short> a)
    {
        int n = acc.Length;
        Check(n, a.Length);
        ref short d = ref MemoryMarshal.GetReference(acc);
        ref short x = ref MemoryMarshal.GetReference(a);
        int i = 0;
        if (width == 512)
            for (; i + 32 <= n; i += 32)
                (Vector512.LoadUnsafe(ref d, (nuint)i) + Vector512.LoadUnsafe(ref x, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
        if (width >= 256)
        {
            for (; i < n; i += 16)
                (Vector256.LoadUnsafe(ref d, (nuint)i) + Vector256.LoadUnsafe(ref x, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
            return;
        }
        for (; i < n; i++)
            acc[i] = (short)(acc[i] + a[i]);
    }

    /// <summary>dst = src + a - s.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddSub(Span<short> dst, ReadOnlySpan<short> src, ReadOnlySpan<short> a, ReadOnlySpan<short> s) =>
        AddSub(Width, dst, src, a, s);

    /// <summary>dst = src + a - s, at the given width.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddSub(int width, Span<short> dst, ReadOnlySpan<short> src, ReadOnlySpan<short> a, ReadOnlySpan<short> s)
    {
        int n = dst.Length;
        Check(n, Math.Min(src.Length, Math.Min(a.Length, s.Length)));
        ref short d = ref MemoryMarshal.GetReference(dst);
        ref short p = ref MemoryMarshal.GetReference(src);
        ref short x = ref MemoryMarshal.GetReference(a);
        ref short y = ref MemoryMarshal.GetReference(s);
        int i = 0;
        if (width == 512)
            for (; i + 32 <= n; i += 32)
                (Vector512.LoadUnsafe(ref p, (nuint)i) + Vector512.LoadUnsafe(ref x, (nuint)i)
                 - Vector512.LoadUnsafe(ref y, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
        if (width >= 256)
        {
            for (; i < n; i += 16)
                (Vector256.LoadUnsafe(ref p, (nuint)i) + Vector256.LoadUnsafe(ref x, (nuint)i)
                 - Vector256.LoadUnsafe(ref y, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
            return;
        }
        for (; i < n; i++)
            dst[i] = (short)(src[i] + a[i] - s[i]);
    }

    /// <summary>dst = src + a - s1 - s2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddSubSub(Span<short> dst, ReadOnlySpan<short> src, ReadOnlySpan<short> a,
                                 ReadOnlySpan<short> s1, ReadOnlySpan<short> s2) =>
        AddSubSub(Width, dst, src, a, s1, s2);

    /// <summary>dst = src + a - s1 - s2, at the given width.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddSubSub(int width, Span<short> dst, ReadOnlySpan<short> src, ReadOnlySpan<short> a,
                                   ReadOnlySpan<short> s1, ReadOnlySpan<short> s2)
    {
        int n = dst.Length;
        Check(n, Math.Min(Math.Min(src.Length, a.Length), Math.Min(s1.Length, s2.Length)));
        ref short d = ref MemoryMarshal.GetReference(dst);
        ref short p = ref MemoryMarshal.GetReference(src);
        ref short x = ref MemoryMarshal.GetReference(a);
        ref short y1 = ref MemoryMarshal.GetReference(s1);
        ref short y2 = ref MemoryMarshal.GetReference(s2);
        int i = 0;
        if (width == 512)
            for (; i + 32 <= n; i += 32)
                (Vector512.LoadUnsafe(ref p, (nuint)i) + Vector512.LoadUnsafe(ref x, (nuint)i)
                 - Vector512.LoadUnsafe(ref y1, (nuint)i) - Vector512.LoadUnsafe(ref y2, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
        if (width >= 256)
        {
            for (; i < n; i += 16)
                (Vector256.LoadUnsafe(ref p, (nuint)i) + Vector256.LoadUnsafe(ref x, (nuint)i)
                 - Vector256.LoadUnsafe(ref y1, (nuint)i) - Vector256.LoadUnsafe(ref y2, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
            return;
        }
        for (; i < n; i++)
            dst[i] = (short)(src[i] + a[i] - s1[i] - s2[i]);
    }

    /// <summary>dst = src + a1 + a2 - s1 - s2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddAddSubSub(Span<short> dst, ReadOnlySpan<short> src, ReadOnlySpan<short> a1, ReadOnlySpan<short> a2,
                                    ReadOnlySpan<short> s1, ReadOnlySpan<short> s2) =>
        AddAddSubSub(Width, dst, src, a1, a2, s1, s2);

    /// <summary>dst = src + a1 + a2 - s1 - s2, at the given width.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddAddSubSub(int width, Span<short> dst, ReadOnlySpan<short> src, ReadOnlySpan<short> a1,
                                      ReadOnlySpan<short> a2, ReadOnlySpan<short> s1, ReadOnlySpan<short> s2)
    {
        int n = dst.Length;
        Check(n, Math.Min(Math.Min(src.Length, Math.Min(a1.Length, a2.Length)), Math.Min(s1.Length, s2.Length)));
        ref short d = ref MemoryMarshal.GetReference(dst);
        ref short p = ref MemoryMarshal.GetReference(src);
        ref short x1 = ref MemoryMarshal.GetReference(a1);
        ref short x2 = ref MemoryMarshal.GetReference(a2);
        ref short y1 = ref MemoryMarshal.GetReference(s1);
        ref short y2 = ref MemoryMarshal.GetReference(s2);
        int i = 0;
        if (width == 512)
            for (; i + 32 <= n; i += 32)
                (Vector512.LoadUnsafe(ref p, (nuint)i) + Vector512.LoadUnsafe(ref x1, (nuint)i) + Vector512.LoadUnsafe(ref x2, (nuint)i)
                 - Vector512.LoadUnsafe(ref y1, (nuint)i) - Vector512.LoadUnsafe(ref y2, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
        if (width >= 256)
        {
            for (; i < n; i += 16)
                (Vector256.LoadUnsafe(ref p, (nuint)i) + Vector256.LoadUnsafe(ref x1, (nuint)i) + Vector256.LoadUnsafe(ref x2, (nuint)i)
                 - Vector256.LoadUnsafe(ref y1, (nuint)i) - Vector256.LoadUnsafe(ref y2, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
            return;
        }
        for (; i < n; i++)
            dst[i] = (short)(src[i] + a1[i] + a2[i] - s1[i] - s2[i]);
    }
}
