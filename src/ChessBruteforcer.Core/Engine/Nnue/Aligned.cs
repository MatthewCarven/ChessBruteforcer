using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ChessBruteforcer.Core.Engine.Nnue;

/// <summary>
/// Room for int16 numbers that starts on a 64-byte boundary (a cache line), so that no 32- or 64-byte load
/// of a weight column or an accumulator crosses one. .NET puts an array's numbers wherever the heap has
/// room: the weights used to sit 24 bytes past a boundary, so half of the 32-byte loads crossed a line, and
/// every 64-byte one would have (NNUE.md, "Speed at 1024"). The array lives on the pinned heap, where
/// nothing ever moves, so a boundary found once holds for the array's life.
/// </summary>
internal static class Aligned
{
    public const int Boundary = 64;

    /// <summary>A pinned array with at least <paramref name="length"/> numbers from <paramref name="start"/> on, which is on a boundary.</summary>
    public static short[] Allocate(int length, out int start)
    {
        var array = GC.AllocateArray<short>(length + Boundary / sizeof(short), pinned: true);
        start = (Boundary - Offset(array)) % Boundary / sizeof(short);
        return array;
    }

    /// <summary>How many bytes past a 64-byte boundary the first number is (a pinned array's never changes).</summary>
    public static int Offset(ReadOnlySpan<short> numbers) =>
        (int)((long)Unsafe.ByteOffset(ref Unsafe.NullRef<short>(), ref MemoryMarshal.GetReference(numbers)) % Boundary);
}
