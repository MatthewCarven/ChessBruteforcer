using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
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
    public static int NoGen;          // move lists replayed, a bit per kind: 1 Negamax's, 2 quiescence's captures, 4 its evasions
    public static bool NoCastle;      // castling's attack tests (inside generation) replayed
    public static Action<Position, int>? OnGen;            // the bitboard lab's sampling

    // The prototype of decision K's (b) and (c) (AttackTables, below). Each gives today's answers, so the tree is
    // the same, except BbGen's other move order.
    public static bool KeepSets;      // a set per colour and piece type kept beside the squares (Position.Put)
    public static bool TableAttacks;  // IsAttacked (InCheck, castling's tests) by attack tables; the tablebase's count by popcount
    public static bool Compressed;    // the slider tables in 16 bits, expanded by PDEP (~210 KB against ~840 KB)
    public static bool BbGen;         // move generation from the sets (another move order: its own tree)
    public static bool BbOrdered;     // move generation from the sets in today's order (the identical tree)
    public static bool VerifyBb;      // every list also generated from the sets and compared, as a set (the tree untouched)
    public static bool VerifyBbo;     // every list also generated from the sets in today's order and compared, in order
    public static long BbLists, BbMismatches;
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
    public static byte[] Ca = new byte[1 << 16];
    public static int CaN;
    public static bool RecordGens;    // keep the move lists themselves (the generation arms' replay), a stream per kind
    public static ushort[][] GenC = { new ushort[1 << 16], new ushort[1 << 16], new ushort[1 << 16] };
    public static int[] GenCN = new int[3];
    public static Move[][] GenM = { new Move[1 << 16], new Move[1 << 16], new Move[1 << 16] };
    public static int[] GenMN = new int[3];

    // Counts, in the recording runs.
    public static long NegamaxNodes, QsNodes, ChkEntryNeg, ChkEntryQs, ChkLmr, Makes, MakesInCheck, Illegal, IllegalInCheck,
                       GensNeg, GensQs, GensQsChk, MovesNeg, MovesQs, MovesQsChk, PicksNeg, PicksQs, ScanNeg, ScanQs,
                       TtProbes, TtHits, TtStores, RepCalls, RepYes, RepWindow, TbCalls, TbCastling, TbSmall, TbHits,
                       NullMoves, EvalCount, CastleAttackChecks, MakesKing, IllegalKing, IllegalOther, ChkEYes,
                       Handoffs, TtMoveCuts;

    public static void ResetStreams()
    {
        EvalN = ChkEN = ChkLN = PickN = TtN = RepN = TbN = TbOutN = CaN = 0;
        Array.Clear(GenCN);
        Array.Clear(GenMN);
    }

    public static void ResetArms()
    {
        EvalMode = 0;
        SkipUpdates = Rec = false;
        NoChk = NoChkLegal = NoIllegal = NoTt = NoRep = NoTb = PickReplay = NoScore = NoCastle = false;
        KeepSets = TableAttacks = Compressed = BbGen = BbOrdered = VerifyBb = VerifyBbo = false;
        NoGen = 0;
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

    // kind: 0 Negamax (every move), 1 quiescence (captures and queen promotions), 2 quiescence in check (every move)
    public static void RecordGen(List<Move> moves, int kind)
    {
        if (kind == 0)
        {
            GensNeg++;
            MovesNeg += moves.Count;
        }
        else
        {
            GensQs++;
            MovesQs += moves.Count;
            if (kind == 2)
            {
                GensQsChk++;
                MovesQsChk += moves.Count;
            }
        }
        if (!RecordGens)
            return;
        Add(ref GenC[kind], ref GenCN[kind], (ushort)moves.Count);
        while (GenMN[kind] + moves.Count > GenM[kind].Length)
            Array.Resize(ref GenM[kind], GenM[kind].Length * 2);
        CollectionsMarshal.AsSpan(moves).CopyTo(GenM[kind].AsSpan(GenMN[kind]));
        GenMN[kind] += moves.Count;
    }

    public static void ReplayGen(List<Move> moves, int kind)
    {
        int count = GenC[kind][GenCN[kind]++];
        CollectionsMarshal.SetCount(moves, count);
        GenM[kind].AsSpan(GenMN[kind], count).CopyTo(CollectionsMarshal.AsSpan(moves));
        GenMN[kind] += count;
    }

    public static void RecordCastle(bool attacked)
    {
        Add(ref Ca, ref CaN, (byte)(attacked ? 1 : 0));
        CastleAttackChecks++;
    }

    public static bool ReplayCastle() => Ca[CaN++] != 0;

    private static readonly List<Move> BbScratch = new(256);
    private static readonly int[] KeysA = new int[256], KeysB = new int[256];

    private static int Key(Move m) => m.From | m.To << 8 | (int)m.Promotion << 16 | (int)m.Flags << 24;

    /// <summary>The ordered verify arm: <paramref name="moves"/> (today's list) against the sets' list in today's order, move by move.</summary>
    public static void CheckBbo(Position position, List<Move> moves, bool quiet)
    {
        AttackTables.GenerateOrdered(position, BbScratch, quiet);
        BbLists++;
        if (BbScratch.Count != moves.Count)
        {
            BbMismatches++;
            return;
        }
        for (int i = 0; i < moves.Count; i++)
        {
            if (BbScratch[i] != moves[i])
            {
                BbMismatches++;
                return;
            }
        }
    }

    /// <summary>The verify arm: <paramref name="moves"/> (today's list) against the sets' list, as sets.</summary>
    public static void CheckBb(Position position, List<Move> moves, bool quiet)
    {
        AttackTables.Generate(position, BbScratch, quiet);
        BbLists++;
        int count = moves.Count;
        if (BbScratch.Count != count)
        {
            BbMismatches++;
            return;
        }
        for (int i = 0; i < count; i++)
        {
            KeysA[i] = Key(moves[i]);
            KeysB[i] = Key(BbScratch[i]);
        }
        Array.Sort(KeysA, 0, count);
        Array.Sort(KeysB, 0, count);
        for (int i = 0; i < count; i++)
        {
            if (KeysA[i] != KeysB[i])
            {
                BbMismatches++;
                return;
            }
        }
    }
}

/// <summary>
/// The probe's prototype of NNUE.md decision K's (b) and (c), never the engine's own: a set per colour and piece type
/// kept beside the squares (Position.Sets and ColourSets, while Hooks.KeepSets), attacks from the sets by table
/// (knights, kings and pawns a set per square; rooks and bishops by PEXT into a table per square, 64-bit entries,
/// ~840 KB, or with Hooks.Compressed 16-bit entries expanded by PDEP, ~210 KB), and move generation from the sets
/// (the bitboard lab's, lab/lab.cs, which checked it against today's on 24,700 positions from the search).
/// </summary>
public static class AttackTables
{
    private static readonly ulong[] Knight = new ulong[64], King = new ulong[64];
    private static readonly ulong[] Pawn = new ulong[128];   // [colour * 64 + square]: what a pawn there attacks
    private static readonly ulong[] RookMask = new ulong[64], BishopMask = new ulong[64];   // the occupancy that matters
    private static readonly ulong[] RookRays = new ulong[64], BishopRays = new ulong[64];   // all it reaches on an empty board
    private static readonly int[] RookBase = new int[64], BishopBase = new int[64];
    private static readonly ulong[] RookTable, BishopTable;
    private static readonly ushort[] RookTable16, BishopTable16;
    private static readonly PieceType[] Promotions = { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight };

    static AttackTables()
    {
        (int, int)[] knight = { (1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2) };
        (int, int)[] king = { (0, 1), (0, -1), (1, 0), (-1, 0), (1, 1), (1, -1), (-1, 1), (-1, -1) };
        (int, int)[] rook = { (0, 1), (0, -1), (1, 0), (-1, 0) };
        (int, int)[] bishop = { (1, 1), (1, -1), (-1, 1), (-1, -1) };
        for (int sq = 0; sq < 64; sq++)
        {
            Knight[sq] = Steps(sq, knight);
            King[sq] = Steps(sq, king);
            Pawn[sq] = Steps(sq, new[] { (-1, 1), (1, 1) });
            Pawn[64 + sq] = Steps(sq, new[] { (-1, -1), (1, -1) });
            RookMask[sq] = Slide(sq, 0, rook, inner: true);
            BishopMask[sq] = Slide(sq, 0, bishop, inner: true);
            RookRays[sq] = Slide(sq, 0, rook, inner: false);
            BishopRays[sq] = Slide(sq, 0, bishop, inner: false);
        }
        (RookTable, RookTable16) = Fill(RookMask, RookRays, RookBase, rook);
        (BishopTable, BishopTable16) = Fill(BishopMask, BishopRays, BishopBase, bishop);
    }

    private static ulong Steps(int sq, (int F, int R)[] steps)
    {
        ulong set = 0;
        foreach (var (f, r) in steps)
        {
            int file = sq % 8 + f, rank = sq / 8 + r;
            if (file is >= 0 and < 8 && rank is >= 0 and < 8)
                set |= 1UL << (rank * 8 + file);
        }
        return set;
    }

    // The squares a slider on sq reaches through occ (inner: the occupancy mask, the last square of each ray left out).
    private static ulong Slide(int sq, ulong occ, (int F, int R)[] dirs, bool inner)
    {
        ulong set = 0;
        foreach (var (f, r) in dirs)
        {
            int file = sq % 8 + f, rank = sq / 8 + r;
            while (file is >= 0 and < 8 && rank is >= 0 and < 8)
            {
                if (inner && !(file + f is >= 0 and < 8 && rank + r is >= 0 and < 8))
                    break;
                ulong bit = 1UL << (rank * 8 + file);
                set |= bit;
                if ((occ & bit) != 0)
                    break;
                file += f;
                rank += r;
            }
        }
        return set;
    }

    private static (ulong[], ushort[]) Fill(ulong[] masks, ulong[] rays, int[] bases, (int, int)[] dirs)
    {
        int total = 0;
        for (int sq = 0; sq < 64; sq++)
        {
            bases[sq] = total;
            total += 1 << BitOperations.PopCount(masks[sq]);
        }
        var table = new ulong[total];
        var table16 = new ushort[total];
        for (int sq = 0; sq < 64; sq++)
        {
            for (int i = 0; i < 1 << BitOperations.PopCount(masks[sq]); i++)
            {
                ulong attacks = Slide(sq, Bmi2.X64.ParallelBitDeposit((ulong)i, masks[sq]), dirs, inner: false);
                table[bases[sq] + i] = attacks;
                table16[bases[sq] + i] = (ushort)Bmi2.X64.ParallelBitExtract(attacks, rays[sq]);
            }
        }
        return (table, table16);
    }

    public static ulong Rook(int sq, ulong occ)
    {
        int i = RookBase[sq] + (int)Bmi2.X64.ParallelBitExtract(occ, RookMask[sq]);
        return Hooks.Compressed ? Bmi2.X64.ParallelBitDeposit(RookTable16[i], RookRays[sq]) : RookTable[i];
    }

    public static ulong Bishop(int sq, ulong occ)
    {
        int i = BishopBase[sq] + (int)Bmi2.X64.ParallelBitExtract(occ, BishopMask[sq]);
        return Hooks.Compressed ? Bmi2.X64.ParallelBitDeposit(BishopTable16[i], BishopRays[sq]) : BishopTable[i];
    }

    public static bool IsAttacked(Position position, int sq, Colour by)
    {
        var sets = position.Sets;
        int c = (int)by * 8;
        if ((Pawn[(1 - (int)by) * 64 + sq] & sets[c + 1]) != 0) return true;
        if ((Knight[sq] & sets[c + 2]) != 0) return true;
        if ((King[sq] & sets[c + 6]) != 0) return true;
        ulong occ = position.ColourSets[0] | position.ColourSets[1];
        ulong queens = sets[c + 5];
        ulong diagonal = sets[c + 3] | queens;
        if (diagonal != 0 && (Bishop(sq, occ) & diagonal) != 0) return true;
        ulong straight = sets[c + 4] | queens;
        return straight != 0 && (Rook(sq, occ) & straight) != 0;
    }

    /// <summary>The pseudo-legal moves (with <paramref name="quiet"/> false, only what quiescence searches), from the sets.</summary>
    public static void Generate(Position position, List<Move> moves, bool quiet)
    {
        moves.Clear();
        var sets = position.Sets;
        Colour us = position.SideToMove, them = Position.Opponent(us);
        int u = (int)us * 8;
        ulong own = position.ColourSets[(int)us], enemy = position.ColourSets[(int)them];
        ulong occ = own | enemy, empty = ~occ;

        // Pawns.
        ulong pawns = sets[u + 1];
        bool white = us == Colour.White;
        ulong last = white ? 0xFF00000000000000UL : 0xFFUL;
        int up = white ? 8 : -8;
        ulong single = (white ? pawns << 8 : pawns >> 8) & empty;
        for (ulong set = single & last; set != 0; set &= set - 1)
        {
            int to = BitOperations.TrailingZeroCount(set);
            if (quiet)
                foreach (var promotion in Promotions)
                    moves.Add(new Move((byte)(to - up), (byte)to, promotion));
            else
                moves.Add(new Move((byte)(to - up), (byte)to, PieceType.Queen));
        }
        if (quiet)
        {
            for (ulong set = single & ~last; set != 0; set &= set - 1)
            {
                int to = BitOperations.TrailingZeroCount(set);
                moves.Add(new Move((byte)(to - up), (byte)to));
            }
            ulong third = white ? 0xFF0000UL : 0xFF0000000000UL;
            ulong two = (white ? (single & third) << 8 : (single & third) >> 8) & empty;
            for (ulong set = two; set != 0; set &= set - 1)
            {
                int to = BitOperations.TrailingZeroCount(set);
                moves.Add(new Move((byte)(to - 2 * up), (byte)to, PieceType.None, MoveFlags.DoublePawnPush));
            }
        }
        const ulong notA = 0xFEFEFEFEFEFEFEFEUL, notH = 0x7F7F7F7F7F7F7F7FUL;
        ulong left = (white ? (pawns & notA) << 7 : (pawns & notA) >> 9) & enemy;    // towards the a-file
        ulong right = (white ? (pawns & notH) << 9 : (pawns & notH) >> 7) & enemy;   // towards the h-file
        PawnCaptures(moves, left, white ? 7 : -9, last);
        PawnCaptures(moves, right, white ? 9 : -7, last);
        int ep = position.EnPassantSquare;
        if (ep >= 0)
            for (ulong set = Pawn[(int)them * 64 + ep] & pawns; set != 0; set &= set - 1)
                moves.Add(new Move((byte)BitOperations.TrailingZeroCount(set), (byte)ep, PieceType.None, MoveFlags.Capture | MoveFlags.EnPassant));

        // Pieces.
        ulong targets = quiet ? ~own : enemy;
        for (ulong set = sets[u + 2]; set != 0; set &= set - 1)
        {
            int from = BitOperations.TrailingZeroCount(set);
            Add(moves, from, Knight[from] & targets, enemy);
        }
        for (ulong set = sets[u + 3]; set != 0; set &= set - 1)
        {
            int from = BitOperations.TrailingZeroCount(set);
            Add(moves, from, Bishop(from, occ) & targets, enemy);
        }
        for (ulong set = sets[u + 4]; set != 0; set &= set - 1)
        {
            int from = BitOperations.TrailingZeroCount(set);
            Add(moves, from, Rook(from, occ) & targets, enemy);
        }
        for (ulong set = sets[u + 5]; set != 0; set &= set - 1)
        {
            int from = BitOperations.TrailingZeroCount(set);
            Add(moves, from, (Rook(from, occ) | Bishop(from, occ)) & targets, enemy);
        }
        int king = position.KingSquare(us);
        if (king < 0)
            return;
        Add(moves, king, King[king] & targets, enemy);

        // Castling, as the engine has it: the right, the rook on its corner, the squares between empty, and the king
        // not in check and not passing through or landing on an attacked square.
        int home = white ? 4 : 60;
        if (!quiet || king != home)
            return;
        var (kingside, queenside) = white
            ? (CastlingRights.WhiteKingside, CastlingRights.WhiteQueenside)
            : (CastlingRights.BlackKingside, CastlingRights.BlackQueenside);
        ulong rooks = sets[u + 4];
        var castling = position.Castling;
        if ((castling & kingside) != 0 && (rooks & 1UL << (home + 3)) != 0 && (occ & (3UL << (home + 1))) == 0
            && !IsAttacked(position, home, them) && !IsAttacked(position, home + 1, them) && !IsAttacked(position, home + 2, them))
            moves.Add(new Move((byte)home, (byte)(home + 2), PieceType.None, MoveFlags.Castle));
        if ((castling & queenside) != 0 && (rooks & 1UL << (home - 4)) != 0 && (occ & (7UL << (home - 3))) == 0
            && !IsAttacked(position, home, them) && !IsAttacked(position, home - 1, them) && !IsAttacked(position, home - 2, them))
            moves.Add(new Move((byte)home, (byte)(home - 2), PieceType.None, MoveFlags.Castle));
    }

    // Today's rays as sets ([square * 8 + direction]), and whether a direction runs up the board (nearest first = lowest bit first).
    private static readonly ulong[] RayMask = BuildRayMasks();
    private static readonly bool[] Ascending = Enumerable.Range(0, Attacks.DirectionCount).Select(d => Attacks.Rays[27][d][0] > 27).ToArray();

    private static ulong[] BuildRayMasks()
    {
        var masks = new ulong[64 * Attacks.DirectionCount];
        for (int sq = 0; sq < 64; sq++)
            for (int d = 0; d < Attacks.DirectionCount; d++)
                foreach (int to in Attacks.Rays[sq][d])
                    masks[sq * Attacks.DirectionCount + d] |= 1UL << to;
        return masks;
    }

    /// <summary>
    /// The same moves as <see cref="Generate"/>, in today's order (MoveGenerator's: from-squares up; a pawn's pushes, then
    /// its captures towards the a-file and the h-file; knights and kings in Attacks' order; sliders ray by ray, nearest
    /// first; castling after the king's steps), so PickNext breaks ties as today and the tree stays the same.
    /// </summary>
    public static void GenerateOrdered(Position position, List<Move> moves, bool quiet)
    {
        moves.Clear();
        Colour us = position.SideToMove;
        ulong own = position.ColourSets[(int)us], enemy = position.ColourSets[1 - (int)us];
        ulong occ = own | enemy;
        ulong targets = quiet ? ~own : enemy;
        for (ulong set = own; set != 0; set &= set - 1)
        {
            int from = BitOperations.TrailingZeroCount(set);
            switch (position[from].Type)
            {
                case PieceType.Pawn:
                    PawnOrdered(position, from, us, quiet, occ, enemy, moves);
                    break;
                case PieceType.Knight:
                    ListOrdered(moves, from, Attacks.Knight[from], targets, enemy);
                    break;
                case PieceType.King:
                    ListOrdered(moves, from, Attacks.King[from], targets, enemy);
                    if (quiet)
                        CastlingOrdered(position, from, us, occ, moves);
                    break;
                case PieceType.Bishop:
                    RaysOrdered(moves, from, Bishop(from, occ) & targets, enemy, Attacks.FirstBishopDirection, Attacks.DirectionCount);
                    break;
                case PieceType.Rook:
                    RaysOrdered(moves, from, Rook(from, occ) & targets, enemy, Attacks.FirstRookDirection, Attacks.FirstBishopDirection);
                    break;
                case PieceType.Queen:
                    RaysOrdered(moves, from, (Rook(from, occ) | Bishop(from, occ)) & targets, enemy, 0, Attacks.DirectionCount);
                    break;
            }
        }
    }

    private static void PawnOrdered(Position position, int from, Colour us, bool quiet, ulong occ, ulong enemy, List<Move> moves)
    {
        int forward = us == Colour.White ? 8 : -8;
        int rank = from / 8, file = from % 8;
        int startRank = us == Colour.White ? 1 : 6;
        int lastRank = us == Colour.White ? 7 : 0;
        int one = from + forward;
        if (one is >= 0 and < 64 && (occ >> one & 1) == 0)
        {
            if (quiet)
            {
                AddPawnOrdered(from, one, MoveFlags.None, lastRank, moves);
                int two = one + forward;
                if (rank == startRank && (occ >> two & 1) == 0)
                    moves.Add(new Move((byte)from, (byte)two, PieceType.None, MoveFlags.DoublePawnPush));
            }
            else if (one / 8 == lastRank)
            {
                moves.Add(new Move((byte)from, (byte)one, PieceType.Queen));
            }
        }
        for (int side = -1; side <= 1; side += 2)
        {
            int targetFile = file + side;
            if (targetFile is < 0 or > 7)
                continue;
            int to = one + side;
            if (to is < 0 or >= 64)
                continue;
            if ((enemy >> to & 1) != 0)
                AddPawnOrdered(from, to, MoveFlags.Capture, lastRank, moves);
            else if (to == position.EnPassantSquare)
                moves.Add(new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture | MoveFlags.EnPassant));
        }
    }

    private static void AddPawnOrdered(int from, int to, MoveFlags flags, int lastRank, List<Move> moves)
    {
        if (to / 8 != lastRank)
        {
            moves.Add(new Move((byte)from, (byte)to, PieceType.None, flags));
            return;
        }
        foreach (var promotion in Promotions)
            moves.Add(new Move((byte)from, (byte)to, promotion, flags));
    }

    private static void ListOrdered(List<Move> moves, int from, int[] list, ulong targets, ulong enemy)
    {
        foreach (int to in list)
        {
            if ((targets >> to & 1) == 0)
                continue;
            moves.Add((enemy >> to & 1) != 0
                ? new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture)
                : new Move((byte)from, (byte)to));
        }
    }

    private static void RaysOrdered(List<Move> moves, int from, ulong reach, ulong enemy, int firstDirection, int endDirection)
    {
        for (int d = firstDirection; d < endDirection; d++)
        {
            ulong ray = reach & RayMask[from * Attacks.DirectionCount + d];
            while (ray != 0)
            {
                int to = Ascending[d] ? BitOperations.TrailingZeroCount(ray) : 63 - BitOperations.LeadingZeroCount(ray);
                ray &= ~(1UL << to);
                moves.Add((enemy >> to & 1) != 0
                    ? new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture)
                    : new Move((byte)from, (byte)to));
            }
        }
    }

    private static void CastlingOrdered(Position position, int from, Colour us, ulong occ, List<Move> moves)
    {
        int home = us == Colour.White ? 4 : 60;
        if (from != home)
            return;
        var (kingside, queenside) = us == Colour.White
            ? (CastlingRights.WhiteKingside, CastlingRights.WhiteQueenside)
            : (CastlingRights.BlackKingside, CastlingRights.BlackQueenside);
        Colour them = Position.Opponent(us);
        ulong rooks = position.Sets[(int)us * 8 + (int)PieceType.Rook];
        var castling = position.Castling;
        if ((castling & kingside) != 0 && (rooks & 1UL << (home + 3)) != 0 && (occ & (3UL << (home + 1))) == 0
            && !IsAttacked(position, home, them) && !IsAttacked(position, home + 1, them) && !IsAttacked(position, home + 2, them))
            moves.Add(new Move((byte)home, (byte)(home + 2), PieceType.None, MoveFlags.Castle));
        if ((castling & queenside) != 0 && (rooks & 1UL << (home - 4)) != 0 && (occ & (7UL << (home - 3))) == 0
            && !IsAttacked(position, home, them) && !IsAttacked(position, home - 1, them) && !IsAttacked(position, home - 2, them))
            moves.Add(new Move((byte)home, (byte)(home - 2), PieceType.None, MoveFlags.Castle));
    }

    private static void PawnCaptures(List<Move> moves, ulong set, int step, ulong last)
    {
        for (; set != 0; set &= set - 1)
        {
            int to = BitOperations.TrailingZeroCount(set);
            int from = to - step;
            if ((last & 1UL << to) == 0)
                moves.Add(new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture));
            else
                foreach (var promotion in Promotions)
                    moves.Add(new Move((byte)from, (byte)to, promotion, MoveFlags.Capture));
        }
    }

    private static void Add(List<Move> moves, int from, ulong targets, ulong enemy)
    {
        for (; targets != 0; targets &= targets - 1)
        {
            int to = BitOperations.TrailingZeroCount(targets);
            moves.Add((enemy & 1UL << to) != 0
                ? new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture)
                : new Move((byte)from, (byte)to));
        }
    }
}
