using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Game;
using ChessBruteforcer.Core.Records;

namespace ChessBruteforcer.Core.Match;

/// <summary>
/// Where a real game enters our endgame tables, the position there, and the
/// game's positions up to it (for repetitions).  <see cref="PliesLeft"/> is
/// how much of the game was still to be played.
/// </summary>
public sealed record TakeoverPoint(StoredGame Game, int Ply, Position Position, Material Material, IReadOnlyList<ulong> Hashes)
{
    public int PliesLeft => Game.Moves.Count - Ply;
}

/// <summary>
/// Taking over real games once our tables cover them (Matthew's early test,
/// 2026-10-01): find the first position of a game in a table we have, and
/// play it out from there with the tables on both sides.
/// </summary>
public static class Takeover
{
    /// <summary>
    /// The first position of <paramref name="game"/> in a table we have: at
    /// most 5 pieces, no castling rights (tables assume none), a table on hand
    /// for its material (<paramref name="covered"/>, given the canonical
    /// material), and a move to make (not mate or stalemate).  Null if none.
    /// Every later position is then covered too: a table's captures and
    /// promotions lead into tables built before it.
    /// </summary>
    public static TakeoverPoint? Find(StoredGame game, Func<Material, bool> covered)
    {
        var position = game.StartPosition();
        var hashes = new List<ulong> { position.Hash };
        int pieces = 0;
        for (int square = 0; square < 64; square++)
            if (!position[square].IsEmpty)
                pieces++;
        Material? material = null;
        for (int ply = 0; ; ply++)
        {
            if (pieces <= Tablebase.MaxPieces)
            {
                material ??= Material.FromPosition(position).Canonical;   // (again after a capture or promotion)
                if (position.Castling == CastlingRights.None && covered(material) && position.Status() == GameStatus.Ongoing)
                    return new TakeoverPoint(game, ply, position, material, hashes);
            }
            if (ply == game.Moves.Count)
                return null;
            var move = game.Moves[ply];
            if (move.IsCapture)
                pieces--;
            if (move.IsCapture || move.IsPromotion)
                material = null;
            position.MakeMove(move);
            hashes.Add(position.Hash);
        }
    }

    /// <summary>
    /// Play a position out with the engine on both sides, from its own clock
    /// and history: every move by the tables, under the 50-move rule (see
    /// <see cref="Search"/>), so the side ahead converts as fast as the rule
    /// needs and the side behind defends as long as the tables allow.  Ends by
    /// the rules of a match (mate, stalemate, the 50-move rule, threefold
    /// repetition, insufficient material), or "left the tables" if a position
    /// isn't covered (it shouldn't happen).  Returns the result, how it ended,
    /// and the plies played.
    /// </summary>
    public static (GameResult Result, string Termination, int Plies) PlayOut(Position start, IReadOnlyList<ulong> hashes,
                                                                            Tablebase tables, int maxPlies = GamePlayer.MaxPlies)
    {
        var position = Position.FromFen(start.ToFen());
        var history = hashes.ToList();   // the last is the start position itself
        var seen = new Dictionary<ulong, int>();
        foreach (ulong hash in history)
            seen[hash] = seen.GetValueOrDefault(hash) + 1;
        var search = new Search(new TranspositionTable(1), tables);
        for (int plies = 0; ; plies++)
        {
            if (GamePlayer.Ended(position, seen) is var (result, termination))
                return (result, termination, plies);
            if (plies >= maxPlies)
                return (GameResult.Draw, "move limit", plies);
            if (!tables.TryProbe(position, out _))
                return (GameResult.Draw, "left the tables", plies);
            var move = search.Run(position, new SearchLimits { Depth = 1 }, history.Take(history.Count - 1).ToList()).BestMove!.Value;
            position.MakeMove(move);
            history.Add(position.Hash);
            seen[position.Hash] = seen.GetValueOrDefault(position.Hash) + 1;
        }
    }
}
