using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace ChessBruteforcer.Core.Engine.Nnue;

/// <summary>
/// The accumulator's column arithmetic, 16 int16 lanes at a time (AVX2), or one at a time where the
/// hardware has no 256-bit vectors. Wrapping 16-bit arithmetic either way, so the order of adds and
/// subtracts never changes a result. <c>n</c> is a multiple of 16 (the loader insists).
/// </summary>
internal static class Kernels
{
    /// <summary>acc += a.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add(ref short acc, ref short a, int n)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            for (int i = 0; i < n; i += 16)
                (Vector256.LoadUnsafe(ref acc, (nuint)i) + Vector256.LoadUnsafe(ref a, (nuint)i)).StoreUnsafe(ref acc, (nuint)i);
            return;
        }
        for (int i = 0; i < n; i++)
            Unsafe.Add(ref acc, i) = (short)(Unsafe.Add(ref acc, i) + Unsafe.Add(ref a, i));
    }

    /// <summary>dst = src + a - s.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddSub(ref short dst, ref short src, ref short a, ref short s, int n)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            for (int i = 0; i < n; i += 16)
                (Vector256.LoadUnsafe(ref src, (nuint)i) + Vector256.LoadUnsafe(ref a, (nuint)i)
                 - Vector256.LoadUnsafe(ref s, (nuint)i)).StoreUnsafe(ref dst, (nuint)i);
            return;
        }
        for (int i = 0; i < n; i++)
            Unsafe.Add(ref dst, i) = (short)(Unsafe.Add(ref src, i) + Unsafe.Add(ref a, i) - Unsafe.Add(ref s, i));
    }

    /// <summary>dst = src + a - s1 - s2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddSubSub(ref short dst, ref short src, ref short a, ref short s1, ref short s2, int n)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            for (int i = 0; i < n; i += 16)
                (Vector256.LoadUnsafe(ref src, (nuint)i) + Vector256.LoadUnsafe(ref a, (nuint)i)
                 - Vector256.LoadUnsafe(ref s1, (nuint)i) - Vector256.LoadUnsafe(ref s2, (nuint)i)).StoreUnsafe(ref dst, (nuint)i);
            return;
        }
        for (int i = 0; i < n; i++)
            Unsafe.Add(ref dst, i) = (short)(Unsafe.Add(ref src, i) + Unsafe.Add(ref a, i)
                                             - Unsafe.Add(ref s1, i) - Unsafe.Add(ref s2, i));
    }

    /// <summary>dst = src + a1 + a2 - s1 - s2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddAddSubSub(ref short dst, ref short src, ref short a1, ref short a2, ref short s1, ref short s2, int n)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            for (int i = 0; i < n; i += 16)
                (Vector256.LoadUnsafe(ref src, (nuint)i) + Vector256.LoadUnsafe(ref a1, (nuint)i) + Vector256.LoadUnsafe(ref a2, (nuint)i)
                 - Vector256.LoadUnsafe(ref s1, (nuint)i) - Vector256.LoadUnsafe(ref s2, (nuint)i)).StoreUnsafe(ref dst, (nuint)i);
            return;
        }
        for (int i = 0; i < n; i++)
            Unsafe.Add(ref dst, i) = (short)(Unsafe.Add(ref src, i) + Unsafe.Add(ref a1, i) + Unsafe.Add(ref a2, i)
                                             - Unsafe.Add(ref s1, i) - Unsafe.Add(ref s2, i));
    }
}
