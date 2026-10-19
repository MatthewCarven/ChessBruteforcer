"""Instrument a scratch copy of the engine for the split probe and the bitboard lab (never the repo's own engine).

    python split-patch.py <scratch>/probe-src

<scratch>/probe-src holds `git archive <commit> src` (the probe was made from 4ecea62). Hooks.cs, beside this
script, goes into the copy's Engine folder; the edits below add the hooks to Search, Position, Evaluation,
TranspositionTable and MoveGenerator. Every edit must match exactly as often as stated, or the script stops (a
newer engine needs the edits brought up to date first)."""
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

    # (The hand-off to quiescence can pass the check status, which at depth 0 is always "not in check".)
    ("        if (depth <= 0)\n"
     "            return Quiescence(position, alpha, beta, ply);\n",
     "        if (depth <= 0)\n"
     "        {\n"
     "            if (Hooks.Rec) Hooks.Handoffs++;\n"
     "            return Quiescence(position, alpha, beta, ply, Hooks.PassCheck ? (inCheck ? 1 : 0) : -1);\n"
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
     "        GenH(position, moves, false);\n"
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
    ("    private int Quiescence(Position position, int alpha, int beta, int ply)\n"
     "    {\n"
     "        _pvLength[ply] = ply;\n",
     "    private int Quiescence(Position position, int alpha, int beta, int ply, int knownCheck = -1)\n"
     "    {\n"
     "        _pvLength[ply] = ply;\n"
     "        if (Hooks.Rec) Hooks.QsNodes++;\n", 1),

    ("        bool inCheck = position.InCheck();\n"
     "        int best;\n",
     "        bool inCheck = knownCheck >= 0 ? knownCheck != 0 : ChkE(position, 1);\n"
     "        int best;\n", 1),

    ("        var moves = _moveLists[ply];\n"
     "        MoveGenerator.Pseudo(position, moves);\n"
     "        var scores = _moveScores[ply];\n"
     "        ScoreMoves(position, moves, scores, default, ply);\n",
     "        var moves = _moveLists[ply];\n"
     "        if (Hooks.GenX2 is 1 or 2) { MoveGenerator.Pseudo(position, _scratchMoves); Hooks.Sink += _scratchMoves.Count; }\n"
     "        Hooks.OnGen?.Invoke(position, inCheck ? 2 : 1);\n"
     "        if (Hooks.QsCaptures && !inCheck)\n"
     "            MoveGenerator.Captures(position, moves);\n"
     "        else\n"
     "            GenH(position, moves, true);\n"
     "        if (Hooks.VerifyCaptures && !inCheck)\n"
     "        {\n"
     "            MoveGenerator.Captures(position, _scratchMoves);\n"
     "            int k = 0;\n"
     "            bool same = true;\n"
     "            foreach (var m in moves)\n"
     "            {\n"
     "                if (!m.IsCapture && m.Promotion != PieceType.Queen)\n"
     "                    continue;\n"
     "                if (k >= _scratchMoves.Count || _scratchMoves[k] != m)\n"
     "                    same = false;\n"
     "                k++;\n"
     "            }\n"
     "            if (k != _scratchMoves.Count)\n"
     "                same = false;\n"
     "            Hooks.CaptureLists++;\n"
     "            if (!same)\n"
     "                Hooks.CaptureMismatches++;\n"
     "        }\n"
     "        var scores = _moveScores[ply];\n"
     "        if (Hooks.ScoreX2) ScoreMoves(position, moves, _scratchScores, default, ply);\n"
     "        if (!Hooks.NoScore) ScoreMoves(position, moves, scores, default, ply);\n", 1),

    ("        for (int i = 0; i < moves.Count; i++)\n"
     "        {\n"
     "            var move = PickNext(moves, scores, i);\n"
     "            if (!inCheck && !move.IsCapture && move.Promotion != PieceType.Queen)\n"
     "                continue;\n"
     "            var undo = position.MakeMove(move);\n"
     "            if (position.InCheck(us))\n"
     "            {\n"
     "                position.UnmakeMove(move, undo);\n"
     "                continue;\n"
     "            }\n",
     "        for (int i = 0; i < moves.Count; i++)\n"
     "        {\n"
     "            var move = Hooks.PickReplay ? PickR(moves, scores, i) : PickNext(moves, scores, i, true);\n"
     "            if (!inCheck && !move.IsCapture && move.Promotion != PieceType.Queen)\n"
     "            {\n"
     "                if (Hooks.Rec) Hooks.QsSkipped++;\n"
     "                if (Hooks.QsBreak)\n"
     "                    break;\n"
     "                continue;\n"
     "            }\n"
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
     "            }\n", 1),

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

    private static void GenH(Position position, List<Move> moves, bool qs)
    {
        if (Hooks.NoGen)
        {
            Hooks.ReplayGen(moves);
            return;
        }
        MoveGenerator.Pseudo(position, moves);
        if (Hooks.Rec)
            Hooks.RecordGen(moves, qs);
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

table = [
    ("    /// <summary>Per-mille of slots in use, for UCI \"hashfull\".</summary>\n",
     "    internal static ulong PackEntry(TableEntry entry) => Pack(entry.Move, entry.Score, entry.Depth, entry.Bound);\n"
     "\n"
     "    internal static TableEntry UnpackData(ulong data) => Unpack(0, data);\n"
     "\n"
     "    /// <summary>Per-mille of slots in use, for UCI \"hashfull\".</summary>\n", 1),
]

generator = [
    ("        { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight };\n",
     "        { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight };\n"
     "\n"
     "    private static readonly int[] Sides = { -1, 1 };   // the split probe's no-allocation arm\n", 1),
    ("        foreach (int side in new[] { -1, 1 })\n",
     "        foreach (int side in ChessBruteforcer.Core.Engine.Hooks.NoAlloc ? Sides : new[] { -1, 1 })\n", 1),
    ("            && !position.IsAttacked(", "            && !Attacked(position, ", 6),
    ("    private static void CastlingMoves(Position position, int from, Colour us, List<Move> moves)\n",
     "    private static bool Attacked(Position position, int square, Colour by)\n"
     "    {\n"
     "        if (ChessBruteforcer.Core.Engine.Hooks.Rec)\n"
     "            ChessBruteforcer.Core.Engine.Hooks.CastleAttackChecks++;\n"
     "        return position.IsAttacked(square, by);\n"
     "    }\n"
     "\n"
     "    private static void CastlingMoves(Position position, int from, Colour us, List<Move> moves)\n", 1),
    # The captures-only generator for quiescence (the qscap lever's prototype).
    ("    private static bool IsLegal(Position position, Move move, Colour mover)\n",
     """    /// <summary>
    /// The split probe's quiescence generator: only what quiescence searches when not in check (captures with
    /// every promotion piece, en passant, pushes that promote to a queen), in <see cref="Pseudo"/>'s own order.
    /// </summary>
    public static void Captures(Position position, List<Move> moves)
    {
        moves.Clear();
        Colour us = position.SideToMove;
        for (int from = 0; from < 64; from++)
        {
            var piece = position[from];
            if (piece.IsEmpty || piece.Colour != us)
                continue;
            switch (piece.Type)
            {
                case PieceType.Pawn:
                    PawnCaptures(position, from, us, moves);
                    break;
                case PieceType.Knight:
                    StepCaptures(position, from, us, Attacks.Knight[from], moves);
                    break;
                case PieceType.King:
                    StepCaptures(position, from, us, Attacks.King[from], moves);
                    break;
                case PieceType.Bishop:
                    SlideCaptures(position, from, us, Attacks.FirstBishopDirection, Attacks.DirectionCount, moves);
                    break;
                case PieceType.Rook:
                    SlideCaptures(position, from, us, Attacks.FirstRookDirection, Attacks.FirstBishopDirection, moves);
                    break;
                case PieceType.Queen:
                    SlideCaptures(position, from, us, 0, Attacks.DirectionCount, moves);
                    break;
            }
        }
    }

    private static void PawnCaptures(Position position, int from, Colour us, List<Move> moves)
    {
        int forward = us == Colour.White ? 8 : -8;
        int file = from % 8;
        int lastRank = us == Colour.White ? 7 : 0;
        int one = from + forward;
        if (one is >= 0 and < 64 && one / 8 == lastRank && position[one].IsEmpty)
            moves.Add(new Move((byte)from, (byte)one, PieceType.Queen, MoveFlags.None));
        for (int side = -1; side <= 1; side += 2)
        {
            int targetFile = file + side;
            if (targetFile is < 0 or > 7)
                continue;
            int to = one + side;
            if (to is < 0 or >= 64)
                continue;
            var target = position[to];
            if (!target.IsEmpty && target.Colour != us)
                AddPawnMove(from, to, MoveFlags.Capture, lastRank, moves);
            else if (to == position.EnPassantSquare)
                moves.Add(new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture | MoveFlags.EnPassant));
        }
    }

    private static void StepCaptures(Position position, int from, Colour us, int[] targets, List<Move> moves)
    {
        foreach (int to in targets)
        {
            var target = position[to];
            if (!target.IsEmpty && target.Colour != us)
                moves.Add(new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture));
        }
    }

    private static void SlideCaptures(Position position, int from, Colour us, int firstDirection, int endDirection,
                                      List<Move> moves)
    {
        for (int d = firstDirection; d < endDirection; d++)
        {
            foreach (int to in Attacks.Rays[from][d])
            {
                var target = position[to];
                if (target.IsEmpty)
                    continue;
                if (target.Colour != us)
                    moves.Add(new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture));
                break;
            }
        }
    }

    private static bool IsLegal(Position position, Move move, Colour mover)
""", 1),
]

patch(os.path.join("Engine", "Search.cs"), search)
patch(os.path.join("Game", "Position.cs"), position)
patch(os.path.join("Engine", "Evaluation.cs"), evaluation)
patch(os.path.join("Engine", "TranspositionTable.cs"), table)
patch(os.path.join("Game", "MoveGenerator.cs"), generator)
shutil.copyfile(os.path.join(here, "Hooks.cs"), os.path.join(core, "Engine", "Hooks.cs"))
print("copied Hooks.cs")
