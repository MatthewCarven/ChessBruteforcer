using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Endgame;

/// <summary>
/// Which pieces are on the board, besides the two kings, written like
/// "KQvK" or "KRvKN".  Pieces are kept strongest first (Q, R, B, N, P).
/// </summary>
public sealed record Material
{
    private static readonly PieceType[] Order =
        { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight, PieceType.Pawn };

    public IReadOnlyList<PieceType> White { get; }
    public IReadOnlyList<PieceType> Black { get; }

    public Material(IEnumerable<PieceType> white, IEnumerable<PieceType> black)
    {
        White = Sorted(white);
        Black = Sorted(black);
    }

    public int PieceCount => 2 + White.Count + Black.Count;

    public bool HasPawns => White.Contains(PieceType.Pawn) || Black.Contains(PieceType.Pawn);

    public static Material Parse(string text)
    {
        string[] sides = text.Trim().ToUpperInvariant().Split('V');
        if (sides.Length != 2 || !sides[0].StartsWith('K') || !sides[1].StartsWith('K'))
            throw new FormatException($"'{text}' is not a material signature like KQvK.");
        return new Material(ParseSide(sides[0][1..]), ParseSide(sides[1][1..]));
    }

    public static Material FromPosition(Position position)
    {
        var white = new List<PieceType>();
        var black = new List<PieceType>();
        for (int square = 0; square < 64; square++)
        {
            var piece = position[square];
            if (piece.IsEmpty || piece.Type == PieceType.King)
                continue;
            (piece.Colour == Colour.White ? white : black).Add(piece.Type);
        }
        return new Material(white, black);
    }

    /// <summary>The same material with the colours swapped.</summary>
    public Material Flipped() => new(Black, White);

    /// <summary>
    /// Tables are only built with the stronger side as white; a position with
    /// the stronger side black is looked up with colours swapped.
    /// </summary>
    public bool IsCanonical => Compare(White, Black) >= 0;

    public Material Canonical => IsCanonical ? this : Flipped();

    public bool Equals(Material? other) =>
        other is not null && White.SequenceEqual(other.White) && Black.SequenceEqual(other.Black);

    public override int GetHashCode() => ToString().GetHashCode();

    public override string ToString() => $"K{Letters(White)}vK{Letters(Black)}";

    private static int Compare(IReadOnlyList<PieceType> a, IReadOnlyList<PieceType> b)
    {
        if (a.Count != b.Count)
            return a.Count.CompareTo(b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            int rank = Rank(b[i]).CompareTo(Rank(a[i]));
            if (rank != 0)
                return rank;
        }
        return 0;
    }

    private static int Rank(PieceType type) => Array.IndexOf(Order, type);

    private static PieceType[] Sorted(IEnumerable<PieceType> pieces)
    {
        var list = pieces.ToArray();
        if (list.Any(p => Rank(p) < 0))
            throw new ArgumentException("Only Q, R, B, N and P can be listed besides the king.");
        return list.OrderBy(Rank).ToArray();
    }

    private static IEnumerable<PieceType> ParseSide(string letters) => letters.Select(c => c switch
    {
        'Q' => PieceType.Queen,
        'R' => PieceType.Rook,
        'B' => PieceType.Bishop,
        'N' => PieceType.Knight,
        'P' => PieceType.Pawn,
        _ => throw new FormatException($"Unknown piece letter '{c}'."),
    });

    private static string Letters(IEnumerable<PieceType> pieces) =>
        string.Concat(pieces.Select(p => new Piece(p, Colour.White).ToFenChar()));
}
