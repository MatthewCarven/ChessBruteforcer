"""Mutation battery for bitboards sessions 1 and 2 (NNUE.md "Bitboards, measured"): the sets beside the squares, attacks
by table, the tablebase's count and Material by popcount (mutants 0-40); generation from the sets in the walk's order
(41 on: `python -u battery.py <repo> 41 999 BitboardTests,MoveGeneratorTests`). Each mutant is one text change in the engine's sources; the
tests run (dotnet test, a filter naming classes); red = some test failed or the run timed out. The source is restored
after each mutant, whatever happens, and checked byte for byte at the end, when the tests run once more on it.

    python -u battery.py <repo> [first] [last] [Class1,Class2,...]

The classes default to FILTER's. Name only classes that solve no tables (BitboardTests,MoveGeneratorTests) when the
mutants break attacks or the sets: the solver then takes a king for a capture, finds the same material (kings aren't
counted), solves its own table again, and the test host's stack overflows. Each such crash puts a "System Error" box
on the screen, which SetErrorMode below did not stop (2026-10-20: five boxes; the run was stopped and the rest re-run
on the two classes). A run past its timeout is killed with its whole process tree (taskkill /T).
"""
import ctypes
import os
import re
import subprocess
import sys
import time

repo = sys.argv[1]
first = int(sys.argv[2]) if len(sys.argv) > 2 else 0
last = int(sys.argv[3]) if len(sys.argv) > 3 else 999

pos = os.path.join("src", "ChessBruteforcer.Core", "Game", "Position.cs")
bb = os.path.join("src", "ChessBruteforcer.Core", "Game", "Bitboards.cs")
mat = os.path.join("src", "ChessBruteforcer.Core", "Endgame", "Material.cs")
tb = os.path.join("src", "ChessBruteforcer.Core", "Endgame", "Tablebase.cs")
gen = os.path.join("src", "ChessBruteforcer.Core", "Game", "MoveGenerator.cs")

# Classes, not files (EngineTests.cs holds SearchTests, UciTests and four more): everything that makes moves, asks
# for attacks, solves or probes tables. The count each run reports is printed beside the full suite's.
CLASSES = sys.argv[4].split(",") if len(sys.argv) > 4 else (
    "BitboardTests", "MoveGeneratorTests", "EndgameTests", "SearchTests", "HashTests", "RuleAwareTableTests",
    "TakeoverTests", "TableIndexTests", "CheckerTests", "BoardTests")
FILTER = "|".join("FullyQualifiedName~" + name for name in CLASSES)

# (name, file, old, new, expected: "red", or "equivalent" for a change no test can show)
MUTANTS = [
    # The sets' upkeep in make and unmake: castling's rook, en passant's victim, promotions, captures.
    ("castling rook: not moved in the sets on make", pos,
     "            Put(rookFrom, Piece.Empty);\n            ToggleRook(rook, rookFrom, rookTo);\n",
     "            Put(rookFrom, Piece.Empty);\n", "red"),
    ("castling rook: not moved back on unmake", pos,
     "            Put(rookTo, Piece.Empty);\n            ToggleRook(rook, rookFrom, rookTo);\n",
     "            Put(rookTo, Piece.Empty);\n", "red"),
    ("castling rook: only its from bit", pos,
     "        ulong bits = 1UL << from | 1UL << to;\n", "        ulong bits = 1UL << from;\n", "red"),
    ("castling rook: its colour's set left alone", pos,
     "        _sets[(int)rook.Colour << 3] ^= bits;\n", "", "red"),
    ("en passant: the victim's bit taken at the to square (make)", pos,
     "        ToggleSets(move.From, move.To, mover, placed, captured, capturedSquare);\n",
     "        ToggleSets(move.From, move.To, mover, placed, captured, move.To);\n", "red"),
    ("en passant: the victim's bit put back at the to square (unmake)", pos,
     "        ToggleSets(move.From, move.To, moved, placed, undo.Captured, capturedSquare);\n",
     "        ToggleSets(move.From, move.To, moved, placed, undo.Captured, move.To);\n", "red"),
    ("promotion: the new piece left in the pawns' set", pos,
     "        if (placed.Type == mover.Type)\n", "        if (true)\n", "red"),
    ("promotion: its to bit into the pawns' set", pos,
     "            sets[us | (int)placed.Type] ^= toBit;\n", "            sets[us | (int)mover.Type] ^= toBit;\n", "red"),
    ("capture-promotion: the victim stays in its sets", pos,
     "        if (!captured.IsEmpty)\n        {\n            ulong bit = 1UL << capturedSquare;\n",
     "        if (!captured.IsEmpty && placed.Type == mover.Type)\n        {\n            ulong bit = 1UL << capturedSquare;\n", "red"),
    ("capture: the victim's colour set left alone", pos,
     "            sets[them] ^= bit;\n", "", "red"),
    ("unmake of a capture: the victim not put back", pos,
     "        ToggleSets(move.From, move.To, moved, placed, undo.Captured, capturedSquare);\n",
     "        ToggleSets(move.From, move.To, moved, placed, Piece.Empty, capturedSquare);\n", "red"),
    ("unmake of a promotion: the new piece taken back as itself", pos,
     "        ToggleSets(move.From, move.To, moved, placed, undo.Captured, capturedSquare);\n",
     "        ToggleSets(move.From, move.To, placed, placed, undo.Captured, capturedSquare);\n", "red"),
    ("a quiet move: only its from bit in its type's set", pos,
     "            sets[us | (int)mover.Type] ^= fromBit | toBit;\n", "            sets[us | (int)mover.Type] ^= fromBit;\n", "red"),
    ("a move: the mover's colour set left alone", pos,
     "        sets[us] ^= fromBit | toBit;\n", "", "red"),
    ("null move: touches the sets", pos,
     "        EnPassantSquare = NoSquare;\n        HalfmoveClock++;\n",
     "        EnPassantSquare = NoSquare;\n        HalfmoveClock++;\n        _sets[0] ^= 1UL << 63;\n", "red"),
    ("SetPiece: the old piece's type set not cleared", pos,
     "            _sets[(int)old.Colour << 3 | (int)old.Type] &= ~bit;\n", "", "red"),
    ("SetPiece: the new piece's colour set not set", pos,
     "            _sets[(int)piece.Colour << 3] |= bit;\n", "", "red"),
    ("FromFen: the squares without the sets", pos,
     "            position.Place(square, piece);\n", "            position.Put(square, piece);\n", "red"),

    # IsAttacked by table.
    ("attacks: pawns looked for from the wrong side", pos,
     "        if ((Bitboards.Pawn(Opponent(by), square) & sets[c | (int)PieceType.Pawn]) != 0\n",
     "        if ((Bitboards.Pawn(by, square) & sets[c | (int)PieceType.Pawn]) != 0\n", "red"),
    ("attacks: queens not on the diagonals", pos,
     "        ulong diagonal = (sets[c | (int)PieceType.Bishop] | queens) & Bitboards.BishopLines(square);\n",
     "        ulong diagonal = sets[c | (int)PieceType.Bishop] & Bitboards.BishopLines(square);\n", "red"),
    ("attacks: the king never looked for", pos,
     "            || (Bitboards.King(square) & sets[c | (int)PieceType.King]) != 0)\n", "            || false)\n", "red"),
    ("attacks: only their own pieces block", pos,
     "        ulong occupied = sets[0] | sets[8];\n", "        ulong occupied = sets[c];\n", "red"),
    ("attacks: the rook's lines filtered by the bishop's", pos,
     "        ulong straight = (sets[c | (int)PieceType.Rook] | queens) & Bitboards.RookLines(square);\n",
     "        ulong straight = (sets[c | (int)PieceType.Rook] | queens) & Bitboards.BishopLines(square);\n", "red"),
    ("attacks: rays whatever the path (slower, the same answers)", pos,
     "        if (path == AttackPath.Rays)\n            return IsAttackedByRays(square, by);\n",
     "        if (true)\n            return IsAttackedByRays(square, by);\n", "equivalent"),

    # The tables.
    ("a wrong mask: e4 missing from every rook mask", bb,
     "    private static readonly ulong[] RookMasks = Rays(RookSteps, edge: false);\n",
     "    private static readonly ulong[] RookMasks = Rays(RookSteps, edge: false).Select(mask => mask & ~(1UL << 28)).ToArray();\n", "red"),
    ("a wrong mask: the bishop's with the board's edge in", bb,
     "    private static readonly ulong[] BishopMasks = Rays(BishopSteps, edge: false);\n",
     "    private static readonly ulong[] BishopMasks = Rays(BishopSteps, edge: true);\n", "red"),
    ("pawn sets: the colours swapped", bb,
     "Steps(new[] { (-1, 1), (1, 1) }).Concat(Steps(new[] { (-1, -1), (1, -1) }))",
     "Steps(new[] { (-1, -1), (1, -1) }).Concat(Steps(new[] { (-1, 1), (1, 1) }))", "red"),
    ("knight sets: one jump missing", bb, "(-2, 1), (-1, 2) });", "(-2, 1) });", "red"),
    ("king sets: no diagonal steps", bb, "Steps(RookSteps.Concat(BishopSteps).ToArray())", "Steps(RookSteps)", "red"),
    ("a magic number wrong (a1's rook)", bb,
     "        0x1080004008801020, 0x0840092002C03000,", "        0x0000000000000001, 0x0840092002C03000,", "red"),
    ("magic: the slot one bit short", bb,
     "            squares[square] = new Slider(mask, magics?[square] ?? 0, 64 - bits, size);\n",
     "            squares[square] = new Slider(mask, magics?[square] ?? 0, 65 - bits, size);\n", "red"),
    ("PEXT: a rook's slot halved", bb,
     "            return RookTable[slider.Base + (int)Bmi2.X64.ParallelBitExtract(occupied, slider.Mask)];\n",
     "            return RookTable[slider.Base + ((int)Bmi2.X64.ParallelBitExtract(occupied, slider.Mask) >> 1)];\n", "red"),
    ("magic: a bishop read from the rook's table", bb,
     "            return BishopTable[slider.Base + (int)(((occupied & slider.Mask) * slider.Magic) >> slider.Shift)];\n",
     "            return RookTable[slider.Base + (int)(((occupied & slider.Mask) * slider.Magic) >> slider.Shift)];\n", "red"),
    ("lines: the bishop's filter is the rook's", bb,
     "    public static ulong BishopLines(int square) => BishopLineSets[square];\n",
     "    public static ulong BishopLines(int square) => RookLineSets[square];\n", "red"),

    # The path's choice.
    ("choice: rays not recognised", bb, "        \"rays\" => AttackPath.Rays,\n", "        \"ray\" => AttackPath.Rays,\n", "red"),
    ("slow PEXT: Zen 3 counted slow", bb, "family < 0x19;", "family <= 0x19;", "red"),
    ("slow PEXT: Hygon left out", bb, "vendor is \"AuthenticAMD\" or \"HygonGenuine\" &&", "vendor is \"AuthenticAMD\" &&", "red"),
    ("best: magic where PEXT is fast", bb,
     "Bmi2.X64.IsSupported && !PextIsSlowHere() ? AttackPath.Pext", "Bmi2.X64.IsSupported && PextIsSlowHere() ? AttackPath.Pext", "red"),

    # Material and the tablebase's count by popcount.
    ("material: each kind counted one short", mat,
     "            for (int n = BitOperations.PopCount(position.Pieces(colour, type)); n > 0; n--)\n",
     "            for (int n = BitOperations.PopCount(position.Pieces(colour, type)); n > 1; n--)\n", "red"),
    ("tablebase: one side's pieces counted", tb,
     "        if (System.Numerics.BitOperations.PopCount(position.Occupied) > MaxPieces)\n",
     "        if (System.Numerics.BitOperations.PopCount(position.Pieces(Colour.White)) > MaxPieces)\n", "red"),
    ("tablebase: six pieces let through", tb,
     "        if (System.Numerics.BitOperations.PopCount(position.Occupied) > MaxPieces)\n",
     "        if (System.Numerics.BitOperations.PopCount(position.Occupied) > MaxPieces + 1)\n", "red"),

    # Session 2 (41 on): generation from the sets, in the walk's order. Run them on BitboardTests,MoveGeneratorTests.
    ("generation: the sets whatever the path (the walk's reference gone; the same lists)", gen,
     "        if (path == AttackPath.Rays)\n        {\n            GenerateByRays(position, moves, quiet);\n",
     "        if (false)\n        {\n            GenerateByRays(position, moves, quiet);\n", "equivalent"),
    ("generation: quiet moves in quiescence's captures", gen,
     "        ulong targets = quiet ? ~own : enemy;\n", "        ulong targets = ~own;\n", "red"),
    ("generation: their own pieces captured", gen,
     "        ulong targets = quiet ? ~own : enemy;\n", "        ulong targets = quiet ? ~0UL : enemy;\n", "red"),
    ("pawn: pushed through its own pieces", gen,
     "        if (one is >= 0 and < 64 && (occupied >> one & 1) == 0)\n",
     "        if (one is >= 0 and < 64 && (enemy >> one & 1) == 0)\n", "red"),
    ("pawn: a double push from any rank", gen,
     "                if (from / 8 == (us == Colour.White ? 1 : 6) && (occupied >> two & 1) == 0)\n",
     "                if (two is >= 0 and < 64 && (occupied >> two & 1) == 0)\n", "red"),
    ("pawn: a double push onto a piece", gen,
     "                if (from / 8 == (us == Colour.White ? 1 : 6) && (occupied >> two & 1) == 0)\n",
     "                if (from / 8 == (us == Colour.White ? 1 : 6))\n", "red"),
    ("pawn: quiescence's push to a queen missing", gen,
     "            else if (one / 8 == lastRank)\n            {\n                moves.Add(new Move((byte)from, (byte)one, PieceType.Queen));\n            }\n        }\n\n        // Its captures",
     "            else if (false)\n            {\n                moves.Add(new Move((byte)from, (byte)one, PieceType.Queen));\n            }\n        }\n\n        // Its captures", "red"),
    ("pawn: no en passant", gen,
     "        ulong passing = enPassant >= 0 ? 1UL << enPassant : 0;\n", "        ulong passing = 0;\n", "red"),
    ("pawn: en passant as a plain capture", gen,
     "                moves.Add(new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture | MoveFlags.EnPassant));\n        }\n    }\n\n    /// <summary>A knight's",
     "                moves.Add(new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture));\n        }\n    }\n\n    /// <summary>A knight's", "red"),
    ("pawn: captures from the other colour's set", gen,
     "Bitboards.Pawn(us, from) & (enemy | passing)", "Bitboards.Pawn(Position.Opponent(us), from) & (enemy | passing)", "red"),
    ("knight: stepped through the king's list", gen,
     "StepMoves(from, Attacks.Knight[from], Bitboards.Knight(from) & targets, enemy, moves);",
     "StepMoves(from, Attacks.King[from], Bitboards.Knight(from) & targets, enemy, moves);", "red"),
    ("steps: one square added as a quiet move", gen,
     "        if ((reach & (reach - 1)) == 0)   // one square: no order to keep\n        {\n            AddMove(from, BitOperations.TrailingZeroCount(reach), enemy, moves);\n            return;\n        }\n        foreach",
     "        if ((reach & (reach - 1)) == 0)   // one square: no order to keep\n        {\n            moves.Add(new Move((byte)from, (byte)BitOperations.TrailingZeroCount(reach)));\n            return;\n        }\n        foreach", "red"),
    ("steps: in square order, not Attacks'", gen,
     "        foreach (int to in steps)\n        {\n            if ((reach >> to & 1) != 0)\n                AddMove(from, to, enemy, moves);\n        }\n",
     "        for (; reach != 0; reach &= reach - 1)\n            AddMove(from, BitOperations.TrailingZeroCount(reach), enemy, moves);\n", "red"),
    ("rays: every ray farthest first", gen,
     "            if (RayRunsUp[d])\n", "            if (!RayRunsUp[d])\n", "red"),
    ("rays: the directions backwards", gen,
     "        for (int d = firstDirection; d < endDirection; d++)\n        {\n            ulong ray = reach & RaySets[from * Attacks.DirectionCount + d];\n",
     "        for (int d = endDirection - 1; d >= firstDirection; d--)\n        {\n            ulong ray = reach & RaySets[from * Attacks.DirectionCount + d];\n", "red"),
    ("rays: two squares taken for one", gen,
     "        if ((reach & (reach - 1)) == 0)   // one square: no order to keep\n        {\n            AddMove(from, BitOperations.TrailingZeroCount(reach), enemy, moves);\n            return;\n        }\n        for (int d",
     "        if (BitOperations.PopCount(reach) <= 2)\n        {\n            AddMove(from, BitOperations.TrailingZeroCount(reach), enemy, moves);\n            return;\n        }\n        for (int d", "red"),
    ("queen: only the rook's lines", gen,
     "(Bitboards.Rook(path, from, occupied) | Bitboards.Bishop(path, from, occupied)) & targets",
     "Bitboards.Rook(path, from, occupied) & targets", "red"),
    ("bishop: read along the rook's rays", gen,
     "                    RayMoves(from, Bitboards.Bishop(path, from, occupied) & targets, enemy,\n                             Attacks.FirstBishopDirection, Attacks.DirectionCount, moves);\n",
     "                    RayMoves(from, Bitboards.Bishop(path, from, occupied) & targets, enemy,\n                             Attacks.FirstRookDirection, Attacks.FirstBishopDirection, moves);\n", "red"),
    ("ray sets: each ray one square short", gen,
     "                foreach (int to in Attacks.Rays[square][d])\n                    sets[square * Attacks.DirectionCount + d] |= 1UL << to;\n",
     "                foreach (int to in Attacks.Rays[square][d].SkipLast(1))\n                    sets[square * Attacks.DirectionCount + d] |= 1UL << to;\n", "red"),
    ("castling: missing from the sets' lists", gen,
     "                    StepMoves(from, Attacks.King[from], Bitboards.King(from) & targets, enemy, moves);\n                    if (quiet)\n                        CastlingMoves(position, from, us, moves);\n",
     "                    StepMoves(from, Attacks.King[from], Bitboards.King(from) & targets, enemy, moves);\n", "red"),
    ("moves: every one quiet", gen,
     "        moves.Add((enemy >> to & 1) != 0\n            ? new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture)\n            : new Move((byte)from, (byte)to));\n",
     "        moves.Add(new Move((byte)from, (byte)to));\n", "red"),
]

# No "System Error" box for a crashing test host (SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX |
# SEM_NOOPENFILEERRORBOX); children inherit it.
ctypes.windll.kernel32.SetErrorMode(0x8003)


def free_commit_mb():
    class Status(ctypes.Structure):
        _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                    ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                    ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                    ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                    ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
    status = Status()
    status.dwLength = ctypes.sizeof(Status)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status))
    return status.ullAvailPageFile // (1024 * 1024)


def read(rel):
    with open(os.path.join(repo, rel), "rb") as f:
        return f.read()


def write(rel, data):
    with open(os.path.join(repo, rel), "wb") as f:
        f.write(data)


def run_tests(timeout):
    # A guard: a build beside the bot wants headroom (the notebook's 2 GB floor, plus the build's own).
    while free_commit_mb() < 2600:
        print(f"  waiting for commit ({free_commit_mb()} MB free)", flush=True)
        time.sleep(30)
    started = time.time()
    process = subprocess.Popen(
        ["dotnet", "test", os.path.join(repo, "tests", "ChessBruteforcer.Tests"), "--disable-build-servers",
         "-p:UseSharedCompilation=false", "--filter", FILTER, "-v", "q", "--nologo"],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
    try:
        out, _ = process.communicate(timeout=timeout)
        code = process.returncode
    except subprocess.TimeoutExpired:
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(process.pid)], capture_output=True)
        out, _ = process.communicate()
        code = "timeout"
    summary = [line.strip() for line in out.splitlines() if "Passed!" in line or "Failed!" in line]
    errors = [line.strip() for line in out.splitlines() if "error CS" in line]
    total = re.search(r"Total:\s+(\d+)", summary[-1]) if summary else None
    tail = summary[-1] if summary else (errors[0] if errors else "")
    return code, tail, int(total.group(1)) if total else None, bool(errors), time.time() - started


originals = {rel: read(rel) for rel in {m[1] for m in MUTANTS}}
results = []
try:
    for index, (name, rel, old, new, expected) in enumerate(MUTANTS):
        if index < first or index > last:
            continue
        original = originals[rel]
        text = original.decode("utf-8")
        eol = "\r\n" if "\r\n" in text else "\n"
        body = text.replace("\r\n", "\n")
        if body.count(old) != 1:
            sys.exit(f"mutant {index} ({name}): expected one of\n{old}\nfound {body.count(old)}")
        try:
            write(rel, body.replace(old, new).replace("\n", eol).encode("utf-8"))
            code, tail, total, compile_error, seconds = run_tests(timeout=420)
        finally:
            write(rel, original)
        if compile_error:
            sys.exit(f"mutant {index} ({name}) doesn't compile: {tail}")
        verdict = "green" if code == 0 else "red"
        ok = (verdict == "red") == (expected == "red")
        results.append((index, name, verdict, expected, ok))
        print(f"{index:2} {verdict:5} ({'as expected' if ok else 'NOT AS EXPECTED'}, {expected}) {seconds:4.0f} s  "
              f"{total} tests  {name}  [{code}] {tail}", flush=True)
finally:
    for rel, data in originals.items():
        if read(rel) != data:
            write(rel, data)
            print(f"restored {rel} (it differed)", flush=True)

# The sources back as they were, and the build of them clean (a battery leaves its last mutant's binaries).
code, tail, total, _, seconds = run_tests(timeout=600)
same = all(read(rel) == data for rel, data in originals.items())
print(f"restored: sources {'byte-identical' if same else 'DIFFERENT'}; tests exit {code}, {total} tests: {tail}", flush=True)
print(f"{sum(1 for r in results if r[4])} of {len(results)} as expected", flush=True)
