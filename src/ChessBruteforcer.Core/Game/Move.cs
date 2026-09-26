namespace ChessBruteforcer.Core.Game;

[Flags]
public enum MoveFlags : byte
{
    None = 0,
    Capture = 1 << 0,
    DoublePawnPush = 1 << 1,
    EnPassant = 1 << 2,
    Castle = 1 << 3,
}

/// <summary>
/// One move: from and to squares (a1 = 0 … h8 = 63), the piece a pawn
/// promotes to (or None), and what kind of move it is.
/// </summary>
public readonly record struct Move(byte From, byte To, PieceType Promotion = PieceType.None,
                                   MoveFlags Flags = MoveFlags.None)
{
    public bool IsCapture => (Flags & MoveFlags.Capture) != 0;

    public bool IsPromotion => Promotion != PieceType.None;

    /// <summary>UCI long algebraic notation: e2e4, e7e8q, e1g1 for castling.</summary>
    public string ToUci()
    {
        string text = PackedBoard.SquareName(From) + PackedBoard.SquareName(To);
        return Promotion switch
        {
            PieceType.Queen => text + "q",
            PieceType.Rook => text + "r",
            PieceType.Bishop => text + "b",
            PieceType.Knight => text + "n",
            _ => text,
        };
    }

    public override string ToString() => ToUci();
}
