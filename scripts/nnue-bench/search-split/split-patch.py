"""Instrument a scratch copy of the engine for the split probe and the bitboard lab (never the repo's own engine).

    python split-patch.py <scratch>/probe-src

<scratch>/probe-src holds `git archive <commit> src`: 180fe25 (quiescence generates only what it searches) or a
commit whose Search, MoveGenerator, Position, Evaluation and TranspositionTable read the same. The version for
4ecea62, with the quiescence levers' arms, is this script at commit 4dce15e. Hooks.cs, beside this script, goes
into the copy's Engine folder; the edits below add the hooks to Search, Position, Evaluation, TranspositionTable,
MoveGenerator and Tablebase, and the prototype of decision K's (b) and (c) (sets beside the squares, attacks by
table, generation from the sets). Every edit must match exactly as often as stated, or the script stops (a newer
engine needs the edits brought up to date first)."""
import os
import shutil
import sys

here = os.path.dirname(os.path.abspath(__file__))
core = os.path.join(sys.argv[1], "src", "ChessBruteforcer.Core")


def patch(rel, edits):
    path = os.path.join(core, rel)
    with open(path, "rb") as f:
        text = f.read().decode("utf-8-sig").replace("\r\n", "\n")
    for old, new, count in edits:
        found = text.count(old)
        if found != count:
            sys.exit(f"{rel}: expected {count} of:\n{old}\nfound {found}")
        text = text.replace(old, new)
    with open(path, "wb") as f:
        f.write(text.encode("utf-8"))
    print("patched", rel, len(edits), "edits")


search = [
    # Scratch lists for the doubling arms.
    ("    private readonly List<ulong> _hashes = new();\n",
     "    private readonly List<ulong> _hashes = new();\n"
     "    private readonly List<Move> _scratchMoves = new(256);   // the doubling arms' second generation\n"
     "    private readonly int[] _scratchScores = new int[256];\n", 1),

    # Negamax: counts, then each piece of work through its hook.
    ("    private int Negamax(Position position, int depth, int alpha, int beta, int ply, bool allowNull)\n"
     "    {\n"
     "        _pvLength[ply] = ply;\n",
     "    private int Negamax(Position position, int depth, int alpha, int beta, int ply, bool allowNull)\n"
     "    {\n"
     "        _pvLength[ply] = ply;\n"
     "        if (Hooks.Rec) Hooks.NegamaxNodes++;\n", 1),

    ("if (position.HalfmoveClock >= 100 || IsRepetition(position))",
     "if (position.HalfmoveClock >= 100 || RepH(position))", 1),

    ("if (_tablebase is not null && _tablebase.TryProbeWithClock(position, out var outcome))",
     "if (_tablebase is not null && TbH(position, out var outcome))", 1),

    ("        bool inCheck = position.InCheck();\n"
     "        if (inCheck)\n"
     "            depth++;\n",
     "        bool inCheck = ChkE(position, 0);\n"
     "        if (inCheck)\n"
     "            depth++;\n", 1),

    # The hand-off to quiescence, which is told the check status (at depth 0 always "not in check").
    ("        if (depth <= 0)\n"
     "            return Quiescence(position, alpha, beta, ply, inCheck);\n",
     "        if (depth <= 0)\n"
     "        {\n"
     "            if (Hooks.Rec) Hooks.Handoffs++;\n"
     "            return Quiescence(position, alpha, beta, ply, inCheck);\n"
     "        }\n", 1),

    ("        if (_table.TryGet(position.Hash, out var entry))\n",
     "        if (TtGetH(position.Hash, out var entry))\n", 1),

    ("            var nullUndo = position.MakeNullMove();\n"
     "            _hashes.Add(position.Hash);\n"
     "            int nullScore = -Negamax(position, depth - 1 - reduction, -beta, -beta + 1, ply + 1, allowNull: false);\n"
     "            _hashes.RemoveAt(_hashes.Count - 1);\n",
     "            var nullUndo = position.MakeNullMove();\n"
     "            if (Hooks.Rec) Hooks.NullMoves++;\n"
     "            if (!Hooks.NoRep) _hashes.Add(position.Hash);\n"
     "            int nullScore = -Negamax(position, depth - 1 - reduction, -beta, -beta + 1, ply + 1, allowNull: false);\n"
     "            if (!Hooks.NoRep) _hashes.RemoveAt(_hashes.Count - 1);\n", 1),

    ("        var moves = _moveLists[ply];\n"
     "        MoveGenerator.Pseudo(position, moves);\n"
     "        var scores = _moveScores[ply];\n"
     "        ScoreMoves(position, moves, scores, ttMove, ply);\n",
     "        var moves = _moveLists[ply];\n"
     "        if (Hooks.GenX2 is 1 or 3) { MoveGenerator.Pseudo(position, _scratchMoves); Hooks.Sink += _scratchMoves.Count; }\n"
     "        GenH(position, moves, 0);\n"
     "        Hooks.OnGen?.Invoke(position, 0);\n"
     "        var scores = _moveScores[ply];\n"
     "        if (Hooks.ScoreX2) ScoreMoves(position, moves, _scratchScores, ttMove, ply);\n"
     "        if (!Hooks.NoScore) ScoreMoves(position, moves, scores, ttMove, ply);\n", 1),

    ("        for (int i = 0; i < moves.Count; i++)\n"
     "        {\n"
     "            var move = PickNext(moves, scores, i);\n"
     "            var undo = position.MakeMove(move);\n"
     "            if (position.InCheck(us))\n"
     "            {\n"
     "                position.UnmakeMove(move, undo);\n"
     "                continue;\n"
     "            }\n"
     "            legalMoves++;\n"
     "            _hashes.Add(position.Hash);\n",
     "        for (int i = 0; i < moves.Count; i++)\n"
     "        {\n"
     "            var move = Hooks.PickReplay ? PickR(moves, scores, i) : PickNext(moves, scores, i, false);\n"
     "            if (Hooks.NoIllegal && Hooks.ChkL[Hooks.ChkLN] != 0)\n"
     "            {\n"
     "                Hooks.ChkLN++;\n"
     "                continue;\n"
     "            }\n"
     "            if (Hooks.MakeX2) { var undo2 = position.MakeMove(move); position.UnmakeMove(move, undo2); }\n"
     "            var undo = position.MakeMove(move);\n"
     "            if (ChkL(position, us, inCheck, move))\n"
     "            {\n"
     "                position.UnmakeMove(move, undo);\n"
     "                continue;\n"
     "            }\n"
     "            legalMoves++;\n"
     "            if (!Hooks.NoRep) _hashes.Add(position.Hash);\n", 1),

    ("if (depth >= 3 && legalMoves > 4 && quiet && !inCheck && !position.InCheck())",
     "if (depth >= 3 && legalMoves > 4 && quiet && !inCheck && !ChkE(position, 2))", 1),

    ("            _hashes.RemoveAt(_hashes.Count - 1);\n"
     "            position.UnmakeMove(move, undo);\n",
     "            if (!Hooks.NoRep) _hashes.RemoveAt(_hashes.Count - 1);\n"
     "            position.UnmakeMove(move, undo);\n", 1),

    ("                    if (score >= beta)\n"
     "                    {\n"
     "                        if (quiet)\n",
     "                    if (score >= beta)\n"
     "                    {\n"
     "                        if (Hooks.Rec && legalMoves == 1 && move == ttMove)\n"
     "                            Hooks.TtMoveCuts++;\n"
     "                        if (quiet)\n", 1),

    ("        _table.Store(position.Hash, bestMove, ToTable(best, ply), depth, bound);\n",
     "        if (Hooks.Rec) Hooks.TtStores++;\n"
     "        if (!Hooks.NoTt)\n"
     "            _table.Store(position.Hash, bestMove, ToTable(best, ply), depth, bound);\n", 1),

    # Quiescence.
    ("    private int Quiescence(Position position, int alpha, int beta, int ply, bool? knownCheck = null)\n"
     "    {\n"
     "        _pvLength[ply] = ply;\n",
     "    private int Quiescence(Position position, int alpha, int beta, int ply, bool? knownCheck = null)\n"
     "    {\n"
     "        _pvLength[ply] = ply;\n"
     "        if (Hooks.Rec) Hooks.QsNodes++;\n", 1),

    ("        bool inCheck = knownCheck ?? position.InCheck();\n"
     "        int best;\n",
     "        bool inCheck = knownCheck ?? ChkE(position, 1);\n"
     "        int best;\n", 1),

    # (Not in check, quiescence generates only captures and queen promotions: kind 1; in check, every move: kind 2.)
    ("        var moves = _moveLists[ply];\n"
     "        if (inCheck)\n"
     "            MoveGenerator.Pseudo(position, moves);\n"
     "        else\n"
     "            MoveGenerator.Captures(position, moves);\n"
     "        var scores = _moveScores[ply];\n"
     "        ScoreMoves(position, moves, scores, default, ply);\n",
     "        var moves = _moveLists[ply];\n"
     "        if (Hooks.GenX2 is 1 or 2)\n"
     "        {\n"
     "            if (inCheck) MoveGenerator.Pseudo(position, _scratchMoves); else MoveGenerator.Captures(position, _scratchMoves);\n"
     "            Hooks.Sink += _scratchMoves.Count;\n"
     "        }\n"
     "        Hooks.OnGen?.Invoke(position, inCheck ? 2 : 1);\n"
     "        GenH(position, moves, inCheck ? 2 : 1);\n"
     "        var scores = _moveScores[ply];\n"
     "        if (Hooks.ScoreX2) ScoreMoves(position, moves, _scratchScores, default, ply);\n"
     "        if (!Hooks.NoScore) ScoreMoves(position, moves, scores, default, ply);\n", 1),

    # (Its first lines read as Negamax's loop; the edit above has changed that one already.)
    ("        for (int i = 0; i < moves.Count; i++)\n"
     "        {\n"
     "            var move = PickNext(moves, scores, i);\n"
     "            var undo = position.MakeMove(move);\n"
     "            if (position.InCheck(us))\n"
     "            {\n"
     "                position.UnmakeMove(move, undo);\n"
     "                continue;\n"
     "            }\n"
     "            int score = -Quiescence(position, -beta, -alpha, ply + 1);\n",
     "        for (int i = 0; i < moves.Count; i++)\n"
     "        {\n"
     "            var move = Hooks.PickReplay ? PickR(moves, scores, i) : PickNext(moves, scores, i, true);\n"
     "            if (Hooks.NoIllegal && Hooks.ChkL[Hooks.ChkLN] != 0)\n"
     "            {\n"
     "                Hooks.ChkLN++;\n"
     "                continue;\n"
     "            }\n"
     "            if (Hooks.MakeX2) { var undo2 = position.MakeMove(move); position.UnmakeMove(move, undo2); }\n"
     "            var undo = position.MakeMove(move);\n"
     "            if (ChkL(position, us, inCheck, move))\n"
     "            {\n"
     "                position.UnmakeMove(move, undo);\n"
     "                continue;\n"
     "            }\n"
     "            int score = -Quiescence(position, -beta, -alpha, ply + 1);\n", 1),

    ("    private static Move PickNext(List<Move> moves, int[] scores, int start)\n"
     "    {\n"
     "        int bestIndex = start;\n"
     "        for (int i = start + 1; i < moves.Count; i++)\n"
     "            if (scores[i] > scores[bestIndex]) bestIndex = i;\n",
     "    private static Move PickNext(List<Move> moves, int[] scores, int start, bool qs)\n"
     "    {\n"
     "        int bestIndex = start;\n"
     "        for (int i = start + 1; i < moves.Count; i++)\n"
     "            if (scores[i] > scores[bestIndex]) bestIndex = i;\n"
     "        if (Hooks.Rec)\n"
     "            Hooks.RecordPick(bestIndex, qs, moves.Count - start);\n", 1),

    # The hook methods.
    ("    /// <summary>Has this position occurred before, since the last capture or pawn move?</summary>\n",
     """    // ---- the split probe's hooks ----

    private static Move PickR(List<Move> moves, int[] scores, int start)
    {
        int bestIndex = Hooks.Pick[Hooks.PickN++];
        if (bestIndex != start)
        {
            (moves[start], moves[bestIndex]) = (moves[bestIndex], moves[start]);
            if (!Hooks.NoScore)
                (scores[start], scores[bestIndex]) = (scores[bestIndex], scores[start]);
        }
        return moves[start];
    }

    // kind: 0 Negamax (every move), 1 quiescence (captures and queen promotions), 2 quiescence in check (every move)
    private static void GenH(Position position, List<Move> moves, int kind)
    {
        if ((Hooks.NoGen >> kind & 1) != 0)
        {
            Hooks.ReplayGen(moves, kind);
            return;
        }
        if (Hooks.BbGen)
            AttackTables.Generate(position, moves, kind != 1);
        else if (Hooks.BbOrdered)
            AttackTables.GenerateOrdered(position, moves, kind != 1);
        else if (kind == 1)
            MoveGenerator.Captures(position, moves);
        else
            MoveGenerator.Pseudo(position, moves);
        if (Hooks.VerifyBb)
            Hooks.CheckBb(position, moves, kind != 1);
        if (Hooks.VerifyBbo)
            Hooks.CheckBbo(position, moves, kind != 1);
        if (Hooks.Rec)
            Hooks.RecordGen(moves, kind);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool ChkE(Position position, int site)
    {
        if (Hooks.NoChk)
            return Hooks.ChkE[Hooks.ChkEN++] != 0;
        Hooks.OnChk?.Invoke(position, position.SideToMove, 1);
        bool check = position.InCheck();
        if (Hooks.ChkX2)
            Hooks.Sink += position.InCheck() ? 1 : 0;
        if (Hooks.Rec)
            Hooks.RecordChkE(check, site);
        return check;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool ChkL(Position position, Colour us, bool nodeInCheck, Move move)
    {
        if (Hooks.NoChk || Hooks.NoChkLegal)
            return Hooks.ChkL[Hooks.ChkLN++] != 0;
        Hooks.OnChk?.Invoke(position, us, 0);
        bool check = position.InCheck(us);
        if (Hooks.NoIllegal)
            Hooks.ChkLN++;
        if (Hooks.ChkX2)
            Hooks.Sink += position.InCheck(us) ? 1 : 0;
        if (Hooks.Rec)
            Hooks.RecordChkL(check, nodeInCheck, position[move.To].Type == PieceType.King);
        return check;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool TtGetH(ulong key, out TableEntry entry)
    {
        if (Hooks.NoTt)
        {
            ulong data = Hooks.Tt[Hooks.TtN++];
            entry = TranspositionTable.UnpackData(data);
            return data != 0;
        }
        bool hit = _table.TryGet(key, out entry);
        if (Hooks.Rec)
            Hooks.RecordTt(hit ? TranspositionTable.PackEntry(entry) : 0);
        return hit;
    }

    private bool RepH(Position position)
    {
        if (Hooks.NoRep)
            return Hooks.Rep[Hooks.RepN++] != 0;
        bool repeated = IsRepetition(position);
        if (Hooks.Rec)
            Hooks.RecordRep(repeated, Math.Min(_hashes.Count - 1, position.HalfmoveClock));
        return repeated;
    }

    private bool TbH(Position position, out Outcome outcome)
    {
        if (Hooks.NoTb)
            return Hooks.ReplayTb(out outcome);
        bool hit = _tablebase!.TryProbeWithClock(position, out outcome);
        if (Hooks.Rec)
            Hooks.RecordTb(position, hit, outcome);
        return hit;
    }

    /// <summary>Has this position occurred before, since the last capture or pawn move?</summary>
""", 1),
]

position = [
    # The prototype's sets (decision K's (b) and (c)): kept in Put while Hooks.KeepSets, read by AttackTables.
    ("    private readonly Piece[] _squares = new Piece[64];\n",
     "    private readonly Piece[] _squares = new Piece[64];\n"
     "    internal readonly ulong[] Sets = new ulong[16];      // the split probe's prototype: [colour * 8 + type]\n"
     "    internal readonly ulong[] ColourSets = new ulong[2];\n", 1),
    ("        var old = _squares[square];\n"
     "        Hash ^= Zobrist.Piece(old, square) ^ Zobrist.Piece(piece, square);\n",
     "        var old = _squares[square];\n"
     "        Hash ^= Zobrist.Piece(old, square) ^ Zobrist.Piece(piece, square);\n"
     "        if (ChessBruteforcer.Core.Engine.Hooks.KeepSets)\n"
     "        {\n"
     "            ulong bit = 1UL << square;\n"
     "            if (!old.IsEmpty)\n"
     "            {\n"
     "                Sets[(int)old.Colour * 8 + (int)old.Type] &= ~bit;\n"
     "                ColourSets[(int)old.Colour] &= ~bit;\n"
     "            }\n"
     "            if (!piece.IsEmpty)\n"
     "            {\n"
     "                Sets[(int)piece.Colour * 8 + (int)piece.Type] |= bit;\n"
     "                ColourSets[(int)piece.Colour] |= bit;\n"
     "            }\n"
     "        }\n", 1),
    ("    public bool IsAttacked(int square, Colour by)\n"
     "    {\n",
     "    public bool IsAttacked(int square, Colour by)\n"
     "    {\n"
     "        if (ChessBruteforcer.Core.Engine.Hooks.TableAttacks)\n"
     "            return ChessBruteforcer.Core.Engine.AttackTables.IsAttacked(this, square, by);\n", 1),
    ("            _nnue?.PushCastle(mover, move.From, move.To, rook, rookFrom, rookTo);\n",
     "            if (!ChessBruteforcer.Core.Engine.Hooks.SkipUpdates)\n"
     "                _nnue?.PushCastle(mover, move.From, move.To, rook, rookFrom, rookTo);\n", 1),
    ("        else if (_nnue is not null)\n",
     "        else if (_nnue is not null && !ChessBruteforcer.Core.Engine.Hooks.SkipUpdates)\n", 1),
    ("        _nnue?.Pop(this);\n",
     "        if (!ChessBruteforcer.Core.Engine.Hooks.SkipUpdates)\n"
     "            _nnue?.Pop(this);\n", 1),
]

evaluation = [
    ("    public static int Evaluate(Position position) =>\n"
     "        position.Accumulators is { } network ? network.Evaluate(position.SideToMove) : Evaluate(position, Parameters);\n",
     """    public static int Evaluate(Position position)
    {
        if (position.Accumulators is not { } network)
            return Evaluate(position, Parameters);
        switch (Hooks.EvalMode)
        {
            case 1:
            {
                int value = network.Evaluate(position.SideToMove);
                Hooks.AddEval(value);
                return value;
            }
            case 2:
                return Hooks.Evals[Hooks.EvalN++];
            case 3:
            {
                int value = network.Evaluate(position.SideToMove), recorded = Hooks.Evals[Hooks.EvalN++];
                if (value != recorded)
                    Hooks.Mismatches++;
                return recorded;
            }
            default:
                return network.Evaluate(position.SideToMove);
        }
    }
""", 1),
]

tablebase = [
    # The prototype's piece count by popcount (with Hooks.TableAttacks).
    ("        int pieces = 0;\n"
     "        for (int square = 0; square < 64 && pieces <= MaxPieces; square++)\n"
     "            if (!position[square].IsEmpty) pieces++;\n",
     "        int pieces = 0;\n"
     "        if (ChessBruteforcer.Core.Engine.Hooks.TableAttacks)\n"
     "            pieces = System.Numerics.BitOperations.PopCount(position.ColourSets[0] | position.ColourSets[1]);\n"
     "        else\n"
     "            for (int square = 0; square < 64 && pieces <= MaxPieces; square++)\n"
     "                if (!position[square].IsEmpty) pieces++;\n", 1),
]

table = [
    ("    /// <summary>Per-mille of slots in use, for UCI \"hashfull\".</summary>\n",
     "    internal static ulong PackEntry(TableEntry entry) => Pack(entry.Move, entry.Score, entry.Depth, entry.Bound);\n"
     "\n"
     "    internal static TableEntry UnpackData(ulong data) => Unpack(0, data);\n"
     "\n"
     "    /// <summary>Per-mille of slots in use, for UCI \"hashfull\".</summary>\n", 1),
]

generator = [
    # Castling's attack tests through a hook (counted, and replayed by the -castle arm).
    ("            && !position.IsAttacked(", "            && !Attacked(position, ", 6),
    ("    private static void CastlingMoves(Position position, int from, Colour us, List<Move> moves)\n",
     "    private static bool Attacked(Position position, int square, Colour by)\n"
     "    {\n"
     "        if (ChessBruteforcer.Core.Engine.Hooks.NoCastle)\n"
     "            return ChessBruteforcer.Core.Engine.Hooks.ReplayCastle();\n"
     "        bool attacked = position.IsAttacked(square, by);\n"
     "        if (ChessBruteforcer.Core.Engine.Hooks.Rec)\n"
     "            ChessBruteforcer.Core.Engine.Hooks.RecordCastle(attacked);\n"
     "        return attacked;\n"
     "    }\n"
     "\n"
     "    private static void CastlingMoves(Position position, int from, Colour us, List<Move> moves)\n", 1),
]

patch(os.path.join("Engine", "Search.cs"), search)
patch(os.path.join("Game", "Position.cs"), position)
patch(os.path.join("Engine", "Evaluation.cs"), evaluation)
patch(os.path.join("Engine", "TranspositionTable.cs"), table)
patch(os.path.join("Game", "MoveGenerator.cs"), generator)
patch(os.path.join("Endgame", "Tablebase.cs"), tablebase)
shutil.copyfile(os.path.join(here, "Hooks.cs"), os.path.join(core, "Engine", "Hooks.cs"))
print("copied Hooks.cs")
