using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Records;

/// <summary>
/// Which endgame tables real games reach: every material of at most
/// <see cref="MaxPieces"/> pieces (kings included) a game passes through,
/// in either colour (tables are kept with the stronger side as white).
/// Per material: how many games reach it, how many reach it first (their
/// first position that small), and the plies they spend in it.
/// </summary>
public sealed class EndingStats
{
    public const int MaxPieces = Tablebase.MaxPieces;

    private readonly Dictionary<Material, int> _ids = new();
    private readonly List<Material> _materials = new();
    private readonly List<MaterialCounts> _counts = new();
    private readonly List<int[]> _gameMaterials = new();

    public long Games { get; private set; }

    /// <summary>Games that got down to <see cref="MaxPieces"/> pieces or fewer.</summary>
    public long Reaching { get; private set; }

    /// <summary>Of those, games that stood in such a position with castling rights still held (no table covers those).</summary>
    public long WithCastling { get; private set; }

    /// <summary>Every material seen, in the order first met.</summary>
    public IReadOnlyList<Material> Materials => _materials;

    /// <summary>Per material (same order as <see cref="Materials"/>).</summary>
    public IReadOnlyList<MaterialCounts> Counts => _counts;

    /// <summary>Per game that reached <see cref="MaxPieces"/> pieces or fewer: the materials it passed through, as indexes into <see cref="Materials"/>, in order.</summary>
    public IReadOnlyList<int[]> GameMaterials => _gameMaterials;

    public void Add(StoredGame game)
    {
        Games++;
        var position = game.StartPosition();
        int pieces = 0;
        for (int square = 0; square < 64; square++)
            if (!position[square].IsEmpty)
                pieces++;
        var passed = new List<int>();
        int current = -1;
        bool castling = false;
        if (pieces <= MaxPieces)
        {
            current = Enter(position, passed);
            castling = position.Castling != CastlingRights.None;
        }
        foreach (var move in game.Moves)
        {
            if (move.IsCapture)
                pieces--;
            position.MakeMove(move);
            if (pieces > MaxPieces)
                continue;
            if (current < 0 || move.IsCapture || move.IsPromotion)
                current = Enter(position, passed);
            _counts[current].Plies++;
            castling |= position.Castling != CastlingRights.None;
        }
        if (passed.Count == 0)
            return;
        Reaching++;
        if (castling)
            WithCastling++;
        _counts[passed[0]].FirstEntries++;
        _gameMaterials.Add(passed.ToArray());
    }

    /// <summary>The position's material as an index, counting the game in it once.</summary>
    private int Enter(Position position, List<int> passed)
    {
        var material = Material.FromPosition(position).Canonical;
        if (!_ids.TryGetValue(material, out int id))
        {
            id = _materials.Count;
            _ids[material] = id;
            _materials.Add(material);
            _counts.Add(new MaterialCounts());
        }
        if (!passed.Contains(id))
        {
            passed.Add(id);
            _counts[id].Games++;
        }
        return id;
    }

    /// <summary>
    /// The tables a promotion leads to: each pawn of either side becoming a
    /// queen, rook, bishop or knight (a table with pawns needs these first).
    /// </summary>
    public static IEnumerable<Material> PromotionTargets(Material material)
    {
        var targets = new HashSet<Material>();
        foreach (var (mine, theirs, white) in new[] { (material.White, material.Black, true), (material.Black, material.White, false) })
        {
            if (!mine.Contains(PieceType.Pawn))
                continue;
            foreach (var piece in new[] { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight })
            {
                var promoted = mine.ToList();
                promoted.Remove(PieceType.Pawn);
                promoted.Add(piece);
                targets.Add((white ? new Material(promoted, theirs) : new Material(theirs, promoted)).Canonical);
            }
        }
        return targets;
    }

    /// <summary>
    /// An order to build the <paramref name="missing"/> tables in, greedily:
    /// each round takes the table that, with the missing tables its promotions
    /// need (built first, fewer pawns first), brings the most games wholly into
    /// the tables per position solved.  <paramref name="gameNeeds"/> holds, per
    /// game, the missing tables it passes through.
    /// </summary>
    public static List<Material> BuildOrder(IReadOnlyList<Material> missing,
                                            IEnumerable<IReadOnlyCollection<Material>> gameNeeds)
    {
        int n = missing.Count;
        var index = missing.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i);
        var size = missing.Select(EndgameTable.TableSize).ToArray();
        var pawns = missing.Select(m => m.White.Concat(m.Black).Count(t => t == PieceType.Pawn)).ToArray();
        var deps = missing.Select(m => PromotionTargets(m).Where(index.ContainsKey).Select(t => index[t]).ToArray()).ToArray();
        var open = gameNeeds.Select(need => need.Select(m => index[m]).ToArray()).Where(need => need.Length > 0).ToList();
        var games = new long[n];
        foreach (var need in open)
            foreach (int k in need)
                games[k]++;

        var built = new bool[n];
        var order = new List<int>();
        while (order.Count < n)
        {
            List<int>? best = null;
            double bestScore = -1;
            int bestTable = -1;
            for (int t = 0; t < n; t++)
            {
                if (built[t])
                    continue;
                var closure = new HashSet<int>();
                var stack = new Stack<int>([t]);
                while (stack.Count > 0)
                {
                    int k = stack.Pop();
                    if (built[k] || !closure.Add(k))
                        continue;
                    foreach (int d in deps[k])
                        stack.Push(d);
                }
                long cost = closure.Sum(k => size[k]);
                long gain = open.Count(need => need.All(k => built[k] || closure.Contains(k)));
                double score = (double)gain / cost;
                if (score > bestScore || (score == bestScore && games[t] > games[bestTable]))
                {
                    bestScore = score;
                    bestTable = t;
                    best = closure.OrderBy(k => pawns[k]).ThenByDescending(k => games[k]).ThenBy(k => k).ToList();
                }
            }
            foreach (int k in best!)
            {
                built[k] = true;
                order.Add(k);
            }
            open.RemoveAll(need => need.All(k => built[k]));
        }
        return order.Select(k => missing[k]).ToList();
    }
}

/// <summary>Games through one material (see <see cref="EndingStats"/>).</summary>
public sealed class MaterialCounts
{
    /// <summary>Games that stood in this material at least once.</summary>
    public long Games { get; internal set; }

    /// <summary>Games whose first position of <see cref="EndingStats.MaxPieces"/> pieces or fewer was this material.</summary>
    public long FirstEntries { get; internal set; }

    /// <summary>Plies that ended in this material, over all games.</summary>
    public long Plies { get; internal set; }
}
