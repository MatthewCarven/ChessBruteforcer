using System.Runtime.CompilerServices;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Engine.Nnue;

/// <summary>
/// A position's accumulators, one slot per position on the line from where the network was attached:
/// each slot is white's H numbers, then black's. <see cref="Position.MakeMove"/> writes the child slot as
/// the parent plus the move's 2-4 piece changes, in one pass per perspective; unmake pops. A null move
/// changes no piece, so it shares the slot. Each search thread has its own position, so its own stack;
/// the network's weights are shared, read only. Every slot and column is a bounds-checked span, so a
/// mistake throws rather than writing past the array.
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
        var slots = NextSlots();
        int h = _hidden;
        Kernels.AddSub(slots.Slice(_stride, h), slots[..h], Column(placed, to, 0), Column(moved, from, 0));
        Kernels.AddSub(slots.Slice(_stride + h, h), slots.Slice(h, h), Column(placed, to, 1), Column(moved, from, 1));
    }

    /// <summary>A capture, en passant included (<paramref name="capturedSquare"/> is then the pawn's, not <paramref name="to"/>).</summary>
    public void PushCapture(Piece moved, int from, Piece placed, int to, Piece captured, int capturedSquare)
    {
        var slots = NextSlots();
        int h = _hidden;
        Kernels.AddSubSub(slots.Slice(_stride, h), slots[..h], Column(placed, to, 0), Column(moved, from, 0),
                          Column(captured, capturedSquare, 0));
        Kernels.AddSubSub(slots.Slice(_stride + h, h), slots.Slice(h, h), Column(placed, to, 1), Column(moved, from, 1),
                          Column(captured, capturedSquare, 1));
    }

    /// <summary>Castling: the king and the rook both move.</summary>
    public void PushCastle(Piece king, int kingFrom, int kingTo, Piece rook, int rookFrom, int rookTo)
    {
        var slots = NextSlots();
        int h = _hidden;
        Kernels.AddAddSubSub(slots.Slice(_stride, h), slots[..h], Column(king, kingTo, 0), Column(rook, rookTo, 0),
                             Column(king, kingFrom, 0), Column(rook, rookFrom, 0));
        Kernels.AddAddSubSub(slots.Slice(_stride + h, h), slots.Slice(h, h), Column(king, kingTo, 1), Column(rook, rookTo, 1),
                             Column(king, kingFrom, 1), Column(rook, rookFrom, 1));
    }

    /// <summary>
    /// Make room for a child slot and move to it; returns the parent's slot and the child's, as one span
    /// (bounds-checked: if the room weren't there, this throws instead of anything writing past the array).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<short> NextSlots()
    {
        int parent = _top * _stride;
        if (parent + 2 * _stride > _data.Length)
            Array.Resize(ref _data, _data.Length * 2);
        _top++;
        return _data.AsSpan(parent, 2 * _stride);
    }

    /// <summary>The weight column of a piece on a square, for one perspective (bounds-checked: an empty square has none).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<short> Column(Piece piece, int square, int perspective) =>
        Network.FeatureWeights.AsSpan(Network.Feature(piece, square, perspective) * _hidden, _hidden);
}
