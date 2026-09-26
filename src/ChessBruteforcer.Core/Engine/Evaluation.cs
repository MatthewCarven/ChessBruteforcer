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

    /// <summary>Score for the side to move, in centipawns.</summary>
    public static int Evaluate(Position position)
    {
        int white = 0, black = 0;
        int whiteKingMid = 0, whiteKingEnd = 0, blackKingMid = 0, blackKingEnd = 0;
        int phase = 0, whiteBishops = 0, blackBishops = 0;

        for (int square = 0; square < 64; square++)
        {
            var piece = position[square];
            if (piece.IsEmpty)
                continue;
            int type = (int)piece.Type;
            bool isWhite = piece.Colour == Colour.White;
            int tableIndex = isWhite ? square ^ 56 : square;
            phase += PhaseWeights[type];

            if (piece.Type == PieceType.King)
            {
                if (isWhite)
                {
                    whiteKingMid = KingMiddlegameTable[tableIndex];
                    whiteKingEnd = KingEndgameTable[tableIndex];
                }
                else
                {
                    blackKingMid = KingMiddlegameTable[tableIndex];
                    blackKingEnd = KingEndgameTable[tableIndex];
                }
                continue;
            }

            int score = Values[type] + Tables[type][tableIndex];
            if (isWhite)
            {
                white += score;
                if (piece.Type == PieceType.Bishop) whiteBishops++;
            }
            else
            {
                black += score;
                if (piece.Type == PieceType.Bishop) blackBishops++;
            }
        }

        if (whiteBishops >= 2) white += BishopPair;
        if (blackBishops >= 2) black += BishopPair;

        phase = Math.Min(phase, MaxPhase);
        int kings = ((whiteKingMid - blackKingMid) * phase + (whiteKingEnd - blackKingEnd) * (MaxPhase - phase)) / MaxPhase;
        int total = white - black + kings;
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
