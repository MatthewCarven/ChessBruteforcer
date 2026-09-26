namespace ChessBruteforcer.Core.Game;

/// <summary>Precomputed target squares and rays for every square.</summary>
public static class Attacks
{
    /// <summary>Rook directions are 0-3, bishop directions 4-7.</summary>
    private static readonly (int File, int Rank)[] Directions =
    {
        (0, 1), (0, -1), (1, 0), (-1, 0),
        (1, 1), (1, -1), (-1, 1), (-1, -1),
    };

    public const int FirstRookDirection = 0;
    public const int FirstBishopDirection = 4;
    public const int DirectionCount = 8;

    public static readonly int[][] Knight = Targets(new[]
    {
        (1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2),
    });

    public static readonly int[][] King = Targets(Directions);

    /// <summary>Rays[square][direction] = squares in that direction, nearest first.</summary>
    public static readonly int[][][] Rays = BuildRays();

    private static int[][] Targets((int File, int Rank)[] steps)
    {
        var result = new int[64][];
        for (int square = 0; square < 64; square++)
        {
            int file = square % 8, rank = square / 8;
            result[square] = steps
                .Select(s => (File: file + s.File, Rank: rank + s.Rank))
                .Where(t => t.File is >= 0 and < 8 && t.Rank is >= 0 and < 8)
                .Select(t => t.Rank * 8 + t.File)
                .ToArray();
        }
        return result;
    }

    private static int[][][] BuildRays()
    {
        var rays = new int[64][][];
        for (int square = 0; square < 64; square++)
        {
            rays[square] = new int[DirectionCount][];
            for (int d = 0; d < DirectionCount; d++)
            {
                var ray = new List<int>();
                int file = square % 8 + Directions[d].File, rank = square / 8 + Directions[d].Rank;
                while (file is >= 0 and < 8 && rank is >= 0 and < 8)
                {
                    ray.Add(rank * 8 + file);
                    file += Directions[d].File;
                    rank += Directions[d].Rank;
                }
                rays[square][d] = ray.ToArray();
            }
        }
        return rays;
    }
}
