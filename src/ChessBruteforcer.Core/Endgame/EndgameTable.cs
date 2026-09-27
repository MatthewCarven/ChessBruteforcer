using System.IO.MemoryMappedFiles;
using System.Text;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Endgame;

/// <summary>
/// Every position of one material set, solved: win, loss or draw for the
/// side to move, with distance to mate.
///
/// Positions are not stored as boards.  Each one is a number:
///
///   index = ((square(slot 0) * 64 + square(slot 1)) * 64 + ...) * 2 + side to move
///
/// with slot 0 the white king, slot 1 the black king, then white's other
/// pieces and black's, in <see cref="Material"/> order.  The table is just
/// one 16-bit value per index, so the board is implied by where a value
/// sits.  Impossible placements (two pieces on a square, the side not to
/// move in check) are kept as holes, which costs space but keeps the index
/// trivial; symmetry and a tighter index are on the roadmap.
///
/// Solving is retrograde analysis.  Checkmates are losses in 0.  Working
/// outward one ply at a time: a position with a move to a lost position is a
/// win, and a position whose every move reaches a won position is a loss.
/// Whatever is never reached is a draw.  Captures leave the table and are
/// looked up in the smaller table they lead to.
/// </summary>
public sealed class EndgameTable
{
    private const short Illegal = short.MinValue;
    private const short Unknown = short.MaxValue;
    private const string Magic = "CBT1";

    // Either the values are in memory (just solved) or read from the file on demand (loaded).
    private readonly short[]? _values;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly long _dataOffset;

    public Material Material { get; }

    public int SlotCount => Material.PieceCount;

    public long Size { get; }

    private EndgameTable(Material material, short[] values)
    {
        Material = material;
        _values = values;
        Size = values.LongLength;
    }

    private EndgameTable(Material material, MemoryMappedViewAccessor view, long dataOffset, long size)
    {
        Material = material;
        _view = view;
        _dataOffset = dataOffset;
        Size = size;
    }

    /// <summary>The value of every index, from the side to move's point of view (null = impossible).</summary>
    public Outcome? this[long index] => Decode(Raw(index));

    private short Raw(long index) =>
        _values is not null ? _values[index] : _view!.ReadInt16(_dataOffset + index * sizeof(short));

    /// <summary>
    /// Solve a material set.  <paramref name="probeCapture"/>
    /// gives the outcome, for the side to move, of a position reached by a
    /// capture or a promotion (its material differs, so it lives in another table).
    /// </summary>
    public static EndgameTable Solve(Material material, Func<Position, Outcome> probeCapture,
                                     IProgress<string>? progress = null)
    {
        if (!material.IsCanonical)
            throw new ArgumentException($"Solve {material.Canonical}, not {material}.");
        if (material.PieceCount > 4)
            throw new NotSupportedException("More than four pieces needs a tighter index first.");
        return new Solver(material, probeCapture, progress).Run();
    }

    /// <summary>The index of a position of exactly this material (colours already canonical).</summary>
    public long IndexOf(Position position)
    {
        var slots = Layout(Material);
        var squares = new int[slots.Length];
        var used = new bool[64];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            int found = -1;
            for (int square = 0; square < 64; square++)
            {
                if (!used[square] && position[square] == slots[slot])
                {
                    found = square;
                    break;
                }
            }
            if (found < 0)
                throw new ArgumentException($"Position does not match {Material}.");
            used[found] = true;
            squares[slot] = found;
        }
        return Encode(squares, position.SideToMove);
    }

    public Outcome? Probe(Position position) => this[IndexOf(position)];

    public TableStatistics Statistics()
    {
        var stats = new TableStatistics(Material);
        for (long index = 0; index < Size; index++)
        {
            var outcome = Decode(Raw(index));
            if (outcome is null)
                continue;
            var side = (Colour)(index & 1);
            stats.Add(side, outcome.Value, index);
        }
        return stats;
    }

    /// <summary>Rebuild the position an index stands for (it may be an impossible one).</summary>
    public Position PositionAt(long index)
    {
        var slots = Layout(Material);
        var position = Position.Empty((Colour)(index & 1));
        long rest = index >> 1;
        for (int slot = slots.Length - 1; slot >= 0; slot--)
        {
            position.SetPiece((int)(rest % 64), slots[slot]);
            rest /= 64;
        }
        return position;
    }

    public void Save(string path)
    {
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes(Magic));
        writer.Write(Material.ToString());
        writer.Write(Size);
        var values = _values ?? Enumerable.Range(0, checked((int)Size)).Select(i => Raw(i)).ToArray();
        var bytes = new byte[values.Length * sizeof(short)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        writer.Write(bytes);
    }

    /// <summary>
    /// Open a saved table.  By default the file is memory-mapped: opening is
    /// instant and each probe reads just the page it needs.  With
    /// <paramref name="intoMemory"/> the whole table is read in, so later
    /// probes never touch the disk (what a playing engine wants).
    /// </summary>
    public static EndgameTable Load(string path, bool intoMemory = false)
    {
        Material material;
        long count, dataOffset;
        using (var reader = new BinaryReader(File.OpenRead(path), Encoding.ASCII))
        {
            if (Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length)) != Magic)
                throw new InvalidDataException($"{path} is not an endgame table.");
            material = Material.Parse(reader.ReadString());
            count = reader.ReadInt64();
            dataOffset = reader.BaseStream.Position;
            if (count != TableSize(material))
                throw new InvalidDataException($"{path} has {count} entries; {material} needs {TableSize(material)}.");
            if (reader.BaseStream.Length < dataOffset + count * sizeof(short))
                throw new InvalidDataException($"{path} is truncated.");
        }
        if (intoMemory)
        {
            using var stream = File.OpenRead(path);
            stream.Position = dataOffset;
            var values = new short[count];
            stream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
            return new EndgameTable(material, values);
        }
        var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        return new EndgameTable(material, view, dataOffset, count);
    }

    public static long TableSize(Material material) => (1L << (6 * material.PieceCount)) * 2;

    /// <summary>The piece in each slot: kings first, then white's pieces, then black's.</summary>
    internal static Piece[] Layout(Material material) =>
        new[] { new Piece(PieceType.King, Colour.White), new Piece(PieceType.King, Colour.Black) }
            .Concat(material.White.Select(t => new Piece(t, Colour.White)))
            .Concat(material.Black.Select(t => new Piece(t, Colour.Black)))
            .ToArray();

    private static long Encode(int[] squares, Colour sideToMove)
    {
        long index = 0;
        foreach (int square in squares)
            index = index * 64 + square;
        return index * 2 + (int)sideToMove;
    }

    private static Outcome? Decode(short value) => value switch
    {
        Illegal => null,
        Unknown or 0 => Outcome.Draw,
        > 0 => Outcome.Win(value),
        _ => Outcome.Loss(-value - 1),
    };

    private static short EncodeWin(int plies) => checked((short)plies);

    private static short EncodeLoss(int plies) => checked((short)(-plies - 1));

    /// <summary>The retrograde solve for one table, holding its working arrays.</summary>
    private sealed class Solver
    {
        private readonly Material _material;
        private readonly Func<Position, Outcome> _probeCapture;
        private readonly IProgress<string>? _progress;
        private readonly Piece[] _slots;
        private readonly short[] _values;
        private readonly byte[] _remaining;     // moves that stay in this table, not yet known to lose
        private readonly short[] _longestLoss;  // slowest loss seen so far, in plies, if every move loses
        private readonly bool[] _hasDrawingExit;
        private readonly bool[] _final;
        private readonly List<List<long>> _buckets = new();   // non-negative: table index; negative: en passant node
        private readonly List<EnPassantNode> _enPassant = new();
        private readonly Dictionary<long, int> _enPassantByKey = new();          // child index * 64 + e.p. square
        private readonly Dictionary<long, List<int>> _enPassantByChild = new();
        private readonly int[] _squares;
        private readonly Position _scratch = Position.Empty();

        public Solver(Material material, Func<Position, Outcome> probeCapture, IProgress<string>? progress)
        {
            _material = material;
            _probeCapture = probeCapture;
            _progress = progress;
            _slots = Layout(material);
            long size = TableSize(material);
            _values = new short[size];
            _remaining = new byte[size];
            _longestLoss = new short[size];
            _hasDrawingExit = new bool[size];
            _final = new bool[size];
            _squares = new int[_slots.Length];
        }

        public EndgameTable Run()
        {
            Initialise();
            for (int ply = 0; ply < _buckets.Count; ply++)
            {
                // Indexed loop: settling a position can queue more work at this same ply.
                var bucket = _buckets[ply];
                for (int i = 0; i < bucket.Count; i++)
                {
                    long index = bucket[i];
                    if (index < 0)
                    {
                        var node = _enPassant[(int)(-index - 1)];
                        if (node.Final || PliesOf(node.Value) != ply)
                            continue;
                        node.Final = true;
                        PropagateEnPassant(node);
                        continue;
                    }
                    if (_final[index] || PliesOf(_values[index]) != ply)
                        continue;
                    _final[index] = true;
                    Propagate(index, _values[index] > 0);
                    SettleEnPassantNodes(index);
                }
                _progress?.Report($"{_material}: ply {ply} done, {_buckets[ply].Count:N0} queued");
            }
            for (long i = 0; i < _values.LongLength; i++)
            {
                if (_values[i] == Unknown)
                    _values[i] = 0;
            }
            return new EndgameTable(_material, _values);
        }

        /// <summary>
        /// Mark impossible placements, mates and stalemates, count each
        /// position's moves that stay in the table, and settle captures.
        /// </summary>
        private void Initialise()
        {
            var moves = new List<Move>(64);
            for (long index = 0; index < _values.LongLength; index++)
            {
                if (!Load(index))
                {
                    _values[index] = Illegal;
                    _final[index] = true;
                    continue;
                }
                _values[index] = Unknown;
                var position = _scratch;
                Colour mover = position.SideToMove;
                if (position.InCheck(Position.Opponent(mover)))
                {
                    _values[index] = Illegal;
                    _final[index] = true;
                    continue;
                }

                MoveGenerator.Legal(position, moves);
                if (moves.Count == 0)
                {
                    if (position.InCheck(mover))
                    {
                        Queue(index, EncodeLoss(0));
                    }
                    else
                    {
                        _values[index] = 0;
                        _final[index] = true;
                    }
                    continue;
                }

                int bestWin = int.MaxValue;
                int longestLoss = 0;
                int staying = 0;
                foreach (var move in moves)
                {
                    // Captures and promotions change the material: another table.
                    if (!move.IsCapture && !move.IsPromotion)
                    {
                        staying++;
                        if ((move.Flags & MoveFlags.DoublePawnPush) != 0)
                            TrackEnPassant(index, move);
                        continue;
                    }
                    var undo = position.MakeMove(move);
                    var outcome = _probeCapture(position).ForPreviousMover();
                    position.UnmakeMove(move, undo);
                    switch (outcome.Kind)
                    {
                        case OutcomeKind.Win: bestWin = Math.Min(bestWin, outcome.Plies); break;
                        case OutcomeKind.Loss: longestLoss = Math.Max(longestLoss, outcome.Plies); break;
                        default: _hasDrawingExit[index] = true; break;
                    }
                }

                _remaining[index] = checked((byte)staying);
                _longestLoss[index] = (short)longestLoss;
                if (bestWin != int.MaxValue)
                    Queue(index, EncodeWin(bestWin));
                else if (staying == 0 && !_hasDrawingExit[index])
                    Queue(index, EncodeLoss(longestLoss));

                if ((index & 0xFFFFF) == 0)
                    _progress?.Report($"{_material}: initialised {index:N0} / {_values.LongLength:N0}");
            }
        }

        /// <summary>
        /// A position just became final.  Every position that could have
        /// moved into it (without a capture) learns from it.
        /// </summary>
        private void Propagate(long index, bool isWin)
        {
            int ply = PliesOf(_values[index]);
            foreach (long previous in Predecessors(index))
            {
                if (_final[previous])
                    continue;
                if (!isWin)
                {
                    // They can move into a position we lose: they win.
                    short win = EncodeWin(ply + 1);
                    if (_values[previous] == Unknown || _values[previous] > win)
                        Queue(previous, win);
                    continue;
                }

                // One more of their moves turns out to lose.
                _remaining[previous]--;
                _longestLoss[previous] = (short)Math.Max(_longestLoss[previous], ply + 1);
                if (_remaining[previous] == 0 && !_hasDrawingExit[previous] && _values[previous] == Unknown)
                    Queue(previous, EncodeLoss(_longestLoss[previous]));
            }
        }

        /// <summary>
        /// A double push from <paramref name="parent"/> that hands the
        /// opponent an en passant capture.  The position it reaches is worth
        /// more to the opponent than the table's entry for it (which assumes no
        /// en passant): they may also take en passant.  That position gets its
        /// own node, worth the better of the two, and the parent's move leads there.
        /// </summary>
        private void TrackEnPassant(long parent, Move move)
        {
            var position = _scratch;
            var undo = position.MakeMove(move);
            var replies = MoveGenerator.Legal(position);
            var captures = replies.Where(r => (r.Flags & MoveFlags.EnPassant) != 0).ToList();
            if (captures.Count == 0)
            {
                position.UnmakeMove(move, undo);
                return;
            }

            var best = Outcome.Loss(0);
            foreach (var capture in captures)
            {
                var captureUndo = position.MakeMove(capture);
                var outcome = _probeCapture(position).ForPreviousMover();
                position.UnmakeMove(capture, captureUndo);
                if (outcome.Score > best.Score)
                    best = outcome;
            }
            bool childHasOtherMoves = replies.Count > captures.Count;
            position.UnmakeMove(move, undo);

            var childSquares = (int[])_squares.Clone();
            childSquares[Array.IndexOf(_squares, move.From)] = move.To;
            long child = Encode(childSquares, Position.Opponent(position.SideToMove));

            var node = new EnPassantNode(child, parent, best, childHasOtherMoves);
            int id = _enPassant.Count;
            _enPassant.Add(node);
            _enPassantByKey.Add(child * 64 + (move.From + move.To) / 2, id);
            if (!_enPassantByChild.TryGetValue(child, out var list))
                _enPassantByChild[child] = list = new List<int>();
            list.Add(id);

            // With nothing but en passant to play, the capture alone decides.
            if (!childHasOtherMoves)
            {
                if (best.Kind == OutcomeKind.Draw)
                    node.Final = true;
                else
                    QueueEnPassant(id, EncodeOutcome(best));
            }
            else if (best.Kind == OutcomeKind.Win)
            {
                QueueEnPassant(id, EncodeOutcome(best));
            }
        }

        /// <summary>The position behind some en passant nodes is now settled: settle them too.</summary>
        private void SettleEnPassantNodes(long child)
        {
            if (!_enPassantByChild.TryGetValue(child, out var ids))
                return;
            var childValue = Decode(_values[child])!.Value;
            foreach (int id in ids)
            {
                var node = _enPassant[id];
                if (node.Final || !node.ChildHasOtherMoves)
                    continue;
                var value = childValue.Score >= node.Capture.Score ? childValue : node.Capture;
                if (value.Kind == OutcomeKind.Draw)
                {
                    node.Final = true;
                    continue;
                }
                short encoded = EncodeOutcome(value);
                if (node.Value == Unknown || (value.Kind == OutcomeKind.Win && node.Value > encoded))
                    QueueEnPassant(id, encoded);
            }
        }

        /// <summary>An en passant node is settled: its parent learns from it like from any child.</summary>
        private void PropagateEnPassant(EnPassantNode node)
        {
            long parent = node.Parent;
            if (_final[parent])
                return;
            int ply = PliesOf(node.Value);
            if (node.Value < 0)
            {
                short win = EncodeWin(ply + 1);
                if (_values[parent] == Unknown || _values[parent] > win)
                    Queue(parent, win);
                return;
            }
            _remaining[parent]--;
            _longestLoss[parent] = (short)Math.Max(_longestLoss[parent], ply + 1);
            if (_remaining[parent] == 0 && !_hasDrawingExit[parent] && _values[parent] == Unknown)
                Queue(parent, EncodeLoss(_longestLoss[parent]));
        }

        private void QueueEnPassant(int id, short value)
        {
            _enPassant[id].Value = value;
            int ply = PliesOf(value);
            while (_buckets.Count <= ply)
                _buckets.Add(new List<long>());
            _buckets[ply].Add(-(id + 1));
        }

        private static short EncodeOutcome(Outcome outcome) => outcome.Kind switch
        {
            OutcomeKind.Win => EncodeWin(outcome.Plies),
            OutcomeKind.Loss => EncodeLoss(outcome.Plies),
            _ => 0,
        };

        /// <summary>The position right after a double push, with en passant still possible.</summary>
        private sealed class EnPassantNode
        {
            public EnPassantNode(long child, long parent, Outcome capture, bool childHasOtherMoves)
            {
                Child = child;
                Parent = parent;
                Capture = capture;
                ChildHasOtherMoves = childHasOtherMoves;
            }

            public long Child { get; }
            public long Parent { get; }

            /// <summary>The best en passant capture, for the side that may take.</summary>
            public Outcome Capture { get; }

            /// <summary>False when en passant is the only legal move (so it decides alone).</summary>
            public bool ChildHasOtherMoves { get; }

            public short Value { get; set; } = Unknown;
            public bool Final { get; set; }
        }

        private void Queue(long index, short value)
        {
            _values[index] = value;
            int ply = PliesOf(value);
            while (_buckets.Count <= ply)
                _buckets.Add(new List<long>());
            _buckets[ply].Add(index);
        }

        /// <summary>
        /// Positions one non-capturing move before this one: the side that
        /// just moved takes back a move with any of its pieces.
        /// </summary>
        private IEnumerable<long> Predecessors(long index)
        {
            Load(index);
            var position = _scratch;
            var squares = (int[])_squares.Clone();
            Colour sideToMove = position.SideToMove;
            Colour previousMover = Position.Opponent(sideToMove);
            var results = new List<long>();

            for (int slot = 0; slot < _slots.Length; slot++)
            {
                var piece = _slots[slot];
                if (piece.Colour != previousMover)
                    continue;
                int from = squares[slot];
                foreach (int to in RetractionTargets(position, piece, from))
                {
                    // A double push that allowed en passant leads to its node, not straight here.
                    if (piece.Type == PieceType.Pawn && Math.Abs(to - from) == 16
                        && _enPassantByKey.ContainsKey(index * 64 + (to + from) / 2))
                        continue;
                    position.SetPiece(from, Piece.Empty);
                    position.SetPiece(to, piece);
                    position.SetSideToMove(previousMover);
                    // Before the move, the side now to move must not have been in check.
                    if (!position.InCheck(sideToMove))
                    {
                        squares[slot] = to;
                        results.Add(Encode(squares, previousMover));
                        squares[slot] = from;
                    }
                    position.SetPiece(to, Piece.Empty);
                    position.SetPiece(from, piece);
                    position.SetSideToMove(sideToMove);
                }
            }
            return results;
        }

        /// <summary>Empty squares a piece on <paramref name="from"/> could have come from.</summary>
        private static IEnumerable<int> RetractionTargets(Position position, Piece piece, int from)
        {
            var type = piece.Type;
            switch (type)
            {
                case PieceType.King:
                    return Attacks.King[from].Where(s => position[s].IsEmpty);
                case PieceType.Knight:
                    return Attacks.Knight[from].Where(s => position[s].IsEmpty);
                case PieceType.Pawn:
                    return PawnRetractions(position, piece.Colour, from);
            }
            int first = type == PieceType.Bishop ? Attacks.FirstBishopDirection : 0;
            int end = type == PieceType.Rook ? Attacks.FirstBishopDirection : Attacks.DirectionCount;
            var targets = new List<int>();
            for (int d = first; d < end; d++)
            {
                foreach (int square in Attacks.Rays[from][d])
                {
                    if (!position[square].IsEmpty)
                        break;
                    targets.Add(square);
                }
            }
            return targets;
        }

        /// <summary>
        /// A pawn un-pushes one square, or two if it now stands on its
        /// double-push rank and both squares behind are empty.  It can never
        /// have come from its own back rank.
        /// </summary>
        private static IEnumerable<int> PawnRetractions(Position position, Colour colour, int from)
        {
            int back = colour == Colour.White ? -8 : 8;
            int rank = from / 8;
            int homeRank = colour == Colour.White ? 1 : 6;
            int doublePushRank = colour == Colour.White ? 3 : 4;

            int one = from + back;
            if (one / 8 == (colour == Colour.White ? 0 : 7) || !position[one].IsEmpty)
                yield break;
            yield return one;

            int two = one + back;
            if (rank == doublePushRank && two / 8 == homeRank && position[two].IsEmpty)
                yield return two;
        }

        /// <summary>
        /// Set up the scratch position for an index; false if two pieces share
        /// a square or a pawn stands on the first or last rank.
        /// </summary>
        private bool Load(long index)
        {
            for (int square = 0; square < 64; square++)
                _scratch.SetPiece(square, Piece.Empty);
            _scratch.SetSideToMove((Colour)(index & 1));
            long rest = index >> 1;
            for (int slot = _slots.Length - 1; slot >= 0; slot--)
            {
                _squares[slot] = (int)(rest % 64);
                rest /= 64;
            }
            for (int slot = 0; slot < _slots.Length; slot++)
            {
                if (!_scratch[_squares[slot]].IsEmpty)
                    return false;
                if (_slots[slot].Type == PieceType.Pawn && _squares[slot] / 8 is 0 or 7)
                    return false;
                _scratch.SetPiece(_squares[slot], _slots[slot]);
            }
            return true;
        }

        private static int PliesOf(short value) => value > 0 ? value : -value - 1;
    }
}

/// <summary>How a solved table breaks down, per side to move.</summary>
public sealed class TableStatistics
{
    public Material Material { get; }

    public long[] Wins { get; } = new long[2];
    public long[] Losses { get; } = new long[2];
    public long[] Draws { get; } = new long[2];

    /// <summary>The longest forced mate for each side to move, and an index where it happens.</summary>
    public (Outcome Outcome, long Index)?[] Longest { get; } = new (Outcome, long)?[2];

    public TableStatistics(Material material) => Material = material;

    internal void Add(Colour side, Outcome outcome, long index)
    {
        int s = (int)side;
        switch (outcome.Kind)
        {
            case OutcomeKind.Win: Wins[s]++; break;
            case OutcomeKind.Loss: Losses[s]++; break;
            default: Draws[s]++; break;
        }
        if (outcome.Kind != OutcomeKind.Draw && (Longest[s] is null || outcome.Plies > Longest[s]!.Value.Outcome.Plies))
            Longest[s] = (outcome, index);
    }

    public long Legal(Colour side) => Wins[(int)side] + Losses[(int)side] + Draws[(int)side];
}
