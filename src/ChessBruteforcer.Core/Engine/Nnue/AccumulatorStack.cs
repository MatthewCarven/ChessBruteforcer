using System.Runtime.CompilerServices;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Engine.Nnue;

/// <summary>
/// A position's accumulators, one slot per position on the line from where the network was attached:
/// each slot is white's H numbers, then black's. <see cref="Position.MakeMove"/> pushes a child slot that
/// only notes the move's 2-4 piece changes; the slot's numbers are computed from its parent's, in one pass
/// per perspective, when something needs them: an evaluation, a read of <see cref="White"/> or
/// <see cref="Black"/>, or a child pushed on top. So a slot's parent always has its numbers by the time
/// the slot needs them, and a move found illegal, or a node that returns before evaluating (a table hit,
/// a repetition), never pays for its slot: 29% of the pushes in a search (NNUE.md, "Speed at 1024").
/// Unmake pops. A null move changes no piece, so it shares the slot. Each search thread has its own
/// position, so its own stack; the network's weights are shared, read only. Every slot and column is a
/// bounds-checked span, so a mistake throws rather than writing past the array (a move from an empty
/// square throws when its slot is computed). The slots start on a 64-byte boundary (Aligned), and as a
/// slot is 4H bytes, so does every slot, and every half when H is a multiple of 32.
/// </summary>
internal sealed class AccumulatorStack
{
    private enum Kind : byte { Quiet, Capture, Castle }

    /// <summary>The move a slot waits on, as its push was given it.</summary>
    private struct Change
    {
        public Kind Kind;
        public Piece Moved, Placed, Other;       // Other: the piece captured, or the rook
        public byte From, To, OtherFrom, OtherTo; // OtherFrom: where it was captured, or the rook's from; OtherTo: the rook's to
    }

    private readonly int _hidden;
    private readonly int _stride;
    private short[] _data;   // pinned: the slots are the _capacity numbers from _at on
    private int _at;
    private int _capacity;
    private int _top;
    private bool[] _computed;   // per slot: does it hold its numbers yet, or only its change?
    private Change[] _changes;

    public AccumulatorStack(Network network, int slots = 32)
    {
        Network = network;
        _hidden = network.Hidden;
        _stride = 2 * _hidden;
        _capacity = slots * _stride;
        _data = Aligned.Allocate(_capacity, out _at);
        _computed = new bool[slots];
        _changes = new Change[slots];
    }

    public Network Network { get; }

    /// <summary>How many moves deep the current position is from where the stack starts.</summary>
    public int Depth => _top;

    public ReadOnlySpan<short> White
    {
        get
        {
            Compute();
            return Slots.Slice(_top * _stride, _hidden);
        }
    }

    public ReadOnlySpan<short> Black
    {
        get
        {
            Compute();
            return Slots.Slice(_top * _stride + _hidden, _hidden);
        }
    }

    /// <summary>Every slot there is room for.</summary>
    private Span<short> Slots => _data.AsSpan(_at, _capacity);

    /// <summary>Start again from the board: one slot, refreshed.</summary>
    public void Reset(Position position)
    {
        _top = 0;
        Refresh(position);
    }

    /// <summary>The current slot from the board, from scratch (any change it waited on is moot).</summary>
    public void Refresh(Position position)
    {
        var slot = Slots.Slice(_top * _stride, _stride);
        Network.Refresh(position, slot[.._hidden], slot[_hidden..]);
        _computed[_top] = true;
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
        Compute();
        var slot = Slots.Slice(_top * _stride, _stride);
        return sideToMove == Colour.White
            ? Network.Forward(slot[.._hidden], slot[_hidden..])
            : Network.Forward(slot[_hidden..], slot[.._hidden]);
    }

    /// <summary>A move with no capture: <paramref name="moved"/> off <paramref name="from"/>, <paramref name="placed"/> (itself, or what it promotes to) on <paramref name="to"/>.</summary>
    public void PushQuiet(Piece moved, int from, Piece placed, int to) =>
        Push(new Change { Kind = Kind.Quiet, Moved = moved, From = (byte)from, Placed = placed, To = (byte)to });

    /// <summary>A capture, en passant included (<paramref name="capturedSquare"/> is then the pawn's, not <paramref name="to"/>).</summary>
    public void PushCapture(Piece moved, int from, Piece placed, int to, Piece captured, int capturedSquare) =>
        Push(new Change { Kind = Kind.Capture, Moved = moved, From = (byte)from, Placed = placed, To = (byte)to,
                          Other = captured, OtherFrom = (byte)capturedSquare });

    /// <summary>Castling: the king and the rook both move.</summary>
    public void PushCastle(Piece king, int kingFrom, int kingTo, Piece rook, int rookFrom, int rookTo) =>
        Push(new Change { Kind = Kind.Castle, Moved = king, From = (byte)kingFrom, Placed = king, To = (byte)kingTo,
                          Other = rook, OtherFrom = (byte)rookFrom, OtherTo = (byte)rookTo });

    /// <summary>A child slot on top, waiting on its change. The parent gets its numbers first, if it has none yet.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Push(in Change change)
    {
        Compute();
        if ((_top + 2) * _stride > _capacity)
            Grow();
        _top++;
        _changes[_top] = change;
        _computed[_top] = false;
    }

    /// <summary>The top slot's numbers, if it has none yet: its parent's (which it always has) and its change.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Compute()
    {
        if (!_computed[_top])
            ComputeTop();
    }

    private void ComputeTop()
    {
        var slots = Slots.Slice((_top - 1) * _stride, 2 * _stride);   // the parent's, then this one's
        var c = _changes[_top];
        int h = _hidden;
        switch (c.Kind)
        {
            case Kind.Quiet:
                Kernels.AddSub(slots.Slice(_stride, h), slots[..h], Column(c.Placed, c.To, 0), Column(c.Moved, c.From, 0));
                Kernels.AddSub(slots.Slice(_stride + h, h), slots.Slice(h, h), Column(c.Placed, c.To, 1), Column(c.Moved, c.From, 1));
                break;
            case Kind.Capture:
                Kernels.AddSubSub(slots.Slice(_stride, h), slots[..h], Column(c.Placed, c.To, 0), Column(c.Moved, c.From, 0),
                                  Column(c.Other, c.OtherFrom, 0));
                Kernels.AddSubSub(slots.Slice(_stride + h, h), slots.Slice(h, h), Column(c.Placed, c.To, 1), Column(c.Moved, c.From, 1),
                                  Column(c.Other, c.OtherFrom, 1));
                break;
            default:   // castling: the king (Moved, Placed), the rook (Other)
                Kernels.AddAddSubSub(slots.Slice(_stride, h), slots[..h], Column(c.Placed, c.To, 0), Column(c.Other, c.OtherTo, 0),
                                     Column(c.Moved, c.From, 0), Column(c.Other, c.OtherFrom, 0));
                Kernels.AddAddSubSub(slots.Slice(_stride + h, h), slots.Slice(h, h), Column(c.Placed, c.To, 1), Column(c.Other, c.OtherTo, 1),
                                     Column(c.Moved, c.From, 1), Column(c.Other, c.OtherFrom, 1));
                break;
        }
        _computed[_top] = true;
    }

    /// <summary>Twice the room, the slots so far copied over, still on a boundary.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow()
    {
        var bigger = Aligned.Allocate(2 * _capacity, out int at);
        Slots.CopyTo(bigger.AsSpan(at));
        (_data, _at, _capacity) = (bigger, at, 2 * _capacity);
        Array.Resize(ref _computed, _computed.Length * 2);
        Array.Resize(ref _changes, _changes.Length * 2);
    }

    /// <summary>The weight column of a piece on a square, for one perspective (bounds-checked: an empty square has none).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<short> Column(Piece piece, int square, int perspective) =>
        Network.FeatureWeights.Slice(Network.Feature(piece, square, perspective) * _hidden, _hidden);
}
