using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ChessBruteforcer.Core.Engine.Nnue;

/// <summary>
/// The accumulator's column arithmetic, 16 int16 lanes at a time (AVX2), or one at a time where the
/// hardware has no 256-bit vectors. Wrapping 16-bit arithmetic either way, so the order of adds and
/// subtracts never changes a result.
///
/// Every operand is a span, and its length is checked against the destination's before any lane is
/// touched, so a wrong slot or column throws instead of reading or writing past an array (the vector
/// loads inside are unchecked; these checks are what make them safe). The length is a multiple of 16
/// (the loader insists, and so does <see cref="Check"/>).
/// </summary>
internal static class Kernels
{
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
    public static void Add(Span<short> acc, ReadOnlySpan<short> a)
    {
        int n = acc.Length;
        Check(n, a.Length);
        ref short d = ref MemoryMarshal.GetReference(acc);
        ref short x = ref MemoryMarshal.GetReference(a);
        if (Vector256.IsHardwareAccelerated)
        {
            for (int i = 0; i < n; i += 16)
                (Vector256.LoadUnsafe(ref d, (nuint)i) + Vector256.LoadUnsafe(ref x, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
            return;
        }
        for (int i = 0; i < n; i++)
            acc[i] = (short)(acc[i] + a[i]);
    }

    /// <summary>dst = src + a - s.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddSub(Span<short> dst, ReadOnlySpan<short> src, ReadOnlySpan<short> a, ReadOnlySpan<short> s)
    {
        int n = dst.Length;
        Check(n, Math.Min(src.Length, Math.Min(a.Length, s.Length)));
        ref short d = ref MemoryMarshal.GetReference(dst);
        ref short p = ref MemoryMarshal.GetReference(src);
        ref short x = ref MemoryMarshal.GetReference(a);
        ref short y = ref MemoryMarshal.GetReference(s);
        if (Vector256.IsHardwareAccelerated)
        {
            for (int i = 0; i < n; i += 16)
                (Vector256.LoadUnsafe(ref p, (nuint)i) + Vector256.LoadUnsafe(ref x, (nuint)i)
                 - Vector256.LoadUnsafe(ref y, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
            return;
        }
        for (int i = 0; i < n; i++)
            dst[i] = (short)(src[i] + a[i] - s[i]);
    }

    /// <summary>dst = src + a - s1 - s2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddSubSub(Span<short> dst, ReadOnlySpan<short> src, ReadOnlySpan<short> a,
                                 ReadOnlySpan<short> s1, ReadOnlySpan<short> s2)
    {
        int n = dst.Length;
        Check(n, Math.Min(Math.Min(src.Length, a.Length), Math.Min(s1.Length, s2.Length)));
        ref short d = ref MemoryMarshal.GetReference(dst);
        ref short p = ref MemoryMarshal.GetReference(src);
        ref short x = ref MemoryMarshal.GetReference(a);
        ref short y1 = ref MemoryMarshal.GetReference(s1);
        ref short y2 = ref MemoryMarshal.GetReference(s2);
        if (Vector256.IsHardwareAccelerated)
        {
            for (int i = 0; i < n; i += 16)
                (Vector256.LoadUnsafe(ref p, (nuint)i) + Vector256.LoadUnsafe(ref x, (nuint)i)
                 - Vector256.LoadUnsafe(ref y1, (nuint)i) - Vector256.LoadUnsafe(ref y2, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
            return;
        }
        for (int i = 0; i < n; i++)
            dst[i] = (short)(src[i] + a[i] - s1[i] - s2[i]);
    }

    /// <summary>dst = src + a1 + a2 - s1 - s2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddAddSubSub(Span<short> dst, ReadOnlySpan<short> src, ReadOnlySpan<short> a1, ReadOnlySpan<short> a2,
                                    ReadOnlySpan<short> s1, ReadOnlySpan<short> s2)
    {
        int n = dst.Length;
        Check(n, Math.Min(Math.Min(src.Length, Math.Min(a1.Length, a2.Length)), Math.Min(s1.Length, s2.Length)));
        ref short d = ref MemoryMarshal.GetReference(dst);
        ref short p = ref MemoryMarshal.GetReference(src);
        ref short x1 = ref MemoryMarshal.GetReference(a1);
        ref short x2 = ref MemoryMarshal.GetReference(a2);
        ref short y1 = ref MemoryMarshal.GetReference(s1);
        ref short y2 = ref MemoryMarshal.GetReference(s2);
        if (Vector256.IsHardwareAccelerated)
        {
            for (int i = 0; i < n; i += 16)
                (Vector256.LoadUnsafe(ref p, (nuint)i) + Vector256.LoadUnsafe(ref x1, (nuint)i) + Vector256.LoadUnsafe(ref x2, (nuint)i)
                 - Vector256.LoadUnsafe(ref y1, (nuint)i) - Vector256.LoadUnsafe(ref y2, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
            return;
        }
        for (int i = 0; i < n; i++)
            dst[i] = (short)(src[i] + a1[i] + a2[i] - s1[i] - s2[i]);
    }
}
