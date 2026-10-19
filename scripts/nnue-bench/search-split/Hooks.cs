using System.Runtime.InteropServices;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Engine;

/// <summary>
/// Measurement hooks for the scratchpad split probe (never committed). A recording run (Rec) notes, call by
/// call, what each piece of a node's work answered: every evaluation, every InCheck, PickNext's choice, the
/// table's probe, the repetition and tablebase answers, the move lists. A removal arm then replays one
/// piece's answers instead of doing its work, on the identical tree (checked: the same nodes, every stream
/// read to its end). A doubling arm does one piece's work twice, which needs no recording.
/// </summary>
public static class Hooks
{
    // Evaluations: 0 the network; 1 the network, recorded; 2 replayed (no forward pass); 3 the network, checked.
    public static int EvalMode;
    public static bool SkipUpdates;   // no accumulator pushes or pops (only with replayed evaluations)
    public static bool Rec;           // the recording run

    // Removal arms.
    public static bool NoChk;         // every InCheck in the search replayed: node entry, LMR's gives-check, after make
    public static bool NoChkLegal;    // only the after-make legality test replayed
    public static bool NoIllegal;     // illegal moves never made: no make, no test, no unmake
    public static bool NoTt;          // table probes replayed, stores skipped
    public static bool NoRep;         // repetition answers replayed, the hash list not kept
    public static bool NoTb;          // tablebase answers replayed
    public static bool PickReplay;    // PickNext's choice replayed (the swap kept, the scan gone)
    public static bool NoScore;       // ScoreMoves skipped (only with PickReplay)
    public static bool NoGen;         // move lists replayed (no generation)
    public static bool QsBreak;       // quiescence stops at its first quiet move (exact: quiet moves sort last)
    public static bool NoAlloc;       // PawnMoves walks a static {-1, 1} instead of allocating one each call (exact)
    public static bool PassCheck;     // Negamax hands quiescence the check status it knows (exact)
    public static bool QsCaptures;    // quiescence, not in check, generates only what it searches
    public static bool VerifyCaptures; // check Captures against the full list, filtered (the tree untouched)
    public static long CaptureLists, CaptureMismatches;
    public static Action<Position, int>? OnGen;            // the bitboard lab's sampling
    public static Action<Position, Colour, int>? OnChk;

    // Doubling arms.
    public static int GenX2;          // 1: every generation twice; 2: quiescence's only; 3: Negamax's only
    public static bool MakeX2;        // an extra make + unmake (the board only) before every make
    public static bool ChkX2;         // every InCheck twice
    public static bool ScoreX2;       // every ScoreMoves twice

    public static long Sink;
    public static long Mismatches;

    // The streams.
    public static int[] Evals = new int[1 << 16];
    public static int EvalN;
    public static byte[] ChkE = new byte[1 << 16];
    public static int ChkEN;
    public static byte[] ChkL = new byte[1 << 16];
    public static int ChkLN;
    public static byte[] Pick = new byte[1 << 16];
    public static int PickN;
    public static ulong[] Tt = new ulong[1 << 16];
    public static int TtN;
    public static byte[] Rep = new byte[1 << 16];
    public static int RepN;
    public static byte[] Tb = new byte[1 << 16];
    public static int TbN;
    public static Outcome[] TbOut = new Outcome[16];
    public static int TbOutN;
    public static bool RecordGens;    // keep the move lists themselves (the generation arm's replay)
    public static ushort[] GenC = new ushort[1 << 16];
    public static int GenCN;
    public static Move[] GenM = new Move[1 << 16];
    public static int GenMN;

    // Counts, in the recording runs.
    public static long NegamaxNodes, QsNodes, ChkEntryNeg, ChkEntryQs, ChkLmr, Makes, MakesInCheck, Illegal, IllegalInCheck,
                       GensNeg, GensQs, MovesNeg, MovesQs, PicksNeg, PicksQs, ScanNeg, ScanQs, QsSkipped,
                       TtProbes, TtHits, TtStores, RepCalls, RepYes, RepWindow, TbCalls, TbCastling, TbSmall, TbHits,
                       NullMoves, EvalCount, CastleAttackChecks, MakesKing, IllegalKing, IllegalOther, ChkEYes,
                       Handoffs, TtMoveCuts;

    public static void ResetStreams() =>
        EvalN = ChkEN = ChkLN = PickN = TtN = RepN = TbN = TbOutN = GenCN = GenMN = 0;

    public static void ResetArms()
    {
        EvalMode = 0;
        SkipUpdates = Rec = false;
        NoChk = NoChkLegal = NoIllegal = NoTt = NoRep = NoTb = PickReplay = NoScore = NoGen = QsBreak = NoAlloc = false;
        PassCheck = QsCaptures = VerifyCaptures = false;
        GenX2 = 0;
        MakeX2 = ChkX2 = ScoreX2 = false;
    }

    private static void Add<T>(ref T[] array, ref int count, T value)
    {
        if (count == array.Length)
            Array.Resize(ref array, array.Length * 2);
        array[count++] = value;
    }

    public static void AddEval(int value)
    {
        Add(ref Evals, ref EvalN, value);
        EvalCount++;
    }

    public static void RecordChkE(bool check, int site)
    {
        Add(ref ChkE, ref ChkEN, (byte)(check ? 1 : 0));
        if (site == 0) ChkEntryNeg++;
        else if (site == 1) ChkEntryQs++;
        else ChkLmr++;
        if (check) ChkEYes++;
    }

    public static void RecordChkL(bool check, bool nodeInCheck, bool king)
    {
        Add(ref ChkL, ref ChkLN, (byte)(check ? 1 : 0));
        Makes++;
        if (nodeInCheck) MakesInCheck++;
        else if (king) MakesKing++;
        if (check)
        {
            Illegal++;
            if (nodeInCheck) IllegalInCheck++;
            else if (king) IllegalKing++;
            else IllegalOther++;
        }
    }

    public static void RecordPick(int index, bool qs, int remaining)
    {
        Add(ref Pick, ref PickN, (byte)index);
        if (qs)
        {
            PicksQs++;
            ScanQs += remaining - 1;
        }
        else
        {
            PicksNeg++;
            ScanNeg += remaining - 1;
        }
    }

    public static void RecordTt(ulong data)
    {
        Add(ref Tt, ref TtN, data);
        TtProbes++;
        if (data != 0) TtHits++;
    }

    public static void RecordRep(bool repeated, int window)
    {
        Add(ref Rep, ref RepN, (byte)(repeated ? 1 : 0));
        RepCalls++;
        if (repeated) RepYes++;
        RepWindow += window;
    }

    public static void RecordTb(Position position, bool hit, Outcome outcome)
    {
        Add(ref Tb, ref TbN, (byte)(hit ? 1 : 0));
        if (hit)
        {
            Add(ref TbOut, ref TbOutN, outcome);
            TbHits++;
        }
        TbCalls++;
        if (position.Castling != CastlingRights.None)
        {
            TbCastling++;
            return;
        }
        int pieces = 0;
        for (int square = 0; square < 64; square++)
            if (!position[square].IsEmpty) pieces++;
        if (pieces <= Tablebase.MaxPieces) TbSmall++;
    }

    public static bool ReplayTb(out Outcome outcome)
    {
        bool hit = Tb[TbN++] != 0;
        outcome = hit ? TbOut[TbOutN++] : Outcome.Draw;
        return hit;
    }

    public static void RecordGen(List<Move> moves, bool qs)
    {
        if (qs)
        {
            GensQs++;
            MovesQs += moves.Count;
        }
        else
        {
            GensNeg++;
            MovesNeg += moves.Count;
        }
        if (!RecordGens)
            return;
        Add(ref GenC, ref GenCN, (ushort)moves.Count);
        while (GenMN + moves.Count > GenM.Length)
            Array.Resize(ref GenM, GenM.Length * 2);
        CollectionsMarshal.AsSpan(moves).CopyTo(GenM.AsSpan(GenMN));
        GenMN += moves.Count;
    }

    public static void ReplayGen(List<Move> moves)
    {
        int count = GenC[GenCN++];
        CollectionsMarshal.SetCount(moves, count);
        GenM.AsSpan(GenMN, count).CopyTo(CollectionsMarshal.AsSpan(moves));
        GenMN += count;
    }
}
