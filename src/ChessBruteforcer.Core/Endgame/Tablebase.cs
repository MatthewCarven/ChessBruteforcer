using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Endgame;

/// <summary>
/// A set of solved endgame tables.  Asking about a position finds (or
/// solves, or loads from <see cref="Directory"/>) the table for its
/// material, including every smaller table its captures lead to.
/// </summary>
public sealed class Tablebase : IDisposable
{
    private readonly Dictionary<Material, EndgameTable> _tables = new();
    private readonly Dictionary<Material, EndgameTable> _dtzTables = new();
    private readonly IProgress<string>? _progress;

    /// <summary>Where tables are loaded from and saved to, or null to keep them in memory only.</summary>
    public string? Directory { get; }

    public Tablebase(string? directory = null, IProgress<string>? progress = null)
    {
        Directory = directory;
        _progress = progress;
        if (directory is not null)
            System.IO.Directory.CreateDirectory(directory);
    }

    public IReadOnlyCollection<EndgameTable> Tables => _tables.Values;

    /// <summary>Release every table's file mapping (on Windows a mapped table file can't be moved or deleted until then).</summary>
    public void Dispose()
    {
        foreach (var table in _tables.Values.Concat(_dtzTables.Values))
            table.Dispose();
        _tables.Clear();
        _dtzTables.Clear();
    }

    /// <summary>
    /// When false, a table that is neither loaded nor on disk is reported as
    /// missing instead of being solved.  A playing engine wants this: it must
    /// never stop mid-game to spend minutes solving.
    /// </summary>
    public bool SolveMissing { get; init; } = true;

    /// <summary>
    /// When false, only tables already loaded (see <see cref="Preload"/>) are
    /// used, and the disk is never touched on a probe.  A search probing
    /// thousands of positions a second can't afford a disk read mid-move.
    /// </summary>
    public bool LoadOnDemand { get; init; } = true;

    /// <summary>Largest piece count any table covers.</summary>
    public const int MaxPieces = 5;

    private readonly HashSet<Material> _missing = new();

    /// <summary>
    /// How deep, in plies, tables are solved when <see cref="SolveMissing"/>
    /// is on: null (the default) means to the end.  A table already solved
    /// to less is extended when it is asked for, and so are the tables its
    /// captures lead to, first.  A table solved deeper is used as it is.
    /// </summary>
    public int? Cap { get; set; }

    /// <summary>The table for a material set, in either colour orientation.</summary>
    public EndgameTable Get(Material material)
    {
        material = material.Canonical;
        string? path = Directory is null ? null : Path.Combine(Directory, material + ".cbt");
        if (!_tables.TryGetValue(material, out var table))
        {
            if (!LoadOnDemand)
            {
                _missing.Add(material);
                throw new TableMissingException(material);
            }

            if (path is not null && File.Exists(path))
            {
                table = EndgameTable.Load(path);
            }
            else if (!SolveMissing)
            {
                _missing.Add(material);
                throw new TableMissingException(material);
            }
            else
            {
                table = EndgameTable.Solve(material, Probe, _progress, Cap, path);
                Store(table, path);
            }
            _tables[material] = table;
        }

        if (SolveMissing && table.Cap is int cap && (Cap is null || cap < Cap))
        {
            table = Deepen(table, path);
            _tables[material] = table;
        }
        return table;
    }

    /// <summary>
    /// The DTZ table for a material set (results under the 50-move rule,
    /// see <see cref="EndgameTable.IsDtz"/>): loaded from <c>.cbz</c> beside the
    /// others, or solved, which first needs the smaller tables' DTZ.
    /// </summary>
    public EndgameTable GetDtz(Material material)
    {
        material = material.Canonical;
        if (_dtzTables.TryGetValue(material, out var table))
            return table;
        string? path = Directory is null ? null : Path.Combine(Directory, material + ".cbz");
        if (path is not null && File.Exists(path))
        {
            table = EndgameTable.Load(path);
        }
        else if (!SolveMissing || !LoadOnDemand)
        {
            throw new TableMissingException(material);
        }
        else
        {
            table = EndgameTable.SolveDtz(material, ProbeDtz, _progress, path);
            if (path is not null)
                table.Save(path);
        }
        _dtzTables[material] = table;
        return table;
    }

    /// <summary>Carry a capped table on to <see cref="Cap"/>: from memory, from its frontier file, or failing both, from scratch.</summary>
    private EndgameTable Deepen(EndgameTable table, string? path)
    {
        if (table.HasFrontier)
        {
            table = EndgameTable.Extend(table, Probe, Cap, _progress);
        }
        else
        {
            table.Dispose();   // a mapped file stays locked on Windows until disposed
            table = (path is null ? null : EndgameTable.ExtendFromFiles(path, FrontierPath(path), Probe, Cap, _progress))
                    ?? EndgameTable.Solve(table.Material, Probe, _progress, Cap, path);
        }
        Store(table, path);
        return table;
    }

    /// <summary>
    /// Save a table just solved or extended.  A capped one saves its
    /// frontier first, then lets go of it (the table file's cap says which
    /// frontier belongs to it); a complete one removes any old frontier.
    /// </summary>
    private static void Store(EndgameTable table, string? path)
    {
        if (path is null)
            return;
        string frontier = FrontierPath(path);
        if (table.HasFrontier)
        {
            table.SaveFrontier(frontier);
            table.Save(path);
            table.DropFrontier();
        }
        else
        {
            table.Save(path);
            File.Delete(frontier);
        }
    }

    private static string FrontierPath(string tablePath) => Path.ChangeExtension(tablePath, ".cbf");

    /// <summary>
    /// The outcome for the side to move.  The position is not changed.
    ///
    /// Tables store positions without en passant rights.  If this position
    /// has an en passant capture available, the side to move gets the better
    /// of the table's value and that capture, or the capture alone when it is
    /// the only legal move.
    /// </summary>
    public Outcome Probe(Position position) => Probe(position, dtz: false);

    /// <summary>
    /// The result under the 50-move rule, for the side to move with the count
    /// at 0, and the plies to the next capture or pawn move (or mate).
    /// </summary>
    public Outcome ProbeDtz(Position position) => Probe(position, dtz: true);

    private Outcome Probe(Position position, bool dtz)
    {
        if (position.Castling != CastlingRights.None)
            throw new ArgumentException("Endgame tables assume no castling rights.");
        var material = Material.FromPosition(position);
        var table = dtz ? GetDtz(material) : Get(material);
        var lookup = material.IsCanonical ? position : SwapColours(position);
        var stored = table.Probe(lookup)
                     ?? throw new ArgumentException("That position is impossible (the side not to move is in check).");
        if (position.EnPassantSquare == Position.NoSquare)
            return stored;

        var moves = MoveGenerator.Legal(position);
        var captures = moves.Where(m => (m.Flags & MoveFlags.EnPassant) != 0).ToList();
        if (captures.Count == 0)
            return stored;
        var best = captures
            .Select(capture =>
            {
                var undo = position.MakeMove(capture);
                var reached = Probe(position, dtz);
                position.UnmakeMove(capture, undo);
                // Under the rule a capture starts the count again: only its result carries over.
                return (dtz ? new Outcome(reached.Kind, 0) : reached).ForPreviousMover();
            })
            .Aggregate(Outcome.Better);
        if (moves.Count == captures.Count)
            return best;
        return Outcome.Better(stored, best);
    }

    /// <summary>Memory <see cref="Preload"/> reads tables into by default: every table up to 4 pieces is 427 MB.</summary>
    public const long PreloadBytes = 1L << 30;

    /// <summary>
    /// Open every table in <see cref="Directory"/> (mate distances, <c>.cbt</c>).
    /// Plain files are read into memory, smallest first, while they fit in
    /// <paramref name="memoryBytes"/>.  Compressed files, and plain ones past
    /// that, stay on disk and are read as they are probed (a compressed one
    /// a 64 KB block at a time, cached): with the 5-piece tables there, all
    /// of them in memory would be tens of GB.  Returns how many tables were
    /// opened, the bytes read into memory, and how many stay on disk.
    /// </summary>
    public (int Tables, long Bytes, int OnDisk) Preload(long memoryBytes = PreloadBytes)
    {
        if (Directory is null)
            return (0, 0, 0);
        int count = 0, onDisk = 0;
        long bytes = 0;
        var files = System.IO.Directory.EnumerateFiles(Directory, "*.cbt")
            .Select(path => (Path: path, Length: new FileInfo(path).Length))
            .OrderBy(file => file.Length).ThenBy(file => file.Path, StringComparer.Ordinal);
        foreach (var (path, length) in files)
        {
            bool intoMemory = bytes + length <= memoryBytes && !EndgameTable.IsCompressedFile(path);
            var table = EndgameTable.Load(path, intoMemory);
            if (_tables.Remove(table.Material, out var old))
                old.Dispose();
            _tables[table.Material] = table;
            _missing.Remove(table.Material);
            count++;
            if (intoMemory)
                bytes += table.Size * sizeof(short);
            else
                onDisk++;
        }
        return (count, bytes, onDisk);
    }

    /// <summary>
    /// Probe without ever solving: false if the position has too many pieces,
    /// castling rights, no table on hand, or only a capped table that hasn't
    /// settled it (that is not a draw).  This is what the search calls.
    /// </summary>
    public bool TryProbe(Position position, out Outcome outcome)
    {
        outcome = Outcome.Draw;
        if (position.Castling != CastlingRights.None)
            return false;
        int pieces = 0;
        for (int square = 0; square < 64 && pieces <= MaxPieces; square++)
            if (!position[square].IsEmpty) pieces++;
        if (pieces > MaxPieces)
            return false;
        var material = Material.FromPosition(position).Canonical;
        if (_missing.Contains(material))
            return false;
        try
        {
            outcome = Probe(position);
            if (outcome.Kind == OutcomeKind.Beyond)
            {
                outcome = Outcome.Draw;
                return false;
            }
            return true;
        }
        catch (TableMissingException)
        {
            return false;
        }
    }

    /// <summary>
    /// Check a solved table against the definition of solved: at every
    /// legal position, the stored value must equal the best outcome over its
    /// moves (or mate / stalemate when there are none).  <paramref name="stride"/>
    /// checks every n-th index for a quicker sample.  Returns how many
    /// positions were checked and the first few that disagree.
    /// </summary>
    public (long Checked, List<string> Mismatches) Verify(Material material, int stride = 1,
                                                          IProgress<string>? progress = null)
    {
        var table = Get(material);
        var mismatches = new List<string>();
        long checkedPositions = 0;
        for (long index = 0; index < table.Size; index += stride)
        {
            var stored = table[index];
            if (stored is null)
                continue;
            var position = table.PositionAt(index);
            var ranked = RankMoves(position);
            var expected = ranked.Count > 0
                ? ranked.Select(r => r.Outcome).Aggregate(Outcome.Better)
                : position.InCheck() ? Outcome.Loss(0) : Outcome.Draw;
            // A capped table settles what lies within its cap; past it, a draw
            // (not yet proven) and a longer win or loss both read as beyond.
            if (table.Cap is int cap && (expected.Kind == OutcomeKind.Beyond || expected.Plies > cap
                                         || (expected.Kind == OutcomeKind.Draw && stored.Value.Kind == OutcomeKind.Beyond)))
                expected = Outcome.Beyond(cap);
            if (expected != stored.Value && mismatches.Count < 20)
                mismatches.Add($"{position.ToFen()}: stored {stored}, moves give {expected}");
            checkedPositions++;
            if (checkedPositions % 1_000_000 == 0)
                progress?.Report($"{table.Material}: verified {checkedPositions:N0}");
        }
        return (checkedPositions, mismatches);
    }

    /// <summary>
    /// Every legal move with the outcome it leads to for the mover, best
    /// first: quickest wins, then draws, then the slowest losses.
    /// </summary>
    public List<(Move Move, Outcome Outcome)> RankMoves(Position position)
    {
        var ranked = new List<(Move, Outcome)>();
        foreach (var move in MoveGenerator.Legal(position))
        {
            var undo = position.MakeMove(move);
            ranked.Add((move, Probe(position).ForPreviousMover()));
            position.UnmakeMove(move, undo);
        }
        return ranked
            .OrderByDescending(r => r.Item2.Score)
            .ThenBy(r => r.Item1.ToUci())
            .ToList();
    }

    /// <summary>
    /// Every legal move with its result under the 50-move rule, best first.
    /// A capture or pawn move wins, draws or loses one ply away (the count
    /// starts again); any other move is one ply further from the next one,
    /// and a result more than 100 plies away is a draw.
    /// </summary>
    public List<(Move Move, Outcome Outcome)> RankMovesUnderRule(Position position)
    {
        var ranked = new List<(Move, Outcome)>();
        foreach (var move in MoveGenerator.Legal(position))
        {
            bool zeroing = move.IsCapture || move.IsPromotion || position[move.From].Type == PieceType.Pawn;
            var undo = position.MakeMove(move);
            var reached = ProbeDtz(position);
            position.UnmakeMove(move, undo);
            var outcome = (zeroing ? new Outcome(reached.Kind, 0) : reached).ForPreviousMover();
            if (outcome.Kind != OutcomeKind.Draw && outcome.Plies > EndgameTable.RulePlies)
                outcome = Outcome.Draw;
            ranked.Add((move, outcome));
        }
        return ranked
            .OrderByDescending(r => r.Item2.Score)
            .ThenBy(r => r.Item1.ToUci())
            .ToList();
    }

    /// <summary>Check a DTZ table the way <see cref="Verify"/> checks a DTM one: each value must be the best over its moves under the rule.</summary>
    public (long Checked, List<string> Mismatches) VerifyDtz(Material material, int stride = 1,
                                                             IProgress<string>? progress = null)
    {
        var table = GetDtz(material);
        var mismatches = new List<string>();
        long checkedPositions = 0;
        for (long index = 0; index < table.Size; index += stride)
        {
            var stored = table[index];
            if (stored is null)
                continue;
            var position = table.PositionAt(index);
            var ranked = RankMovesUnderRule(position);
            var expected = ranked.Count > 0
                ? ranked[0].Outcome
                : position.InCheck() ? Outcome.Loss(0) : Outcome.Draw;
            if (expected != stored.Value && mismatches.Count < 20)
                mismatches.Add($"{position.ToFen()}: stored {stored}, moves give {expected}");
            checkedPositions++;
            if (checkedPositions % 1_000_000 == 0)
                progress?.Report($"{table.Material}: verified {checkedPositions:N0}");
        }
        return (checkedPositions, mismatches);
    }

    /// <summary>Best play from here until mate (or <paramref name="maxPlies"/> for a draw).</summary>
    public List<Move> PrincipalLine(Position position, int maxPlies = 200)
    {
        var line = new List<Move>();
        var copy = Position.FromFen(position.ToFen());
        while (line.Count < maxPlies)
        {
            var ranked = RankMoves(copy);
            if (ranked.Count == 0)
                break;
            var best = ranked[0].Move;
            line.Add(best);
            copy.MakeMove(best);
        }
        return line;
    }

    /// <summary>
    /// The same position with white and black exchanged, the board flipped
    /// top to bottom so pawns still run the right way, and the other side to
    /// move.  Every result is unchanged by this, seen from the side to move.
    /// </summary>
    public static Position SwapColours(Position position)
    {
        var swapped = Position.Empty(Position.Opponent(position.SideToMove));
        for (int square = 0; square < 64; square++)
        {
            var piece = position[square];
            if (!piece.IsEmpty)
                swapped.SetPiece(square ^ 56, piece with { Colour = Position.Opponent(piece.Colour) });
        }
        return swapped;
    }
}

/// <summary>A table was needed but is not on disk, and <see cref="Tablebase.SolveMissing"/> is off.</summary>
public sealed class TableMissingException : Exception
{
    public TableMissingException(Material material)
        : base($"No {material} table available.") => Material = material;

    public Material Material { get; }
}
