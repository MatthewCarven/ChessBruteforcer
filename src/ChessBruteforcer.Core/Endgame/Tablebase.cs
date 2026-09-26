using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Endgame;

/// <summary>
/// A set of solved endgame tables.  Asking about a position finds (or
/// solves, or loads from <see cref="Directory"/>) the table for its
/// material, including every smaller table its captures lead to.
/// </summary>
public sealed class Tablebase
{
    private readonly Dictionary<Material, EndgameTable> _tables = new();
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

    /// <summary>The table for a material set, in either colour orientation.</summary>
    public EndgameTable Get(Material material)
    {
        material = material.Canonical;
        if (_tables.TryGetValue(material, out var table))
            return table;

        string? path = Directory is null ? null : Path.Combine(Directory, material + ".cbt");
        if (path is not null && File.Exists(path))
        {
            table = EndgameTable.Load(path);
        }
        else
        {
            table = EndgameTable.Solve(material, Probe, _progress);
            if (path is not null)
                table.Save(path);
        }
        _tables[material] = table;
        return table;
    }

    /// <summary>The outcome for the side to move.  The position is not changed.</summary>
    public Outcome Probe(Position position)
    {
        if (position.Castling != CastlingRights.None)
            throw new ArgumentException("Endgame tables assume no castling rights.");
        var material = Material.FromPosition(position);
        var table = Get(material);
        var lookup = material.IsCanonical ? position : SwapColours(position);
        return table.Probe(lookup)
               ?? throw new ArgumentException("That position is impossible (the side not to move is in check).");
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

    /// <summary>The same position with white and black exchanged (and the side to move with them).</summary>
    public static Position SwapColours(Position position)
    {
        var swapped = Position.Empty(Position.Opponent(position.SideToMove));
        for (int square = 0; square < 64; square++)
        {
            var piece = position[square];
            if (!piece.IsEmpty)
                swapped.SetPiece(square, piece with { Colour = Position.Opponent(piece.Colour) });
        }
        return swapped;
    }
}
