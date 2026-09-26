namespace ChessBruteforcer.Core.Game;

/// <summary>
/// Zobrist hashing: a random 64-bit key for every (piece, square), for
/// black to move, for each castling-rights combination and for each en
/// passant file.  A position's hash is the XOR of the keys that apply, so a
/// move updates it with a handful of XORs, and the same position reached by
/// different move orders gets the same hash.
/// </summary>
public static class Zobrist
{
    private static readonly ulong[] PieceKeys = new ulong[2 * 7 * 64];
    private static readonly ulong[] CastlingKeys = new ulong[16];
    private static readonly ulong[] EnPassantKeys = new ulong[8];

    public static readonly ulong BlackToMove;

    static Zobrist()
    {
        // Fixed seed: hashes are the same on every run, so they can be stored.
        var rng = new Random(20260926);
        ulong Next() => (ulong)rng.NextInt64() ^ ((ulong)rng.NextInt64() << 1);
        for (int i = 0; i < PieceKeys.Length; i++)
            PieceKeys[i] = Next();
        for (int i = 0; i < CastlingKeys.Length; i++)
            CastlingKeys[i] = Next();
        for (int i = 0; i < EnPassantKeys.Length; i++)
            EnPassantKeys[i] = Next();
        BlackToMove = Next();
        // "No castling rights" contributes nothing, so a position built square
        // by square hashes the same as one read from FEN.
        CastlingKeys[(int)CastlingRights.None] = 0;
    }

    public static ulong Piece(Piece piece, int square) =>
        piece.IsEmpty ? 0 : PieceKeys[((int)piece.Colour * 7 + (int)piece.Type) * 64 + square];

    public static ulong Castling(CastlingRights rights) => CastlingKeys[(int)rights];

    public static ulong EnPassant(int square) => square < 0 ? 0 : EnPassantKeys[square % 8];
}
