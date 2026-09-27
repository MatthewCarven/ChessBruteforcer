using System.IO.MemoryMappedFiles;
using System.Text;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Endgame;

/// <summary>
/// Every position of one material set, solved: win, loss or draw for the
/// side to move, with distance to mate.
///
/// Positions are not stored as boards.  Each one is a number, given by
/// <see cref="TableIndex"/>: one per position up to the board's symmetries
/// (8 without pawns, the left-right mirror with them).  The table is just
/// one 16-bit value per index, so the board is implied by where a value
/// sits.  Impossible placements (two pieces on a square, the side not to
/// move in check) and the second images of symmetric ones are holes.
///
/// Files start "CBT2".  "CBT1" files, from before symmetry (every placement
/// of every piece), still load: they are renumbered in memory as they are read.
///
/// Solving is retrograde analysis.  Checkmates are losses in 0.  Working
/// outward one ply at a time: a position with a move to a lost position is a
/// win, and a position whose every move reaches a won position is a loss.
/// Whatever is never reached is a draw.  Captures leave the table and are
/// looked up in the smaller table they lead to.
///
/// A solve can stop at a cap of N plies: every win or loss within N is then
/// exact, and everything else is "beyond N" (a longer win or loss, or a
/// draw).  Such a table is saved as "CBT3", with the solver's working state
/// in a frontier file ("CBF1", <c>.cbf</c>) so it can be extended later
/// from ply N + 1 instead of starting again.  A table that runs out of work
/// before its cap is complete, and is saved as "CBT2" like any other.
///
/// A DTZ table ("CBZ1", <c>.cbz</c>, same index) solves the same positions
/// under the 50-move rule: a win is a win only if the winner can force a
/// capture, pawn move or mate within 100 plies, again and again until mate.
/// Its distances count to the next capture or pawn move, not to mate.
/// </summary>
public sealed class EndgameTable : IDisposable
{
    private const short Illegal = short.MinValue;
    private const short Unknown = short.MaxValue;
    private const string Magic = "CBT2";
    private const string CappedMagic = "CBT3";
    private const string LegacyMagic = "CBT1";
    private const string FrontierMagic = "CBF1";
    private const string DtzMagic = "CBZ1";

    /// <summary>The 50-move rule, in plies: a draw once 100 go by without a capture or pawn move.</summary>
    public const int RulePlies = 100;

    // Either the values are in memory (just solved) or read from the file on demand (loaded).
    // A mapped file stays locked on Windows until the table is disposed.
    private readonly short[]? _values;
    private readonly MemoryMappedFile? _file;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly long _dataOffset;
    private readonly TableIndex _index;

    public Material Material { get; }

    public int SlotCount => Material.PieceCount;

    public long Size { get; }

    /// <summary>For a table solved in this process: what the solver held while it worked (null if loaded).</summary>
    public SolverMemory? SolverMemory { get; private init; }

    /// <summary>Null for a complete table; otherwise the depth in plies it is solved to (see <see cref="OutcomeKind.Beyond"/>).</summary>
    public int? Cap { get; }

    /// <summary>
    /// A DTZ table: results under the 50-move rule (a position is taken with
    /// the count at 0), and distances to the next capture or pawn move.
    /// </summary>
    public bool IsDtz { get; }

    // A capped table solved in this process keeps its solver, so it can be extended without a frontier file.
    private Solver? _frontier;

    public bool HasFrontier => _frontier is not null;

    /// <summary>Let go of the solver's working state (once it is saved), keeping just the values.</summary>
    public void DropFrontier() => _frontier = null;

    private EndgameTable(TableIndex index, short[] values, int? cap = null, bool dtz = false)
    {
        _index = index;
        Material = index.Material;
        _values = values;
        Size = values.LongLength;
        Cap = cap;
        IsDtz = dtz;
    }

    private EndgameTable(TableIndex index, MemoryMappedFile file, MemoryMappedViewAccessor view,
                         long dataOffset, long size, int? cap, bool dtz)
    {
        _index = index;
        Material = index.Material;
        _file = file;
        _view = view;
        _dataOffset = dataOffset;
        Size = size;
        Cap = cap;
        IsDtz = dtz;
    }

    /// <summary>Release a loaded table's file mapping (nothing to do for one held in memory).</summary>
    public void Dispose()
    {
        _view?.Dispose();
        _file?.Dispose();
    }

    /// <summary>The value of every index, from the side to move's point of view (null = impossible).</summary>
    public Outcome? this[long index] => Decode(Raw(index));

    private short Raw(long index) =>
        _values is not null ? _values[index] : _view!.ReadInt16(_dataOffset + index * sizeof(short));

    /// <summary>
    /// Solve a material set.  <paramref name="probeCapture"/>
    /// gives the outcome, for the side to move, of a position reached by a
    /// capture or a promotion (its material differs, so it lives in another table).
    /// With a <paramref name="cap"/>, stop after that many plies (see the class notes);
    /// the tables captures lead to must then be solved to at least cap - 1.
    /// </summary>
    public static EndgameTable Solve(Material material, Func<Position, Outcome> probeCapture,
                                     IProgress<string>? progress = null, int? cap = null)
    {
        if (!material.IsCanonical)
            throw new ArgumentException($"Solve {material.Canonical}, not {material}.");
        if (material.PieceCount > 5)
            throw new NotSupportedException("At most five pieces.");
        if (material.PieceCount == 5 && material.HasPawns)
            throw new NotSupportedException("Five pieces with pawns is ~947 M slots: it needs 48-square pawns or pawn slices first (TODO.md).");
        if (cap < 0)
            throw new ArgumentException("A cap is a number of plies, 0 or more.");
        return new Solver(material, probeCapture, progress).Run(cap);
    }

    /// <summary>
    /// Solve a material set under the 50-move rule (see <see cref="IsDtz"/>).
    /// <paramref name="probeZeroing"/> gives the result under the rule, for the
    /// side to move, of a position reached by a capture or promotion: the
    /// smaller tables' DTZ.  Only win, draw or loss is used from it.
    /// </summary>
    public static EndgameTable SolveDtz(Material material, Func<Position, Outcome> probeZeroing,
                                        IProgress<string>? progress = null)
    {
        if (!material.IsCanonical)
            throw new ArgumentException($"Solve {material.Canonical}, not {material}.");
        if (material.PieceCount > 5)
            throw new NotSupportedException("At most five pieces.");
        if (material.PieceCount == 5 && material.HasPawns)
            throw new NotSupportedException("Five pieces with pawns needs slices with their own memory first (TODO.md).");
        return new Solver(material, probeZeroing, progress, zeroing: true).RunDtz();
    }

    /// <summary>
    /// Carry a capped table solved in this process on to a deeper cap (null:
    /// to the end).  The old table is spent: its values are the new table's.
    /// </summary>
    public static EndgameTable Extend(EndgameTable table, Func<Position, Outcome> probeCapture,
                                      int? cap, IProgress<string>? progress = null)
    {
        var solver = table._frontier
                     ?? throw new InvalidOperationException($"{table.Material} has no solver state in memory.");
        table._frontier = null;
        return solver.Extend(probeCapture, progress, cap);
    }

    /// <summary>
    /// Carry a capped table on from its files: the table and the frontier
    /// saved beside it.  Null when the pair doesn't match (a frontier missing,
    /// or left from another cap), and the table has to be solved again.
    /// </summary>
    public static EndgameTable? ExtendFromFiles(string tablePath, string frontierPath,
                                                Func<Position, Outcome> probeCapture, int? cap,
                                                IProgress<string>? progress = null)
    {
        var header = ReadHeader(tablePath);
        if (header.Cap is not int oldCap || !File.Exists(frontierPath))
            return null;
        var values = ReadValues(tablePath, header.DataOffset, header.Count);
        var solver = Solver.LoadFrontier(frontierPath, header.Material, values, oldCap, probeCapture, progress);
        return solver?.Extend(probeCapture, progress, cap);
    }

    /// <summary>Write the solver's working state for a capped table solved in this process.</summary>
    public void SaveFrontier(string path)
    {
        var solver = _frontier ?? throw new InvalidOperationException($"{Material} has no solver state to save.");
        string temp = path + ".tmp";
        using (var writer = new BinaryWriter(File.Create(temp), Encoding.ASCII))
            solver.WriteFrontier(writer);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The index of a position of exactly this material (colours already canonical).</summary>
    public long IndexOf(Position position)
    {
        var slots = _index.Slots;
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
        long index = _index.Encode(squares, position.SideToMove);
        if (index < 0)
            throw new ArgumentException("The kings stand on the same or neighbouring squares.");
        return index;
    }

    public Outcome? Probe(Position position) => this[IndexOf(position)];

    public TableStatistics Statistics()
    {
        // Each index stands for every symmetric image of its position, so it
        // counts that many times: the totals are over real placements.
        var stats = new TableStatistics(Material);
        var squares = new int[_index.Slots.Length];
        for (long index = 0; index < Size; index++)
        {
            var outcome = Decode(Raw(index));
            if (outcome is null || !_index.Decode(index, squares, out var side))
                continue;
            stats.Add(side, outcome.Value, index, _index.Weight(squares));
        }
        return stats;
    }

    /// <summary>
    /// Set against the same material's DTZ table: per side to move, the
    /// positions won with best play that the 50-move rule turns into draws
    /// ("cursed wins"), and the losses it saves ("blessed losses").  Counted
    /// over real placements, like <see cref="Statistics"/>.
    /// </summary>
    public (long[] CursedWins, long[] BlessedLosses) RuleDraws(EndgameTable dtz)
    {
        if (!dtz.IsDtz || dtz.Material != Material || IsDtz || Cap is not null)
            throw new ArgumentException("Compare a complete table with the DTZ table of the same material.");
        var cursed = new long[2];
        var blessed = new long[2];
        var squares = new int[_index.Slots.Length];
        for (long index = 0; index < Size; index++)
        {
            if (Decode(Raw(index)) is not { } full || dtz.Decode(dtz.Raw(index)) is not { Kind: OutcomeKind.Draw })
                continue;
            if (full.Kind is OutcomeKind.Draw || !_index.Decode(index, squares, out var side))
                continue;
            (full.Kind == OutcomeKind.Win ? cursed : blessed)[(int)side] += _index.Weight(squares);
        }
        return (cursed, blessed);
    }

    /// <summary>Rebuild the position an index stands for (it may be an impossible one).</summary>
    public Position PositionAt(long index)
    {
        var squares = new int[_index.Slots.Length];
        _index.Decode(index, squares, out var side);
        var position = Position.Empty(side);
        for (int slot = 0; slot < squares.Length; slot++)
            position.SetPiece(squares[slot], _index.Slots[slot]);
        return position;
    }

    /// <summary>Write the table; a temporary file is renamed at the end, so an interrupted save leaves nothing half-written.</summary>
    public void Save(string path)
    {
        string temp = path + ".tmp";
        using (var writer = new BinaryWriter(File.Create(temp), Encoding.ASCII))
            WriteTo(writer);
        File.Move(temp, path, overwrite: true);
    }

    private void WriteTo(BinaryWriter writer)
    {
        writer.Write(Encoding.ASCII.GetBytes(IsDtz ? DtzMagic : Cap is null ? Magic : CappedMagic));
        writer.Write(Material.ToString());
        writer.Write(Size);
        if (Cap is int cap)
            writer.Write(cap);
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
        var (material, count, dataOffset, legacy, cap, dtz) = ReadHeader(path);
        var index = new TableIndex(material);
        if (legacy)
            return FromLegacy(index, ReadValues(path, dataOffset, count));
        if (intoMemory)
            return new EndgameTable(index, ReadValues(path, dataOffset, count), cap, dtz);
        var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        return new EndgameTable(index, file, view, dataOffset, count, cap, dtz);
    }

    private readonly record struct Header(Material Material, long Count, long DataOffset, bool Legacy, int? Cap, bool Dtz);

    private static Header ReadHeader(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.ASCII);
        string magic = Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length));
        if (magic != Magic && magic != CappedMagic && magic != LegacyMagic && magic != DtzMagic)
            throw new InvalidDataException($"{path} is not an endgame table.");
        bool legacy = magic == LegacyMagic;
        var material = Material.Parse(reader.ReadString());
        long count = reader.ReadInt64();
        int? cap = magic == CappedMagic ? reader.ReadInt32() : null;
        long dataOffset = reader.BaseStream.Position;
        long expected = legacy ? LegacySize(material) : TableSize(material);
        if (count != expected)
            throw new InvalidDataException($"{path} has {count} entries; {material} needs {expected}.");
        if (reader.BaseStream.Length < dataOffset + count * sizeof(short))
            throw new InvalidDataException($"{path} is truncated.");
        return new Header(material, count, dataOffset, legacy, cap, magic == DtzMagic);
    }

    /// <summary>True for a table saved before symmetry ("CBT1"): it loads, but saving it again makes it ~2-9 times smaller.</summary>
    public static bool IsLegacyFile(string path)
    {
        using var stream = File.OpenRead(path);
        var magic = new byte[LegacyMagic.Length];
        return stream.Read(magic) == magic.Length && Encoding.ASCII.GetString(magic) == LegacyMagic;
    }

    private static short[] ReadValues(string path, long offset, long count)
    {
        using var stream = File.OpenRead(path);
        stream.Position = offset;
        var values = new short[count];
        stream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    /// <summary>
    /// Renumber a table from the first format, where the index was simply
    /// every piece's square in slot order: ((s0 * 64 + s1) * 64 + ...) * 2 + side.
    /// </summary>
    private static EndgameTable FromLegacy(TableIndex index, short[] legacy)
    {
        var values = new short[index.Size];
        var squares = new int[index.Slots.Length];
        for (long i = 0; i < values.LongLength; i++)
        {
            if (!index.Decode(i, squares, out var side))
            {
                values[i] = Illegal;
                continue;
            }
            long raw = 0;
            foreach (int square in squares)
                raw = raw * 64 + square;
            values[i] = legacy[raw * 2 + (int)side];
        }
        return new EndgameTable(index, values);
    }

    public static long TableSize(Material material) => new TableIndex(material).Size;

    private static long LegacySize(Material material) => (1L << (6 * material.PieceCount)) * 2;

    /// <summary>The piece in each slot: kings first, then white's pieces, then black's.</summary>
    internal static Piece[] Layout(Material material) =>
        new[] { new Piece(PieceType.King, Colour.White), new Piece(PieceType.King, Colour.Black) }
            .Concat(material.White.Select(t => new Piece(t, Colour.White)))
            .Concat(material.Black.Select(t => new Piece(t, Colour.Black)))
            .ToArray();

    /// <summary>A stored value as this table means it: past its cap, anything unsettled is beyond the cap.</summary>
    private Outcome? Decode(short value)
    {
        var outcome = DecodeRaw(value);
        if (Cap is int cap && outcome is { } known && (value == Unknown || known.Plies > cap))
            return Outcome.Beyond(cap);
        return outcome;
    }

    /// <summary>A stored value on its own (in a complete table, unknown is a draw).</summary>
    private static Outcome? DecodeRaw(short value) => value switch
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
        private readonly TableIndex _index;
        private Func<Position, Outcome> _probeCapture;
        private IProgress<string>? _progress;
        private int? _cap;
        private int _ply;   // the bucket being worked through; every value at a lower ply is final
        private readonly bool _zeroing;   // DTZ: captures and pawn moves are exits, distances count to the next one
        // Queue entries are 4 bytes: a table index below this, an en passant node from it upward.
        private const uint EnPassantEntry = 1u << 31;

        private readonly Piece[] _slots;
        private readonly short[] _values;
        private readonly byte[] _remaining;     // moves that stay in this table, not yet known to lose
        private readonly short[] _longestLoss;  // slowest loss seen so far, in plies, if every move loses
        private readonly BitSet _hasDrawingExit;
        // A capture into a capped table that it hasn't settled: this position can
        // still be proven a win, but not a loss, until the capture is probed again.
        private readonly BitSet _hasUnknownExit;
        private readonly BitSet _final;
        private readonly List<List<uint>> _buckets = new();   // a table index, or an en passant node with the top bit set
        private readonly List<EnPassantNode> _enPassant = new();
        private readonly Dictionary<long, int> _enPassantByKey = new();          // child index * 64 + e.p. square
        private readonly Dictionary<long, List<int>> _enPassantByChild = new();
        private readonly int[] _squares;
        private readonly int[] _childSquares;
        private readonly List<long> _children = new();
        private readonly Position _scratch = Position.Empty();

        public Solver(Material material, Func<Position, Outcome> probeCapture, IProgress<string>? progress,
                      short[]? values = null, bool zeroing = false)
        {
            _material = material;
            _zeroing = zeroing;
            _probeCapture = probeCapture;
            _progress = progress;
            _index = new TableIndex(material);
            _slots = _index.Slots;
            long size = _index.Size;
            if (size > EnPassantEntry)
                throw new NotSupportedException($"{material} has {size:N0} slots; queue entries hold at most {EnPassantEntry:N0}.");
            _values = values ?? new short[size];
            _remaining = new byte[size];
            _longestLoss = new short[size];
            _hasDrawingExit = new BitSet(size);
            _hasUnknownExit = new BitSet(size);
            _final = new BitSet(size);
            _squares = new int[_slots.Length];
            _childSquares = new int[_slots.Length];
        }

        public EndgameTable Run(int? cap)
        {
            _cap = cap;
            Initialise();
            return Continue();
        }

        /// <summary>
        /// Carry on to a deeper cap.  Captures into tables that were capped
        /// are probed again first (those tables must have been extended
        /// already): whatever they now settle lies beyond the old cap, so it
        /// lands in buckets not yet worked through.
        /// </summary>
        public EndgameTable Extend(Func<Position, Outcome> probeCapture, IProgress<string>? progress, int? cap)
        {
            if (cap is int newCap && newCap < _cap)
                throw new ArgumentException($"{_material} is already solved to {_cap} plies.");
            _probeCapture = probeCapture;
            _progress = progress;
            _cap = cap;
            Reprobe();
            return Continue();
        }

        /// <summary>
        /// The 50-move rule (DTZ).  Captures, promotions and pawn moves reset
        /// the count, so they are exits, worth only whether the position
        /// they reach is won, drawn or lost under the rule.  Between them,
        /// distances count plies to the next such move, and a side that can't
        /// force one (or mate) within 100 plies can't win: the solve stops
        /// at 100 and whatever is left is a draw.
        ///
        /// Pawn moves stay in the table, so it is solved in slices, one pawn
        /// placement (and its mirror image) at a time.  Pawns only move
        /// forward, so taking the most advanced placements first means every
        /// pawn move leads into a slice already solved.
        /// </summary>
        public EndgameTable RunDtz()
        {
            _cap = RulePlies;
            var moves = new List<Move>(64);
            var slices = Slices();
            long done = 0;
            foreach (var slice in slices)
            {
                _ply = 0;
                _buckets.Clear();
                if (slice is null)
                {
                    for (long index = 0; index < _values.LongLength; index++)
                        InitialisePosition(index, moves);
                }
                else
                {
                    foreach (uint index in slice)
                        InitialisePosition(index, moves);
                }
                RunBuckets();
                // Anything not settled within 100 plies is a draw under the rule.
                if (slice is null)
                {
                    for (long index = 0; index < _values.LongLength; index++)
                        CloseUnderRule(index);
                }
                else
                {
                    foreach (uint index in slice)
                        CloseUnderRule(index);
                }
                done += slice?.Count ?? _values.LongLength;
                if (slices.Count > 1)
                    _progress?.Report($"{_material}: DTZ, {done:N0} positions done");
            }
            return new EndgameTable(_index, _values, dtz: true) { SolverMemory = Measure() };
        }

        private void CloseUnderRule(long index)
        {
            if (_final[index])
                return;
            _values[index] = 0;
            _final[index] = true;
        }

        /// <summary>
        /// The slices, most advanced pawns first: each one's positions, or a
        /// single null for "the whole table" when there are no pawns.  Holes
        /// are marked impossible here and belong to no slice.
        /// </summary>
        private List<List<uint>?> Slices()
        {
            var pawnSlots = Enumerable.Range(0, _slots.Length).Where(s => _slots[s].Type == PieceType.Pawn).ToArray();
            if (pawnSlots.Length == 0)
                return new List<List<uint>?> { null };
            var slices = new Dictionary<long, List<uint>>();
            var progress = new Dictionary<long, int>();
            for (long index = 0; index < _values.LongLength; index++)
            {
                if (!_index.Decode(index, _squares, out _))
                {
                    _values[index] = Illegal;
                    _final[index] = true;
                    continue;
                }
                long key = Math.Min(PawnKey(pawnSlots, mirror: false), PawnKey(pawnSlots, mirror: true));
                if (!slices.TryGetValue(key, out var slice))
                {
                    slices[key] = slice = new List<uint>();
                    // How far the pawns have come: the same for a placement and its mirror image.
                    progress[key] = pawnSlots.Sum(s => _slots[s].Colour == Colour.White ? _squares[s] / 8 : 7 - _squares[s] / 8);
                }
                slice.Add((uint)index);
            }
            return slices.OrderByDescending(pair => progress[pair.Key]).ThenBy(pair => pair.Key)
                         .Select(pair => (List<uint>?)pair.Value).ToList();
        }

        /// <summary>The pawns' squares (white's then black's, each sorted) as one number, optionally mirrored left to right.</summary>
        private long PawnKey(int[] pawnSlots, bool mirror)
        {
            long key = 0;
            foreach (var colour in new[] { Colour.White, Colour.Black })
            {
                foreach (int square in pawnSlots.Where(s => _slots[s].Colour == colour)
                                                .Select(s => mirror ? _squares[s] ^ 7 : _squares[s]).Order())
                    key = key * 64 + square;
                key = key * 64 + 63;   // a separator: no pawn stands on h8
            }
            return key;
        }

        private static bool IsZeroing(Position position, Move move) =>
            move.IsCapture || move.IsPromotion || position[move.From].Type == PieceType.Pawn;

        /// <summary>
        /// A capture or pawn move, for the side that makes it: win, draw or
        /// loss under the rule for the position it reaches (the count starts
        /// again there, so only that matters), as a result one ply away.
        /// </summary>
        private Outcome ZeroingExit(Position position, Move move)
        {
            bool staysHere = !move.IsCapture && !move.IsPromotion;
            long child = staysHere ? ChildOf(move) : -1;   // before the move: ChildOf reads the side to move
            var undo = position.MakeMove(move);
            Outcome reached;   // for the side to move after it
            if (!staysHere)
            {
                reached = _probeCapture(position);
            }
            else
            {
                // A pawn push stays here, in a slice already solved.
                if (!_final[child])
                    throw new InvalidOperationException($"{_material}: a pawn move leads into a slice not yet solved.");
                reached = DecodeRaw(_values[child])!.Value;
                if ((move.Flags & MoveFlags.DoublePawnPush) != 0)
                    reached = WithEnPassant(position, reached);
            }
            position.UnmakeMove(move, undo);
            if (reached.Kind == OutcomeKind.Beyond)
                throw new InvalidOperationException($"{_material}: DTZ needs the smaller tables' DTZ, not a capped table.");
            return new Outcome(reached.Kind, 0).ForPreviousMover();
        }

        /// <summary>After a double push the opponent may take en passant: the better of that and the table's value.</summary>
        private Outcome WithEnPassant(Position position, Outcome stored)
        {
            var replies = MoveGenerator.Legal(position);
            Outcome? best = null;
            int captures = 0;
            foreach (var reply in replies)
            {
                if ((reply.Flags & MoveFlags.EnPassant) == 0)
                    continue;
                var undo = position.MakeMove(reply);
                var outcome = new Outcome(_probeCapture(position).Kind, 0).ForPreviousMover();
                position.UnmakeMove(reply, undo);
                best = best is { } sofar ? Outcome.Better(sofar, outcome) : outcome;
                captures++;
            }
            if (best is not { } capture)
                return stored;
            return captures == replies.Count ? capture : Outcome.Better(new Outcome(stored.Kind, 0), capture);
        }

        private EndgameTable Continue()
        {
            RunBuckets();
            if (!IsComplete())
                return new EndgameTable(_index, _values, _cap) { SolverMemory = Measure(), _frontier = this };
            for (long i = 0; i < _values.LongLength; i++)
            {
                if (_values[i] == Unknown)
                    _values[i] = 0;
            }
            return new EndgameTable(_index, _values) { SolverMemory = Measure() };
        }

        private void RunBuckets()
        {
            for (; _ply < _buckets.Count && (_cap is not int cap || _ply <= cap); _ply++)
            {
                // Indexed loop: settling a position can queue more work at this same ply.
                var bucket = _buckets[_ply];
                for (int i = 0; i < bucket.Count; i++)
                {
                    uint entry = bucket[i];
                    if (!IsLive(entry, _ply))
                        continue;
                    if (entry >= EnPassantEntry)
                    {
                        var node = _enPassant[(int)(entry - EnPassantEntry)];
                        node.Final = true;
                        PropagateEnPassant(node);
                        continue;
                    }
                    long index = entry;
                    _final[index] = true;
                    _hasUnknownExit[index] = false;   // settled: its other exits no longer matter
                    Propagate(index, _values[index] > 0);
                    SettleEnPassantNodes(index);
                }
                if (!_zeroing)   // DTZ runs a hundred-odd plies per slice, over a thousand slices
                    _progress?.Report($"{_material}: ply {_ply} done, {_buckets[_ply].Count:N0} queued");
            }
        }

        /// <summary>A queue entry still to be acted on: not settled already, and not superseded by a quicker value.</summary>
        private bool IsLive(uint entry, int ply)
        {
            if (entry >= EnPassantEntry)
            {
                var node = _enPassant[(int)(entry - EnPassantEntry)];
                return !node.Final && PliesOf(node.Value) == ply;
            }
            return !_final[entry] && PliesOf(_values[entry]) == ply;
        }

        /// <summary>
        /// Nothing left to learn: no queued work, and no capture waiting on a
        /// capped table.  Whatever is still unknown can then only be a draw.
        /// </summary>
        private bool IsComplete()
        {
            if (_hasUnknownExit.NextSetBit(0) >= 0)
                return false;
            if (_enPassant.Any(node => !node.Final && node.Capture.Kind == OutcomeKind.Beyond))
                return false;
            for (int ply = _ply; ply < _buckets.Count; ply++)
            {
                foreach (uint entry in _buckets[ply])
                {
                    if (IsLive(entry, ply))
                        return false;
                }
            }
            return true;
        }

        /// <summary>The working arrays, and the queues at their largest (they are only freed when the solve ends).</summary>
        private SolverMemory Measure()
        {
            long size = _values.LongLength;
            long arrays = size * (sizeof(short) + sizeof(byte) + sizeof(short))
                          + _hasDrawingExit.ByteCount + _hasUnknownExit.ByteCount + _final.ByteCount;
            long entries = _buckets.Sum(b => (long)b.Count);
            long queueBytes = _buckets.Sum(b => (long)b.Capacity) * sizeof(uint);
            return new SolverMemory(arrays, queueBytes, entries);
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
                InitialisePosition(index, moves);
                if ((index & 0xFFFFF) == 0)
                    _progress?.Report($"{_material}: initialised {index:N0} / {_values.LongLength:N0}");
            }
        }

        private void InitialisePosition(long index, List<Move> moves)
        {
            if (!Load(index))
            {
                _values[index] = Illegal;
                _final[index] = true;
                return;
            }
            _values[index] = Unknown;
            var position = _scratch;
            Colour mover = position.SideToMove;
            if (position.InCheck(Position.Opponent(mover)))
            {
                _values[index] = Illegal;
                _final[index] = true;
                return;
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
                return;
            }

            int bestWin = int.MaxValue;
            int longestLoss = 0;
            _children.Clear();
            foreach (var move in moves)
            {
                Outcome outcome;
                if (_zeroing && IsZeroing(position, move))
                {
                    // Under the 50-move rule a capture or pawn move is an exit: the count starts again.
                    outcome = ZeroingExit(position, move);
                }
                else if (!move.IsCapture && !move.IsPromotion)
                {
                    // Count children, not moves: in a symmetric position two moves can
                    // reach mirror images of one position, which is one index.
                    // (Predecessors are counted the same way, so the two tallies agree.)
                    int node = (move.Flags & MoveFlags.DoublePawnPush) != 0 ? TrackEnPassant(index, move) : -1;
                    long child = node >= 0 ? -(node + 1L) : ChildOf(move);
                    if (!_children.Contains(child))
                        _children.Add(child);
                    continue;
                }
                else
                {
                    // Captures and promotions change the material: another table.
                    var undo = position.MakeMove(move);
                    outcome = ProbeCapture(position);
                    position.UnmakeMove(move, undo);
                }
                switch (outcome.Kind)
                {
                    case OutcomeKind.Win: bestWin = Math.Min(bestWin, outcome.Plies); break;
                    case OutcomeKind.Loss: longestLoss = Math.Max(longestLoss, outcome.Plies); break;
                    case OutcomeKind.Beyond: _hasUnknownExit[index] = true; break;
                    default: _hasDrawingExit[index] = true; break;
                }
            }

            _remaining[index] = checked((byte)_children.Count);
            _longestLoss[index] = (short)longestLoss;
            if (bestWin != int.MaxValue)
                Queue(index, EncodeWin(bestWin));
            else if (CanOnlyLose(index))
                Queue(index, EncodeLoss(longestLoss));
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
                if (CanOnlyLose(previous))
                    Queue(previous, EncodeLoss(_longestLoss[previous]));
            }
        }

        /// <summary>Every move is known to lose: none stays in the table unsettled, and no capture draws or might.</summary>
        private bool CanOnlyLose(long index) =>
            _remaining[index] == 0 && !_hasDrawingExit[index] && !_hasUnknownExit[index] && _values[index] == Unknown;

        /// <summary>
        /// A capture or promotion, for the side that makes it.  A capped
        /// table can answer "beyond N"; that is only good enough if N reaches
        /// this solve's own cap (a win or loss through it would be longer).
        /// </summary>
        private Outcome ProbeCapture(Position position)
        {
            var outcome = _probeCapture(position).ForPreviousMover();
            if (outcome.Kind == OutcomeKind.Beyond && (_cap is not int cap || outcome.Plies < cap))
                throw new InvalidOperationException(
                    $"{_material} (to {(_cap is null ? "the end" : $"{_cap} plies")}) captures into a table solved only to " +
                    $"{outcome.Plies - 1} plies: solve that one deeper first.");
            return outcome;
        }

        /// <summary>
        /// Probe again every capture that a capped table couldn't settle
        /// (positions with an unknown exit, and en passant nodes).
        /// </summary>
        private void Reprobe()
        {
            var moves = new List<Move>(64);
            for (long index = _hasUnknownExit.NextSetBit(0); index >= 0; index = _hasUnknownExit.NextSetBit(index + 1))
            {
                _hasUnknownExit[index] = false;
                if (_final[index])
                    continue;
                Load(index);
                var position = _scratch;
                MoveGenerator.Legal(position, moves);
                int bestWin = int.MaxValue;
                foreach (var move in moves)
                {
                    if (!move.IsCapture && !move.IsPromotion)
                        continue;
                    var undo = position.MakeMove(move);
                    var outcome = ProbeCapture(position);
                    position.UnmakeMove(move, undo);
                    switch (outcome.Kind)
                    {
                        case OutcomeKind.Win: bestWin = Math.Min(bestWin, outcome.Plies); break;
                        case OutcomeKind.Loss:
                            _longestLoss[index] = (short)Math.Max(_longestLoss[index], outcome.Plies);
                            break;
                        case OutcomeKind.Beyond: _hasUnknownExit[index] = true; break;
                        default: _hasDrawingExit[index] = true; break;
                    }
                }
                if (bestWin != int.MaxValue)
                {
                    short win = EncodeWin(bestWin);
                    if (_values[index] == Unknown || _values[index] > win)
                        Queue(index, win);
                }
                else if (CanOnlyLose(index))
                {
                    Queue(index, EncodeLoss(_longestLoss[index]));
                }
            }

            for (int id = 0; id < _enPassant.Count; id++)
            {
                var node = _enPassant[id];
                if (node.Final || node.Capture.Kind != OutcomeKind.Beyond)
                    continue;
                Load(node.Parent);
                var push = MoveGenerator.Legal(_scratch).First(m => (int)m.From == node.From && (int)m.To == node.To);
                var undo = _scratch.MakeMove(push);
                node.Capture = EnPassantCaptures(_scratch).Best!.Value;
                _scratch.UnmakeMove(push, undo);
                if (!node.ChildHasOtherMoves)
                    SettleByCaptureAlone(id);
                else if (_final[node.Child])
                    SettleEnPassantNode(id, DecodeRaw(_values[node.Child])!.Value);
                else if (node.Capture.Kind == OutcomeKind.Win)
                    QueueEnPassant(id, EncodeOutcome(node.Capture));
            }
        }

        /// <summary>The index a non-capturing move from the loaded position leads to.</summary>
        private long ChildOf(Move move)
        {
            _squares.CopyTo(_childSquares, 0);
            _childSquares[Array.IndexOf(_squares, (int)move.From)] = move.To;
            return _index.Encode(_childSquares, Position.Opponent(_scratch.SideToMove));
        }

        /// <summary>
        /// A double push from <paramref name="parent"/> that hands the
        /// opponent an en passant capture.  Returns the node's number, or -1 if no capture is possible.  The position it reaches is worth
        /// more to the opponent than the table's entry for it (which assumes no
        /// en passant): they may also take en passant.  That position gets its
        /// own node, worth the better of the two, and the parent's move leads there.
        /// </summary>
        private int TrackEnPassant(long parent, Move move)
        {
            var position = _scratch;
            var undo = position.MakeMove(move);
            var (best, childHasOtherMoves) = EnPassantCaptures(position);
            position.UnmakeMove(move, undo);
            if (best is not { } capture)
                return -1;

            var childSquares = (int[])_squares.Clone();
            childSquares[Array.IndexOf(_squares, (int)move.From)] = move.To;
            long child = _index.Encode(childSquares, Position.Opponent(position.SideToMove), out int symmetry);
            int passedSquare = _index.Map(symmetry, (move.From + move.To) / 2);   // in the child's own frame

            var node = new EnPassantNode(child, parent, child * 64 + passedSquare, move.From, move.To,
                                         capture, childHasOtherMoves);
            int id = _enPassant.Count;
            _enPassant.Add(node);
            Register(id);

            if (!childHasOtherMoves)
                SettleByCaptureAlone(id);
            else if (capture.Kind == OutcomeKind.Win)
                QueueEnPassant(id, EncodeOutcome(capture));
            return id;
        }

        private void Register(int id)
        {
            var node = _enPassant[id];
            _enPassantByKey.Add(node.Key, id);
            if (!_enPassantByChild.TryGetValue(node.Child, out var list))
                _enPassantByChild[node.Child] = list = new List<int>();
            list.Add(id);
        }

        /// <summary>
        /// Right after a double push: the best en passant capture for the side
        /// to move (null if there is none), and whether it has other moves.
        /// </summary>
        private (Outcome? Best, bool HasOtherMoves) EnPassantCaptures(Position position)
        {
            var replies = MoveGenerator.Legal(position);
            Outcome? best = null;
            int captures = 0;
            foreach (var reply in replies)
            {
                if ((reply.Flags & MoveFlags.EnPassant) == 0)
                    continue;
                var undo = position.MakeMove(reply);
                var outcome = ProbeCapture(position);
                position.UnmakeMove(reply, undo);
                best = best is { } sofar ? Outcome.Better(sofar, outcome) : outcome;
                captures++;
            }
            return (best, replies.Count > captures);
        }

        /// <summary>With nothing but en passant to play, the capture alone decides (unless it is beyond the cap).</summary>
        private void SettleByCaptureAlone(int id)
        {
            var node = _enPassant[id];
            if (node.Capture.Kind == OutcomeKind.Draw)
                node.Final = true;
            else if (node.Capture.Kind != OutcomeKind.Beyond)
                QueueEnPassant(id, EncodeOutcome(node.Capture));
        }

        /// <summary>The position behind some en passant nodes is now settled: settle them too.</summary>
        private void SettleEnPassantNodes(long child)
        {
            if (!_enPassantByChild.TryGetValue(child, out var ids))
                return;
            var childValue = DecodeRaw(_values[child])!.Value;
            foreach (int id in ids)
                SettleEnPassantNode(id, childValue);
        }

        private void SettleEnPassantNode(int id, Outcome childValue)
        {
            var node = _enPassant[id];
            if (node.Final || !node.ChildHasOtherMoves)
                return;
            var value = Outcome.Better(childValue, node.Capture);
            if (value.Kind == OutcomeKind.Beyond)
                return;   // waits for the capture to be probed again
            if (value.Kind == OutcomeKind.Draw)
            {
                node.Final = true;
                return;
            }
            short encoded = EncodeOutcome(value);
            if (node.Value == Unknown || (value.Kind == OutcomeKind.Win && node.Value > encoded))
                QueueEnPassant(id, encoded);
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
            if (CanOnlyLose(parent))
                Queue(parent, EncodeLoss(_longestLoss[parent]));
        }

        private void QueueEnPassant(int id, short value)
        {
            _enPassant[id].Value = value;
            BucketFor(PliesOf(value)).Add(EnPassantEntry + (uint)id);
        }

        /// <summary>The queue for a ply.  Work behind the current ply would never be done, so that is a bug.</summary>
        private List<uint> BucketFor(int ply)
        {
            if (ply < _ply)
                throw new InvalidOperationException($"{_material}: queued at ply {ply}, but ply {_ply} is already under way.");
            while (_buckets.Count <= ply)
                _buckets.Add(new List<uint>());
            return _buckets[ply];
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
            public EnPassantNode(long child, long parent, long key, int from, int to, Outcome capture,
                                 bool childHasOtherMoves)
            {
                Child = child;
                Parent = parent;
                Key = key;
                From = (byte)from;
                To = (byte)to;
                Capture = capture;
                ChildHasOtherMoves = childHasOtherMoves;
            }

            public long Child { get; }
            public long Parent { get; }

            /// <summary>Child index * 64 + the passed square, in the child's frame.</summary>
            public long Key { get; }

            /// <summary>The double push, in the parent's frame (to replay it when probing again).</summary>
            public byte From { get; }
            public byte To { get; }

            /// <summary>The best en passant capture, for the side that may take (beyond the cap until probed again).</summary>
            public Outcome Capture { get; set; }

            /// <summary>False when en passant is the only legal move (so it decides alone).</summary>
            public bool ChildHasOtherMoves { get; }

            public short Value { get; set; } = Unknown;
            public bool Final { get; set; }
        }

        private void Queue(long index, short value)
        {
            var bucket = BucketFor(PliesOf(value));
            _values[index] = value;
            bucket.Add((uint)index);
        }

        /// <summary>
        /// Everything needed to carry on from the next ply, beside the values
        /// (those are in the table file): move counts, slowest losses, the
        /// three flags, the queued work beyond the cap, and en passant nodes.
        /// </summary>
        public void WriteFrontier(BinaryWriter writer)
        {
            writer.Write(Encoding.ASCII.GetBytes(FrontierMagic));
            writer.Write(_material.ToString());
            writer.Write(_values.LongLength);
            writer.Write(_cap!.Value);
            writer.Write(_ply);
            writer.Write(_remaining);
            writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_longestLoss.AsSpan()));
            _hasDrawingExit.Write(writer);
            _hasUnknownExit.Write(writer);
            _final.Write(writer);
            writer.Write(_buckets.Count);
            for (int ply = _ply; ply < _buckets.Count; ply++)
            {
                var live = _buckets[ply].Where(entry => IsLive(entry, ply)).ToList();
                writer.Write(live.Count);
                foreach (uint entry in live)
                    writer.Write(entry);
            }
            writer.Write(_enPassant.Count);
            foreach (var node in _enPassant)
            {
                writer.Write(node.Child);
                writer.Write(node.Parent);
                writer.Write(node.Key);
                writer.Write(node.From);
                writer.Write(node.To);
                writer.Write((byte)node.Capture.Kind);
                writer.Write(node.Capture.Plies);
                writer.Write(node.ChildHasOtherMoves);
                writer.Write(node.Value);
                writer.Write(node.Final);
            }
        }

        /// <summary>A solver picked up from its frontier file; null if the file is for another table or cap.</summary>
        public static Solver? LoadFrontier(string path, Material material, short[] values, int cap,
                                           Func<Position, Outcome> probeCapture, IProgress<string>? progress)
        {
            using var reader = new BinaryReader(File.OpenRead(path), Encoding.ASCII);
            if (Encoding.ASCII.GetString(reader.ReadBytes(FrontierMagic.Length)) != FrontierMagic
                || reader.ReadString() != material.ToString()
                || reader.ReadInt64() != values.LongLength
                || reader.ReadInt32() != cap)
                return null;
            var solver = new Solver(material, probeCapture, progress, values) { _cap = cap };
            solver._ply = reader.ReadInt32();
            ReadExactly(reader, solver._remaining);
            ReadExactly(reader, System.Runtime.InteropServices.MemoryMarshal.AsBytes(solver._longestLoss.AsSpan()));
            solver._hasDrawingExit.Read(reader);
            solver._hasUnknownExit.Read(reader);
            solver._final.Read(reader);
            int buckets = reader.ReadInt32();
            for (int ply = 0; ply < buckets; ply++)
            {
                var bucket = new List<uint>();
                if (ply >= solver._ply)
                {
                    int count = reader.ReadInt32();
                    bucket.Capacity = count;
                    for (int i = 0; i < count; i++)
                        bucket.Add(reader.ReadUInt32());
                }
                solver._buckets.Add(bucket);
            }
            int nodes = reader.ReadInt32();
            for (int id = 0; id < nodes; id++)
            {
                var node = new EnPassantNode(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(),
                                             reader.ReadByte(), reader.ReadByte(),
                                             new Outcome((OutcomeKind)reader.ReadByte(), reader.ReadInt32()),
                                             reader.ReadBoolean())
                {
                    Value = reader.ReadInt16(),
                    Final = reader.ReadBoolean(),
                };
                solver._enPassant.Add(node);
                solver.Register(id);
            }
            return solver;
        }

        private static void ReadExactly(BinaryReader reader, Span<byte> bytes)
        {
            while (bytes.Length > 0)
            {
                int read = reader.Read(bytes);
                if (read == 0)
                    throw new EndOfStreamException("The frontier file is truncated.");
                bytes = bytes[read..];
            }
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
                if (_zeroing && piece.Type == PieceType.Pawn)
                    continue;   // under the 50-move rule a pawn move is an exit, never an un-move within the count
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
                        long previous = _index.Encode(squares, previousMover);
                        if (previous >= 0 && !results.Contains(previous))   // distinct, like the children
                            results.Add(previous);
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
        /// Set up the scratch position for an index; false for a hole (see
        /// <see cref="TableIndex.Decode"/>), which leaves the scratch position unusable.
        /// </summary>
        private bool Load(long index)
        {
            if (!_index.Decode(index, _squares, out var side))
                return false;
            for (int square = 0; square < 64; square++)
                _scratch.SetPiece(square, Piece.Empty);
            _scratch.SetSideToMove(side);
            for (int slot = 0; slot < _slots.Length; slot++)
                _scratch.SetPiece(_squares[slot], _slots[slot]);
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

    /// <summary>In a capped table: positions not settled within the cap (longer wins and losses, and draws).</summary>
    public long[] Beyond { get; } = new long[2];

    /// <summary>The longest forced mate for each side to move, and an index where it happens.</summary>
    public (Outcome Outcome, long Index)?[] Longest { get; } = new (Outcome, long)?[2];

    public TableStatistics(Material material) => Material = material;

    /// <summary>Count a position <paramref name="weight"/> times (its symmetric images).</summary>
    internal void Add(Colour side, Outcome outcome, long index, int weight = 1)
    {
        int s = (int)side;
        switch (outcome.Kind)
        {
            case OutcomeKind.Win: Wins[s] += weight; break;
            case OutcomeKind.Loss: Losses[s] += weight; break;
            case OutcomeKind.Beyond: Beyond[s] += weight; break;
            default: Draws[s] += weight; break;
        }
        if (outcome.Kind is OutcomeKind.Win or OutcomeKind.Loss
            && (Longest[s] is null || outcome.Plies > Longest[s]!.Value.Outcome.Plies))
            Longest[s] = (outcome, index);
    }

    public long Legal(Colour side) => Wins[(int)side] + Losses[(int)side] + Draws[(int)side] + Beyond[(int)side];
}
