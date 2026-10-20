// The bitboard lab: what move generation and the check test would cost on bitboards, against today's
// 64-square board, on positions the search really meets. Bench's six searches (depth 9, the full engine) are
// sampled through the probe's hooks: every n-th position the search generates moves for, and every n-th
// position it asks InCheck about. Each sample is then built twice, as the engine's Position and as a bitboard
// board (12 piece sets, PEXT attack tables for the sliders), and every answer is checked equal: the same moves
// (as a set), the same captures-only list, the same InCheck. Then rounds of timed passes over the samples, the
// passes rotated, thread time (QueryThreadCycleTime), one thread at below-normal priority.
//   lab <net .nnue> <depth> <sample every n> <rounds>
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Engine.Nnue;
using ChessBruteforcer.Core.Game;

Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
const double TscGhz = 2.496;
if (!Bmi2.X64.IsSupported)
    throw new PlatformNotSupportedException("no BMI2");
var net = Network.Load(args[0]);
int depth = int.Parse(args[1]);
int every = int.Parse(args[2]);
int rounds = int.Parse(args[3]);
string[] fens =
{
    "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
    "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
    "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
    "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
    "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
    "r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
};

// 1. Samples.
var genSamples = new List<(string Fen, int Kind)>();
var chkSamples = new List<(string Fen, Colour Colour, int Kind)>();
long genCalls = 0, chkCalls = 0;
Hooks.OnGen = (p, kind) => { if (genCalls++ % every == 0) genSamples.Add((p.ToFen(), kind)); };
Hooks.OnChk = (p, c, kind) => { if (chkCalls++ % every == 0) chkSamples.Add((p.ToFen(), c, kind)); };
foreach (string fen in fens)
    new Search(new TranspositionTable(16)).Run(Position.FromFen(fen, net), new SearchLimits { Depth = depth });
Hooks.OnGen = null;
Hooks.OnChk = null;
Console.WriteLine($"lab: depth {depth}, every {every}th: {genSamples.Count:N0} of {genCalls:N0} generations " +
                  $"({genSamples.Count(s => s.Kind == 0):N0} Negamax, {genSamples.Count(s => s.Kind == 1):N0} quiescence, {genSamples.Count(s => s.Kind == 2):N0} quiescence in check), " +
                  $"{chkSamples.Count:N0} of {chkCalls:N0} InCheck calls ({chkSamples.Count(s => s.Kind == 0):N0} after make)");

var genPos = genSamples.Select(s => Position.FromFen(s.Fen)).ToArray();
var genBb = genPos.Select(Bb.From).ToArray();
var qsIdx = Enumerable.Range(0, genSamples.Count).Where(i => genSamples[i].Kind == 1).ToArray();
var qsPos = qsIdx.Select(i => genPos[i]).ToArray();
var qsBb = qsIdx.Select(i => genBb[i]).ToArray();
var chkPos = chkSamples.Select(s => Position.FromFen(s.Fen)).ToArray();
var chkBb = chkPos.Select(Bb.From).ToArray();
var chkCol = chkSamples.Select(s => s.Colour).ToArray();

// 2. Every answer checked equal.
var a = new List<Move>(256);
var b = new List<Move>(256);
static int Key(Move m) => m.From | m.To << 8 | (int)m.Promotion << 16 | (int)m.Flags << 24;
bool SameSet(List<Move> x, List<Move> y) =>
    x.Count == y.Count && x.Select(Key).Order().SequenceEqual(y.Select(Key).Order());
for (int i = 0; i < genPos.Length; i++)
{
    MoveGenerator.Pseudo(genPos[i], a);
    genBb[i].Generate(b, capturesOnly: false);
    if (!SameSet(a, b))
        throw new Exception($"moves differ at {genSamples[i].Fen}: {a.Count} against {b.Count}");
    MoveGenerator.Captures(genPos[i], a);
    genBb[i].Generate(b, capturesOnly: true);
    if (!SameSet(a, b))
        throw new Exception($"captures differ at {genSamples[i].Fen}: {a.Count} against {b.Count}");
}
for (int i = 0; i < chkPos.Length; i++)
    for (int c = 0; c < 2; c++)
        if (chkPos[i].InCheck((Colour)c) != chkBb[i].InCheck((Colour)c))
            throw new Exception($"InCheck differs at {chkSamples[i].Fen}");
Console.WriteLine("checked: every sample gives the same moves, captures and InCheck on both boards");

// 3. Timed passes.
long sink = 0;
var passes = new (string Name, int Calls, Action Run)[]
{
    ("generation, today", genPos.Length, () => { foreach (var p in genPos) { MoveGenerator.Pseudo(p, a); sink += a.Count; } }),
    ("generation, bitboards", genPos.Length, () => { foreach (var p in genBb) { p.Generate(a, false); sink += a.Count; } }),
    ("quiescence captures, today", qsPos.Length, () => { foreach (var p in qsPos) { MoveGenerator.Captures(p, a); sink += a.Count; } }),
    ("quiescence captures, bitboards", qsPos.Length, () => { foreach (var p in qsBb) { p.Generate(a, true); sink += a.Count; } }),
    ("quiescence full list, today", qsPos.Length, () => { foreach (var p in qsPos) { MoveGenerator.Pseudo(p, a); sink += a.Count; } }),
    ("InCheck, today", chkPos.Length, () => { for (int i = 0; i < chkPos.Length; i++) sink += chkPos[i].InCheck(chkCol[i]) ? 1 : 0; }),
    ("InCheck, bitboards", chkPos.Length, () => { for (int i = 0; i < chkBb.Length; i++) sink += chkBb[i].InCheck(chkCol[i]) ? 1 : 0; }),
};
var times = passes.ToDictionary(p => p.Name, _ => new List<double>());
nint thread = Native.GetCurrentThread();
for (int round = 0; round <= rounds; round++)   // round 0 warms up
{
    for (int k = 0; k < passes.Length; k++)
    {
        var pass = passes[(k + round * 3) % passes.Length];
        Native.QueryThreadCycleTime(thread, out ulong before);
        pass.Run();
        Native.QueryThreadCycleTime(thread, out ulong after);
        if (round > 0)
            times[pass.Name].Add((after - before) / TscGhz / pass.Calls);
    }
}
double Median(List<double> v) { var s = v.Order().ToList(); return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2; }
Console.WriteLine($"ns a call, medians of {rounds} rounds (sink {sink % 10})");
foreach (var pass in passes)
    Console.WriteLine($"  {pass.Name,-32} {Median(times[pass.Name]),7:0.0}   rounds {string.Join(' ', times[pass.Name].Select(x => x.ToString("0.0")))}");

static class Native
{
    [DllImport("kernel32.dll")]
    public static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryThreadCycleTime(nint thread, out ulong cycles);
}

/// <summary>Attack sets: knights, kings and pawns by table; rooks and bishops by PEXT into a table per square.</summary>
static class Tables
{
    public static readonly ulong[] Knight = new ulong[64], King = new ulong[64];
    public static readonly ulong[] Pawn = new ulong[128];   // [colour * 64 + square]: what a pawn there attacks
    private static readonly ulong[] RookMask = new ulong[64], BishopMask = new ulong[64];
    private static readonly int[] RookBase = new int[64], BishopBase = new int[64];
    private static readonly ulong[] RookTable, BishopTable;

    static Tables()
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
        }
        RookTable = Fill(RookMask, RookBase, rook);
        BishopTable = Fill(BishopMask, BishopBase, bishop);
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

    // The squares a slider on sq reaches through occ (inner: the relevant-occupancy mask, the last square of each ray left out).
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

    private static ulong[] Fill(ulong[] masks, int[] bases, (int, int)[] dirs)
    {
        int total = 0;
        for (int sq = 0; sq < 64; sq++)
        {
            bases[sq] = total;
            total += 1 << BitOperations.PopCount(masks[sq]);
        }
        var table = new ulong[total];
        for (int sq = 0; sq < 64; sq++)
            for (int i = 0; i < 1 << BitOperations.PopCount(masks[sq]); i++)
                table[bases[sq] + i] = Slide(sq, Bmi2.X64.ParallelBitDeposit((ulong)i, masks[sq]), dirs, inner: false);
        return table;
    }

    public static ulong Rook(int sq, ulong occ) => RookTable[RookBase[sq] + (int)Bmi2.X64.ParallelBitExtract(occ, RookMask[sq])];

    public static ulong Bishop(int sq, ulong occ) => BishopTable[BishopBase[sq] + (int)Bmi2.X64.ParallelBitExtract(occ, BishopMask[sq])];
}

/// <summary>A bitboard board: a set per colour and piece type, both colours' sets, and the squares (for what a capture takes).</summary>
sealed class Bb
{
    private readonly ulong[] _p = new ulong[16];   // [colour * 8 + type]
    private ulong _white, _black;
    private readonly int[] _king = new int[2];
    private Colour _side;
    private CastlingRights _castling;
    private int _ep;

    public static Bb From(Position position)
    {
        var bb = new Bb { _side = position.SideToMove, _castling = position.Castling, _ep = position.EnPassantSquare };
        for (int sq = 0; sq < 64; sq++)
        {
            var piece = position[sq];
            if (piece.IsEmpty)
                continue;
            bb._p[(int)piece.Colour * 8 + (int)piece.Type] |= 1UL << sq;
            if (piece.Colour == Colour.White) bb._white |= 1UL << sq;
            else bb._black |= 1UL << sq;
            if (piece.Type == PieceType.King)
                bb._king[(int)piece.Colour] = sq;
        }
        return bb;
    }

    public bool IsAttacked(int sq, Colour by)
    {
        int c = (int)by * 8;
        ulong occ = _white | _black;
        if ((Tables.Pawn[(1 - (int)by) * 64 + sq] & _p[c + 1]) != 0) return true;
        if ((Tables.Knight[sq] & _p[c + 2]) != 0) return true;
        if ((Tables.King[sq] & _p[c + 6]) != 0) return true;
        ulong queens = _p[c + 5];
        if ((Tables.Bishop(sq, occ) & (_p[c + 3] | queens)) != 0) return true;
        return (Tables.Rook(sq, occ) & (_p[c + 4] | queens)) != 0;
    }

    public bool InCheck(Colour colour) => IsAttacked(_king[(int)colour], colour == Colour.White ? Colour.Black : Colour.White);

    private static readonly PieceType[] Promotions = { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight };

    /// <summary>The engine's pseudo-legal moves (or only what quiescence searches: captures, en passant, queen promotions).</summary>
    public void Generate(List<Move> moves, bool capturesOnly)
    {
        moves.Clear();
        Colour us = _side, them = us == Colour.White ? Colour.Black : Colour.White;
        int u = (int)us * 8;
        ulong own = us == Colour.White ? _white : _black, enemy = us == Colour.White ? _black : _white;
        ulong occ = _white | _black, empty = ~occ;

        // Pawns.
        ulong pawns = _p[u + 1];
        bool white = us == Colour.White;
        ulong last = white ? 0xFF00000000000000UL : 0xFFUL;
        int up = white ? 8 : -8;
        ulong single = (white ? pawns << 8 : pawns >> 8) & empty;
        for (ulong set = single & last; set != 0; set &= set - 1)
        {
            int to = BitOperations.TrailingZeroCount(set);
            if (capturesOnly)
                moves.Add(new Move((byte)(to - up), (byte)to, PieceType.Queen));
            else
                foreach (var promotion in Promotions)
                    moves.Add(new Move((byte)(to - up), (byte)to, promotion));
        }
        if (!capturesOnly)
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
        if (_ep >= 0)
            for (ulong set = Tables.Pawn[(int)them * 64 + _ep] & pawns; set != 0; set &= set - 1)
                moves.Add(new Move((byte)BitOperations.TrailingZeroCount(set), (byte)_ep, PieceType.None, MoveFlags.Capture | MoveFlags.EnPassant));

        // Pieces.
        ulong targets = capturesOnly ? enemy : ~own;
        for (ulong set = _p[u + 2]; set != 0; set &= set - 1)
        {
            int from = BitOperations.TrailingZeroCount(set);
            Add(moves, from, Tables.Knight[from] & targets, enemy);
        }
        for (ulong set = _p[u + 3]; set != 0; set &= set - 1)
        {
            int from = BitOperations.TrailingZeroCount(set);
            Add(moves, from, Tables.Bishop(from, occ) & targets, enemy);
        }
        for (ulong set = _p[u + 4]; set != 0; set &= set - 1)
        {
            int from = BitOperations.TrailingZeroCount(set);
            Add(moves, from, Tables.Rook(from, occ) & targets, enemy);
        }
        for (ulong set = _p[u + 5]; set != 0; set &= set - 1)
        {
            int from = BitOperations.TrailingZeroCount(set);
            Add(moves, from, (Tables.Rook(from, occ) | Tables.Bishop(from, occ)) & targets, enemy);
        }
        int king = _king[(int)us];
        Add(moves, king, Tables.King[king] & targets, enemy);

        // Castling, as the engine has it: the right, the rook on its corner, the squares between empty, and the
        // king not in check and not passing through or landing on an attacked square.
        if (capturesOnly)
            return;
        int home = white ? 4 : 60;
        if (king != home)
            return;
        var (kingside, queenside) = white
            ? (CastlingRights.WhiteKingside, CastlingRights.WhiteQueenside)
            : (CastlingRights.BlackKingside, CastlingRights.BlackQueenside);
        ulong rooks = _p[u + 4];
        if ((_castling & kingside) != 0 && (rooks & 1UL << (home + 3)) != 0 && (occ & (3UL << (home + 1))) == 0
            && !IsAttacked(home, them) && !IsAttacked(home + 1, them) && !IsAttacked(home + 2, them))
            moves.Add(new Move((byte)home, (byte)(home + 2), PieceType.None, MoveFlags.Castle));
        if ((_castling & queenside) != 0 && (rooks & 1UL << (home - 4)) != 0 && (occ & (7UL << (home - 3))) == 0
            && !IsAttacked(home, them) && !IsAttacked(home - 1, them) && !IsAttacked(home - 2, them))
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
