using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Engine;

/// <summary>
/// A first, deliberately simple evaluation in centipawns from the side to
/// move's point of view: material plus piece-square tables (Tomasz
/// Michniewski's "simplified evaluation function"), with the king's table
/// blended from middlegame to endgame as pieces come off, and a bishop pair
/// bonus.  The point is a sound baseline to measure improvements against in
/// matches, not a finished evaluation.
/// </summary>
public static class Evaluation
{
    public const int Pawn = 100, Knight = 320, Bishop = 330, Rook = 500, Queen = 900;
    public const int BishopPair = 30;

    private static readonly int[] Values = { 0, Pawn, Knight, Bishop, Rook, Queen, 0 };

    /// <summary>Game phase weight per piece: 24 with all minor and major pieces on, 0 with none.</summary>
    private static readonly int[] PhaseWeights = { 0, 0, 1, 1, 2, 4, 0 };
    private const int MaxPhase = 24;

    // Tables are written as seen from white, rank 8 first (so index = square ^ 56 for white).
    private static readonly int[] PawnTable =
    {
          0,   0,   0,   0,   0,   0,   0,   0,
         50,  50,  50,  50,  50,  50,  50,  50,
         10,  10,  20,  30,  30,  20,  10,  10,
          5,   5,  10,  25,  25,  10,   5,   5,
          0,   0,   0,  20,  20,   0,   0,   0,
          5,  -5, -10,   0,   0, -10,  -5,   5,
          5,  10,  10, -20, -20,  10,  10,   5,
          0,   0,   0,   0,   0,   0,   0,   0,
    };

    private static readonly int[] KnightTable =
    {
        -50, -40, -30, -30, -30, -30, -40, -50,
        -40, -20,   0,   0,   0,   0, -20, -40,
        -30,   0,  10,  15,  15,  10,   0, -30,
        -30,   5,  15,  20,  20,  15,   5, -30,
        -30,   0,  15,  20,  20,  15,   0, -30,
        -30,   5,  10,  15,  15,  10,   5, -30,
        -40, -20,   0,   5,   5,   0, -20, -40,
        -50, -40, -30, -30, -30, -30, -40, -50,
    };

    private static readonly int[] BishopTable =
    {
        -20, -10, -10, -10, -10, -10, -10, -20,
        -10,   0,   0,   0,   0,   0,   0, -10,
        -10,   0,   5,  10,  10,   5,   0, -10,
        -10,   5,   5,  10,  10,   5,   5, -10,
        -10,   0,  10,  10,  10,  10,   0, -10,
        -10,  10,  10,  10,  10,  10,  10, -10,
        -10,   5,   0,   0,   0,   0,   5, -10,
        -20, -10, -10, -10, -10, -10, -10, -20,
    };

    private static readonly int[] RookTable =
    {
          0,   0,   0,   0,   0,   0,   0,   0,
          5,  10,  10,  10,  10,  10,  10,   5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
         -5,   0,   0,   0,   0,   0,   0,  -5,
          0,   0,   0,   5,   5,   0,   0,   0,
    };

    private static readonly int[] QueenTable =
    {
        -20, -10, -10,  -5,  -5, -10, -10, -20,
        -10,   0,   0,   0,   0,   0,   0, -10,
        -10,   0,   5,   5,   5,   5,   0, -10,
         -5,   0,   5,   5,   5,   5,   0,  -5,
          0,   0,   5,   5,   5,   5,   0,  -5,
        -10,   5,   5,   5,   5,   5,   0, -10,
        -10,   0,   5,   0,   0,   0,   0, -10,
        -20, -10, -10,  -5,  -5, -10, -10, -20,
    };

    private static readonly int[] KingMiddlegameTable =
    {
        -30, -40, -40, -50, -50, -40, -40, -30,
        -30, -40, -40, -50, -50, -40, -40, -30,
        -30, -40, -40, -50, -50, -40, -40, -30,
        -30, -40, -40, -50, -50, -40, -40, -30,
        -20, -30, -30, -40, -40, -30, -30, -20,
        -10, -20, -20, -20, -20, -20, -20, -10,
         20,  20,   0,   0,   0,   0,  20,  20,
         20,  30,  10,   0,   0,  10,  30,  20,
    };

    private static readonly int[] KingEndgameTable =
    {
        -50, -40, -30, -20, -20, -30, -40, -50,
        -30, -20, -10,   0,   0, -10, -20, -30,
        -30, -10,  20,  30,  30,  20, -10, -30,
        -30, -10,  30,  40,  40,  30, -10, -30,
        -30, -10,  30,  40,  40,  30, -10, -30,
        -30, -10,  20,  30,  30,  20, -10, -30,
        -30, -30,   0,   0,   0,   0, -30, -30,
        -50, -30, -30, -30, -30, -30, -30, -50,
    };

    private static readonly int[][] Tables =
    {
        Array.Empty<int>(), PawnTable, KnightTable, BishopTable, RookTable, QueenTable, KingMiddlegameTable,
    };

    public static int PieceValue(PieceType type) => Values[(int)type];

    /// <summary>
    /// The numbers the evaluation uses (Texel tuning, 2026-10-14: tapered, every piece on every square with a
    /// middlegame and an endgame value). The built-in ones reproduce the original evaluation (to the
    /// centipawn: only where the king's blend rounds differently can it move by one); a tuned set is loaded
    /// with <see cref="EvalParams.Load"/>, which the UCI option EvalFile does. Process-wide.
    /// </summary>
    public static EvalParams Parameters { get; set; } = EvalParams.Original();

    /// <summary>The original tables, as a parameter set: middlegame and endgame alike, except the king's.</summary>
    internal static void FillOriginal(EvalParams p)
    {
        for (int type = 1; type <= 6; type++)
            for (int i = 0; i < 64; i++)
            {
                int value = type == 6 ? 0 : Values[type] + Tables[type][i];
                p.Mg[type][i] = type == 6 ? KingMiddlegameTable[i] : value;
                p.Eg[type][i] = type == 6 ? KingEndgameTable[i] : value;
            }
        p.BishopPairMg = p.BishopPairEg = BishopPair;
    }

    /// <summary>The game phase: 24 with every minor and major piece on, 0 with none.</summary>
    public static int Phase(Position position)
    {
        int phase = 0;
        for (int square = 0; square < 64; square++)
            if (!position[square].IsEmpty)
                phase += PhaseWeights[(int)position[square].Type];
        return Math.Min(phase, MaxPhase);
    }

    /// <summary>Score for the side to move, in centipawns.</summary>
    public static int Evaluate(Position position) => Evaluate(position, Parameters);

    /// <summary>Score for the side to move, in centipawns, with the given numbers.</summary>
    public static int Evaluate(Position position, EvalParams p)
    {
        int mg = 0, eg = 0, phase = 0, whiteBishops = 0, blackBishops = 0;
        for (int square = 0; square < 64; square++)
        {
            var piece = position[square];
            if (piece.IsEmpty)
                continue;
            int type = (int)piece.Type;
            phase += PhaseWeights[type];
            if (piece.Colour == Colour.White)
            {
                int i = square ^ 56;   // tables are written as seen from white, rank 8 first
                mg += p.Mg[type][i];
                eg += p.Eg[type][i];
                if (piece.Type == PieceType.Bishop) whiteBishops++;
            }
            else
            {
                mg -= p.Mg[type][square];
                eg -= p.Eg[type][square];
                if (piece.Type == PieceType.Bishop) blackBishops++;
            }
        }
        int pairs = (whiteBishops >= 2 ? 1 : 0) - (blackBishops >= 2 ? 1 : 0);
        mg += pairs * p.BishopPairMg;
        eg += pairs * p.BishopPairEg;
        phase = Math.Min(phase, MaxPhase);
        int total = (mg * phase + eg * (MaxPhase - phase)) / MaxPhase;
        return position.SideToMove == Colour.White ? total : -total;
    }

    /// <summary>True if the side to move has anything besides king and pawns (null-move safety).</summary>
    public static bool HasNonPawnMaterial(Position position)
    {
        var us = position.SideToMove;
        for (int square = 0; square < 64; square++)
        {
            var piece = position[square];
            if (piece.Colour == us && piece.Type is PieceType.Knight or PieceType.Bishop or PieceType.Rook or PieceType.Queen)
                return true;
        }
        return false;
    }
}
