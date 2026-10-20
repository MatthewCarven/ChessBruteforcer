using System.Numerics;
using System.Runtime.CompilerServices;

namespace ChessBruteforcer.Core.Game;

/// <summary>
/// Generates moves.  Pseudo-legal moves follow how the pieces move; legal
/// moves are the pseudo-legal ones that don't leave the mover's own king
/// in check, found by making each move and looking.  They come from the
/// piece sets by table (<see cref="Bitboards"/>), or with
/// <see cref="AttackPath.Rays"/> by walking the board as before the sets
/// (the tests' reference), in the same order either way.
/// </summary>
public static class MoveGenerator
{
    private static readonly PieceType[] PromotionPieces =
        { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight };

    // Attacks' rays as sets, [square * 8 + direction], and whether a direction runs up the board (its nearest square
    // the lowest bit of a set) or down (the highest): a slider's moves come ray by ray, nearest first, as the walk gives them.
    private static readonly ulong[] RaySets = BuildRaySets();
    private static readonly bool[] RayRunsUp =
        Enumerable.Range(0, Attacks.DirectionCount).Select(d => Attacks.Rays[27][d][0] > 27).ToArray();   // from d4

    private static ulong[] BuildRaySets()
    {
        var sets = new ulong[64 * Attacks.DirectionCount];
        for (int square = 0; square < 64; square++)
            for (int d = 0; d < Attacks.DirectionCount; d++)
                foreach (int to in Attacks.Rays[square][d])
                    sets[square * Attacks.DirectionCount + d] |= 1UL << to;
        return sets;
    }

    public static List<Move> Legal(Position position)
    {
        var moves = new List<Move>(64);
        Legal(position, moves);
        return moves;
    }

    /// <summary>Fill <paramref name="moves"/> (cleared first) with the legal moves.</summary>
    public static void Legal(Position position, List<Move> moves)
    {
        Pseudo(position, moves);
        Colour mover = position.SideToMove;
        int kept = 0;
        for (int i = 0; i < moves.Count; i++)
        {
            if (IsLegal(position, moves[i], mover))
                moves[kept++] = moves[i];
        }
        moves.RemoveRange(kept, moves.Count - kept);
    }

    public static bool HasLegalMove(Position position)
    {
        var moves = new List<Move>(64);
        Pseudo(position, moves);
        Colour mover = position.SideToMove;
        foreach (var move in moves)
        {
            if (IsLegal(position, move, mover))
                return true;
        }
        return false;
    }

    /// <summary>Fill <paramref name="moves"/> (cleared first) with the pseudo-legal moves.</summary>
    public static void Pseudo(Position position, List<Move> moves) => Generate(Bitboards.Path, position, moves, quiet: true);

    /// <summary>
    /// Fill <paramref name="moves"/> (cleared first) with what quiescence searches when not in check: the
    /// pseudo-legal captures (with every promotion piece), en passant, and pushes that promote to a queen,
    /// in the order <see cref="Pseudo(Position, List{Move})"/> gives them. Quiescence used to generate every
    /// move and skip the rest, which cost it 23% of a node (NNUE.md, "The rest of a node").
    /// </summary>
    public static void Captures(Position position, List<Move> moves) => Generate(Bitboards.Path, position, moves, quiet: false);

    /// <summary><see cref="Pseudo(Position, List{Move})"/> by the given path (the tests try each).</summary>
    internal static void Pseudo(AttackPath path, Position position, List<Move> moves) => Generate(path, position, moves, quiet: true);

    /// <summary><see cref="Captures(Position, List{Move})"/> by the given path.</summary>
    internal static void Captures(AttackPath path, Position position, List<Move> moves) => Generate(path, position, moves, quiet: false);

    /// <summary>
    /// The pseudo-legal moves, or with <paramref name="quiet"/> false only <see cref="Captures(Position, List{Move})"/>',
    /// in one order whatever the path: from the a1 square up, and each piece's as the walk gives them (a pawn's pushes,
    /// then its captures towards the a-file and the h-file; a knight's and a king's in Attacks' order; a slider's ray by
    /// ray, nearest first; castling after the king's steps). The order is part of the search: PickNext breaks ties by
    /// place in the list and LMR reduces by it, so the sets' own order (by piece kind) searched a tree 9% smaller at
    /// bench 9 (NNUE.md "Bitboards, measured"): a change for a match to judge, not this.
    /// </summary>
    private static void Generate(AttackPath path, Position position, List<Move> moves, bool quiet)
    {
        if (path == AttackPath.Rays)
        {
            GenerateByRays(position, moves, quiet);
            return;
        }
        moves.Clear();
        Colour us = position.SideToMove;
        ulong own = position.Pieces(us), enemy = position.Pieces(Position.Opponent(us));
        ulong occupied = own | enemy;
        ulong targets = quiet ? ~own : enemy;
        for (ulong pieces = own; pieces != 0; pieces &= pieces - 1)
        {
            int from = BitOperations.TrailingZeroCount(pieces);
            switch (position[from].Type)
            {
                case PieceType.Pawn:
                    PawnMoves(position, from, us, quiet, occupied, enemy, moves);
                    break;
                case PieceType.Knight:
                    StepMoves(from, Attacks.Knight[from], Bitboards.Knight(from) & targets, enemy, moves);
                    break;
                case PieceType.King:
                    StepMoves(from, Attacks.King[from], Bitboards.King(from) & targets, enemy, moves);
                    if (quiet)
                        CastlingMoves(position, from, us, moves);
                    break;
                case PieceType.Bishop:
                    RayMoves(from, Bitboards.Bishop(path, from, occupied) & targets, enemy,
                             Attacks.FirstBishopDirection, Attacks.DirectionCount, moves);
                    break;
                case PieceType.Rook:
                    RayMoves(from, Bitboards.Rook(path, from, occupied) & targets, enemy,
                             Attacks.FirstRookDirection, Attacks.FirstBishopDirection, moves);
                    break;
                case PieceType.Queen:
                    RayMoves(from, (Bitboards.Rook(path, from, occupied) | Bitboards.Bishop(path, from, occupied)) & targets,
                             enemy, 0, Attacks.DirectionCount, moves);
                    break;
            }
        }
    }

    /// <summary>A pawn's moves from the sets, in <see cref="PawnMovesByRays"/>' order: its pushes, then its captures.</summary>
    private static void PawnMoves(Position position, int from, Colour us, bool quiet, ulong occupied, ulong enemy,
                                  List<Move> moves)
    {
        int forward = us == Colour.White ? 8 : -8;
        int lastRank = us == Colour.White ? 7 : 0;
        int one = from + forward;
        if (one is >= 0 and < 64 && (occupied >> one & 1) == 0)
        {
            if (quiet)
            {
                AddPawnMove(from, one, MoveFlags.None, lastRank, moves);
                int two = one + forward;
                if (from / 8 == (us == Colour.White ? 1 : 6) && (occupied >> two & 1) == 0)
                    moves.Add(new Move((byte)from, (byte)two, PieceType.None, MoveFlags.DoublePawnPush));
            }
            else if (one / 8 == lastRank)
            {
                moves.Add(new Move((byte)from, (byte)one, PieceType.Queen));
            }
        }

        // Its captures, en passant's square among them, lowest square first: towards the a-file first, for either colour.
        int enPassant = position.EnPassantSquare;
        ulong passing = enPassant >= 0 ? 1UL << enPassant : 0;
        for (ulong reach = Bitboards.Pawn(us, from) & (enemy | passing); reach != 0; reach &= reach - 1)
        {
            int to = BitOperations.TrailingZeroCount(reach);
            if ((enemy >> to & 1) != 0)
                AddPawnMove(from, to, MoveFlags.Capture, lastRank, moves);
            else
                moves.Add(new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture | MoveFlags.EnPassant));
        }
    }

    /// <summary>A knight's or a king's moves to <paramref name="reach"/>, in <paramref name="steps"/>' order (Attacks').</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StepMoves(int from, int[] steps, ulong reach, ulong enemy, List<Move> moves)
    {
        if (reach == 0)
            return;
        if ((reach & (reach - 1)) == 0)   // one square: no order to keep
        {
            AddMove(from, BitOperations.TrailingZeroCount(reach), enemy, moves);
            return;
        }
        foreach (int to in steps)
        {
            if ((reach >> to & 1) != 0)
                AddMove(from, to, enemy, moves);
        }
    }

    /// <summary>A slider's moves to <paramref name="reach"/>, ray by ray in Attacks' order, each ray nearest first.</summary>
    private static void RayMoves(int from, ulong reach, ulong enemy, int firstDirection, int endDirection, List<Move> moves)
    {
        if (reach == 0)
            return;
        if ((reach & (reach - 1)) == 0)   // one square: no order to keep
        {
            AddMove(from, BitOperations.TrailingZeroCount(reach), enemy, moves);
            return;
        }
        for (int d = firstDirection; d < endDirection; d++)
        {
            ulong ray = reach & RaySets[from * Attacks.DirectionCount + d];
            if (RayRunsUp[d])
            {
                for (; ray != 0; ray &= ray - 1)
                    AddMove(from, BitOperations.TrailingZeroCount(ray), enemy, moves);
            }
            else
            {
                while (ray != 0)
                {
                    int to = 63 - BitOperations.LeadingZeroCount(ray);
                    ray ^= 1UL << to;
                    AddMove(from, to, enemy, moves);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddMove(int from, int to, ulong enemy, List<Move> moves) =>
        moves.Add((enemy >> to & 1) != 0
            ? new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture)
            : new Move((byte)from, (byte)to));

    /// <summary>The pseudo-legal moves (or <see cref="Captures(Position, List{Move})"/>') by walking the board, as before the sets.</summary>
    private static void GenerateByRays(Position position, List<Move> moves, bool quiet)
    {
        moves.Clear();
        Colour us = position.SideToMove;
        for (int from = 0; from < 64; from++)
        {
            var piece = position[from];
            if (piece.IsEmpty || piece.Colour != us)
                continue;
            switch (piece.Type)
            {
                case PieceType.Pawn:
                    PawnMovesByRays(position, from, us, quiet, moves);
                    break;
                case PieceType.Knight:
                    StepMovesByRays(position, from, us, Attacks.Knight[from], quiet, moves);
                    break;
                case PieceType.King:
                    StepMovesByRays(position, from, us, Attacks.King[from], quiet, moves);
                    if (quiet)
                        CastlingMoves(position, from, us, moves);
                    break;
                case PieceType.Bishop:
                    SlideMovesByRays(position, from, us, Attacks.FirstBishopDirection, Attacks.DirectionCount, quiet, moves);
                    break;
                case PieceType.Rook:
                    SlideMovesByRays(position, from, us, Attacks.FirstRookDirection, Attacks.FirstBishopDirection, quiet, moves);
                    break;
                case PieceType.Queen:
                    SlideMovesByRays(position, from, us, 0, Attacks.DirectionCount, quiet, moves);
                    break;
            }
        }
    }

    private static bool IsLegal(Position position, Move move, Colour mover)
    {
        var undo = position.MakeMove(move);
        bool legal = !position.InCheck(mover);
        position.UnmakeMove(move, undo);
        return legal;
    }

    /// <summary>A pawn's pushes (without <paramref name="quiet"/>, only a push that promotes, to a queen), then its captures, by walking the board.</summary>
    private static void PawnMovesByRays(Position position, int from, Colour us, bool quiet, List<Move> moves)
    {
        int forward = us == Colour.White ? 8 : -8;
        int rank = from / 8, file = from % 8;
        int startRank = us == Colour.White ? 1 : 6;
        int lastRank = us == Colour.White ? 7 : 0;

        int one = from + forward;
        if (one is >= 0 and < 64 && position[one].IsEmpty)
        {
            if (quiet)
            {
                AddPawnMove(from, one, MoveFlags.None, lastRank, moves);
                int two = one + forward;
                if (rank == startRank && position[two].IsEmpty)
                    moves.Add(new Move((byte)from, (byte)two, PieceType.None, MoveFlags.DoublePawnPush));
            }
            else if (one / 8 == lastRank)
            {
                moves.Add(new Move((byte)from, (byte)one, PieceType.Queen));
            }
        }

        // Towards the a-file, then the h-file. (Not foreach over new[] { -1, 1 }: that allocated an array
        // per pawn per list, 46 bytes a node.)
        for (int side = -1; side <= 1; side += 2)
        {
            int targetFile = file + side;
            if (targetFile is < 0 or > 7)
                continue;
            int to = one + side;
            if (to is < 0 or >= 64)
                continue;
            var target = position[to];
            if (!target.IsEmpty && target.Colour != us)
                AddPawnMove(from, to, MoveFlags.Capture, lastRank, moves);
            else if (to == position.EnPassantSquare)
                moves.Add(new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture | MoveFlags.EnPassant));
        }
    }

    private static void AddPawnMove(int from, int to, MoveFlags flags, int lastRank, List<Move> moves)
    {
        if (to / 8 != lastRank)
        {
            moves.Add(new Move((byte)from, (byte)to, PieceType.None, flags));
            return;
        }
        foreach (var promotion in PromotionPieces)
            moves.Add(new Move((byte)from, (byte)to, promotion, flags));
    }

    private static void StepMovesByRays(Position position, int from, Colour us, int[] targets, bool quiet, List<Move> moves)
    {
        foreach (int to in targets)
        {
            var target = position[to];
            if (target.IsEmpty)
            {
                if (quiet)
                    moves.Add(new Move((byte)from, (byte)to));
            }
            else if (target.Colour != us)
            {
                moves.Add(new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture));
            }
        }
    }

    private static void SlideMovesByRays(Position position, int from, Colour us, int firstDirection, int endDirection,
                                   bool quiet, List<Move> moves)
    {
        for (int d = firstDirection; d < endDirection; d++)
        {
            foreach (int to in Attacks.Rays[from][d])
            {
                var target = position[to];
                if (target.IsEmpty)
                {
                    if (quiet)
                        moves.Add(new Move((byte)from, (byte)to));
                    continue;
                }
                if (target.Colour != us)
                    moves.Add(new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture));
                break;
            }
        }
    }

    /// <summary>
    /// Castling: the right is still held, the rook is on its corner, the
    /// squares between are empty, and the king is not in check and does not
    /// pass through or land on an attacked square.
    /// </summary>
    private static void CastlingMoves(Position position, int from, Colour us, List<Move> moves)
    {
        int home = us == Colour.White ? 4 : 60;
        if (from != home)
            return;
        var (kingside, queenside) = us == Colour.White
            ? (CastlingRights.WhiteKingside, CastlingRights.WhiteQueenside)
            : (CastlingRights.BlackKingside, CastlingRights.BlackQueenside);
        Colour them = Position.Opponent(us);
        var rook = new Piece(PieceType.Rook, us);

        if (position.Castling.HasFlag(kingside)
            && position[home + 3] == rook
            && position[home + 1].IsEmpty && position[home + 2].IsEmpty
            && !position.IsAttacked(home, them)
            && !position.IsAttacked(home + 1, them)
            && !position.IsAttacked(home + 2, them))
        {
            moves.Add(new Move((byte)home, (byte)(home + 2), PieceType.None, MoveFlags.Castle));
        }

        if (position.Castling.HasFlag(queenside)
            && position[home - 4] == rook
            && position[home - 1].IsEmpty && position[home - 2].IsEmpty && position[home - 3].IsEmpty
            && !position.IsAttacked(home, them)
            && !position.IsAttacked(home - 1, them)
            && !position.IsAttacked(home - 2, them))
        {
            moves.Add(new Move((byte)home, (byte)(home - 2), PieceType.None, MoveFlags.Castle));
        }
    }
}
