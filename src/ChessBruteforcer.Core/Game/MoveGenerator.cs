namespace ChessBruteforcer.Core.Game;

/// <summary>
/// Generates moves.  Pseudo-legal moves follow how the pieces move; legal
/// moves are the pseudo-legal ones that don't leave the mover's own king
/// in check, found by making each move and looking.
/// </summary>
public static class MoveGenerator
{
    private static readonly PieceType[] PromotionPieces =
        { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight };

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
    public static void Pseudo(Position position, List<Move> moves)
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
                    PawnMoves(position, from, us, moves);
                    break;
                case PieceType.Knight:
                    StepMoves(position, from, us, Attacks.Knight[from], moves);
                    break;
                case PieceType.King:
                    StepMoves(position, from, us, Attacks.King[from], moves);
                    CastlingMoves(position, from, us, moves);
                    break;
                case PieceType.Bishop:
                    SlideMoves(position, from, us, Attacks.FirstBishopDirection, Attacks.DirectionCount, moves);
                    break;
                case PieceType.Rook:
                    SlideMoves(position, from, us, Attacks.FirstRookDirection, Attacks.FirstBishopDirection, moves);
                    break;
                case PieceType.Queen:
                    SlideMoves(position, from, us, 0, Attacks.DirectionCount, moves);
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

    private static void PawnMoves(Position position, int from, Colour us, List<Move> moves)
    {
        int forward = us == Colour.White ? 8 : -8;
        int rank = from / 8, file = from % 8;
        int startRank = us == Colour.White ? 1 : 6;
        int lastRank = us == Colour.White ? 7 : 0;

        int one = from + forward;
        if (one is >= 0 and < 64 && position[one].IsEmpty)
        {
            AddPawnMove(from, one, MoveFlags.None, lastRank, moves);
            int two = one + forward;
            if (rank == startRank && position[two].IsEmpty)
                moves.Add(new Move((byte)from, (byte)two, PieceType.None, MoveFlags.DoublePawnPush));
        }

        foreach (int side in new[] { -1, 1 })
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

    private static void StepMoves(Position position, int from, Colour us, int[] targets, List<Move> moves)
    {
        foreach (int to in targets)
        {
            var target = position[to];
            if (target.IsEmpty)
                moves.Add(new Move((byte)from, (byte)to));
            else if (target.Colour != us)
                moves.Add(new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture));
        }
    }

    private static void SlideMoves(Position position, int from, Colour us, int firstDirection, int endDirection,
                                   List<Move> moves)
    {
        for (int d = firstDirection; d < endDirection; d++)
        {
            foreach (int to in Attacks.Rays[from][d])
            {
                var target = position[to];
                if (target.IsEmpty)
                {
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
