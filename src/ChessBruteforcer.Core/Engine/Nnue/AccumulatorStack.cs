using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Engine.Nnue;

/// <summary>
/// A position's accumulators, one slot per position on the line from where the network was attached:
/// each slot is white's H numbers, then black's. <see cref="Position.MakeMove"/> writes the child slot as
/// the parent plus the move's 2-4 piece changes, in one pass per perspective; unmake pops. A null move
/// changes no piece, so it shares the slot. Each search thread has its own position, so its own stack;
/// the network's weights are shared, read only.
/// </summary>
internal sealed class AccumulatorStack
{
    private readonly int _hidden;
    private readonly int _stride;
    private short[] _data;
    private int _top;

    public AccumulatorStack(Network network, int slots = 32)
    {
        Network = network;
        _hidden = network.Hidden;
        _stride = 2 * _hidden;
        _data = new short[slots * _stride];
    }

    public Network Network { get; }

    /// <summary>How many moves deep the current position is from where the stack starts.</summary>
    public int Depth => _top;

    public ReadOnlySpan<short> White => _data.AsSpan(_top * _stride, _hidden);

    public ReadOnlySpan<short> Black => _data.AsSpan(_top * _stride + _hidden, _hidden);

    /// <summary>Start again from the board: one slot, refreshed.</summary>
    public void Reset(Position position)
    {
        _top = 0;
        Refresh(position);
    }

    /// <summary>The current slot from the board, from scratch.</summary>
    public void Refresh(Position position)
    {
        var slot = _data.AsSpan(_top * _stride, _stride);
        Network.Refresh(position, slot[.._hidden], slot[_hidden..]);
    }

    /// <summary>Back to the parent's slot. At the bottom of the stack there is none, so refresh from the board.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Pop(Position position)
    {
        if (_top > 0)
            _top--;
        else
            Refresh(position);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Evaluate(Colour sideToMove)
    {
        var slot = _data.AsSpan(_top * _stride, _stride);
        return sideToMove == Colour.White
            ? Network.Forward(slot[.._hidden], slot[_hidden..])
            : Network.Forward(slot[_hidden..], slot[.._hidden]);
    }

    /// <summary>A move with no capture: <paramref name="moved"/> off <paramref name="from"/>, <paramref name="placed"/> (itself, or what it promotes to) on <paramref name="to"/>.</summary>
    public void PushQuiet(Piece moved, int from, Piece placed, int to)
    {
        int parent = NextSlot();
        int h = _hidden;
        ref short d = ref MemoryMarshal.GetArrayDataReference(_data);
        ref short w = ref MemoryMarshal.GetArrayDataReference(Network.FeatureWeights);
        Kernels.AddSub(ref Unsafe.Add(ref d, parent + _stride), ref Unsafe.Add(ref d, parent),
                       ref Unsafe.Add(ref w, Network.Feature(placed, to, 0) * h),
                       ref Unsafe.Add(ref w, Network.Feature(moved, from, 0) * h), h);
        Kernels.AddSub(ref Unsafe.Add(ref d, parent + _stride + h), ref Unsafe.Add(ref d, parent + h),
                       ref Unsafe.Add(ref w, Network.Feature(placed, to, 1) * h),
                       ref Unsafe.Add(ref w, Network.Feature(moved, from, 1) * h), h);
    }

    /// <summary>A capture, en passant included (<paramref name="capturedSquare"/> is then the pawn's, not <paramref name="to"/>).</summary>
    public void PushCapture(Piece moved, int from, Piece placed, int to, Piece captured, int capturedSquare)
    {
        int parent = NextSlot();
        int h = _hidden;
        ref short d = ref MemoryMarshal.GetArrayDataReference(_data);
        ref short w = ref MemoryMarshal.GetArrayDataReference(Network.FeatureWeights);
        Kernels.AddSubSub(ref Unsafe.Add(ref d, parent + _stride), ref Unsafe.Add(ref d, parent),
                          ref Unsafe.Add(ref w, Network.Feature(placed, to, 0) * h),
                          ref Unsafe.Add(ref w, Network.Feature(moved, from, 0) * h),
                          ref Unsafe.Add(ref w, Network.Feature(captured, capturedSquare, 0) * h), h);
        Kernels.AddSubSub(ref Unsafe.Add(ref d, parent + _stride + h), ref Unsafe.Add(ref d, parent + h),
                          ref Unsafe.Add(ref w, Network.Feature(placed, to, 1) * h),
                          ref Unsafe.Add(ref w, Network.Feature(moved, from, 1) * h),
                          ref Unsafe.Add(ref w, Network.Feature(captured, capturedSquare, 1) * h), h);
    }

    /// <summary>Castling: the king and the rook both move.</summary>
    public void PushCastle(Piece king, int kingFrom, int kingTo, Piece rook, int rookFrom, int rookTo)
    {
        int parent = NextSlot();
        int h = _hidden;
        ref short d = ref MemoryMarshal.GetArrayDataReference(_data);
        ref short w = ref MemoryMarshal.GetArrayDataReference(Network.FeatureWeights);
        Kernels.AddAddSubSub(ref Unsafe.Add(ref d, parent + _stride), ref Unsafe.Add(ref d, parent),
                             ref Unsafe.Add(ref w, Network.Feature(king, kingTo, 0) * h),
                             ref Unsafe.Add(ref w, Network.Feature(rook, rookTo, 0) * h),
                             ref Unsafe.Add(ref w, Network.Feature(king, kingFrom, 0) * h),
                             ref Unsafe.Add(ref w, Network.Feature(rook, rookFrom, 0) * h), h);
        Kernels.AddAddSubSub(ref Unsafe.Add(ref d, parent + _stride + h), ref Unsafe.Add(ref d, parent + h),
                             ref Unsafe.Add(ref w, Network.Feature(king, kingTo, 1) * h),
                             ref Unsafe.Add(ref w, Network.Feature(rook, rookTo, 1) * h),
                             ref Unsafe.Add(ref w, Network.Feature(king, kingFrom, 1) * h),
                             ref Unsafe.Add(ref w, Network.Feature(rook, rookFrom, 1) * h), h);
    }

    /// <summary>Make room for a child slot and move to it; returns the parent slot's offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int NextSlot()
    {
        int parent = _top * _stride;
        if (parent + 2 * _stride > _data.Length)
            Array.Resize(ref _data, _data.Length * 2);
        _top++;
        return parent;
    }
}
