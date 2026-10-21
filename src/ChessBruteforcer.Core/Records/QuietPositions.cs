using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Records;

/// <summary>
/// The quiet positions of a game, as Texel tuning chose them (2026-10-14) and the network's data does too
/// (`tune positions` and `nnue data` both walk games through here, so the two can't drift): after move 8
/// (ply 16 on), more than 5 pieces on the board (the tables have the rest), the side to move not in check,
/// and neither the move before nor the move played a capture or a promotion.
/// </summary>
public static class QuietPositions
{
    /// <summary>The first ply a position may be taken at: the position after move 8.</summary>
    public const int FirstPly = 16;

    /// <summary>A position needs more pieces than this.</summary>
    public const int MorePiecesThan = 5;

    /// <summary>The rule in words, for reports and data files.</summary>
    public const string Rule = "after move 8 (ply 16 on), more than 5 pieces, side to move not in check, " +
                               "neither the move before nor the move played a capture or a promotion";

    /// <summary>
    /// Play the game through from its start, calling <paramref name="visit"/> with (ply, position) for each
    /// quiet position: the position before <c>game.Moves[ply]</c>. The position is the walk's own and moves on
    /// after the call returns, so take what's needed from it inside.
    /// </summary>
    public static void Walk(StoredGame game, Action<int, Position> visit)
    {
        var position = game.StartPosition();
        int pieces = 0;
        for (int square = 0; square < 64; square++)
            if (!position[square].IsEmpty)
                pieces++;
        for (int ply = 0; ply < game.Moves.Count; ply++)
        {
            var move = game.Moves[ply];
            bool before = ply > 0 && (game.Moves[ply - 1].IsCapture || game.Moves[ply - 1].IsPromotion);
            if (ply >= FirstPly && pieces > MorePiecesThan && !before && !move.IsCapture && !move.IsPromotion && !position.InCheck())
                visit(ply, position);
            if (move.IsCapture)
                pieces--;
            position.MakeMove(move);
        }
    }
}
