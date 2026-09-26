namespace ChessBruteforcer.Core.Game;

/// <summary>
/// Perft ("performance test"): count every legal move sequence to a fixed
/// depth.  The counts for standard positions are published, so matching them
/// is the usual proof that a move generator gets every rule right.
/// </summary>
public static class Perft
{
    public static long Count(Position position, int depth)
    {
        if (depth <= 0)
            return 1;
        var buffers = Enumerable.Range(0, depth).Select(_ => new List<Move>(64)).ToArray();
        return Count(position, depth, buffers);
    }

    /// <summary>Per-move node counts at the root, for finding which move a bug hides under.</summary>
    public static List<(Move Move, long Nodes)> Divide(Position position, int depth)
    {
        var result = new List<(Move, long)>();
        foreach (var move in MoveGenerator.Legal(position))
        {
            var undo = position.MakeMove(move);
            result.Add((move, Count(position, depth - 1)));
            position.UnmakeMove(move, undo);
        }
        return result;
    }

    private static long Count(Position position, int depth, List<Move>[] buffers)
    {
        var moves = buffers[depth - 1];
        MoveGenerator.Legal(position, moves);
        if (depth == 1)
            return moves.Count;

        long nodes = 0;
        // Each depth has its own buffer, so recursing never touches `moves`.
        foreach (var move in moves)
        {
            var undo = position.MakeMove(move);
            nodes += Count(position, depth - 1, buffers);
            position.UnmakeMove(move, undo);
        }
        return nodes;
    }
}
