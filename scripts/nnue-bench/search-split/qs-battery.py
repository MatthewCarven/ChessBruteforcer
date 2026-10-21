"""Mutation battery for K(a) (quiescence generates only what it searches; Negamax passes its check status;
PawnMoves without its allocation). Each mutant is one text change in the engine's sources; the tests run
(dotnet test, a filter); red = some test failed or the run timed out. The source is restored after each
mutant, whatever happens, and the build checked clean at the end.

    python battery.py <repo> [first] [last]
"""
import ctypes
import os
import subprocess
import sys
import time

repo = sys.argv[1]
first = int(sys.argv[2]) if len(sys.argv) > 2 else 0
last = int(sys.argv[3]) if len(sys.argv) > 3 else 999
gen = os.path.join("src", "ChessBruteforcer.Core", "Game", "MoveGenerator.cs")
search = os.path.join("src", "ChessBruteforcer.Core", "Engine", "Search.cs")
# Every class that searches or generates (EngineTests.cs holds six of them, none named EngineTests).
FILTER = "|".join("FullyQualifiedName~" + name for name in (
    "MoveGeneratorTests", "SearchTests", "RuleAwareTableTests", "TakeoverTests", "UciTests", "HashTests",
    "EvaluationTests", "ThreadTests", "PonderTests", "NnueTests"))

# (name, file, old, new, expected: "red", or "equivalent" for a change no behaviour can show)
MUTANTS = [
    ("captures: no push to a queen", gen,
     "            else if (one / 8 == lastRank)\n", "            else if (false)\n", "red"),
    ("captures: the push promotes to a knight", gen,
     "                moves.Add(new Move((byte)from, (byte)one, PieceType.Queen));\n",
     "                moves.Add(new Move((byte)from, (byte)one, PieceType.Knight));\n", "red"),
    ("captures: every push, quiet ones too", gen,
     "            if (quiet)\n            {\n                AddPawnMove(",
     "            if (true)\n            {\n                AddPawnMove(", "red"),
    ("pawns: a capture straight ahead too", gen,
     "        for (int side = -1; side <= 1; side += 2)\n", "        for (int side = -1; side <= 1; side += 1)\n", "red"),
    ("pawns: no captures towards the a-file", gen,
     "        for (int side = -1; side <= 1; side += 2)\n", "        for (int side = 1; side <= 1; side += 2)\n", "red"),
    ("pawns: the array back, allocated per pawn", gen,
     "        for (int side = -1; side <= 1; side += 2)\n", "        foreach (int side in new[] { -1, 1 })\n", "red"),
    ("steps: quiet moves only without quiet", gen,
     "            if (target.IsEmpty)\n            {\n                if (quiet)\n                    moves.Add(new Move((byte)from, (byte)to));\n            }\n",
     "            if (target.IsEmpty)\n            {\n                if (!quiet)\n                    moves.Add(new Move((byte)from, (byte)to));\n            }\n", "red"),
    ("slides: quiet moves in captures too", gen,
     "                if (target.IsEmpty)\n                {\n                    if (quiet)\n",
     "                if (target.IsEmpty)\n                {\n                    if (true)\n", "red"),
    ("king: castling in captures too", gen,
     "                    if (quiet)\n                        CastlingMoves(", "                    if (true)\n                        CastlingMoves(", "red"),
    ("captures: own pieces taken", gen,
     "            else if (target.Colour != us)\n            {\n                moves.Add(new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture));\n",
     "            else if (target.Colour == us)\n            {\n                moves.Add(new Move((byte)from, (byte)to, PieceType.None, MoveFlags.Capture));\n", "red"),
    ("quiescence in check: captures only", search,
     "        if (inCheck)\n            MoveGenerator.Pseudo(position, moves);\n",
     "        if (false)\n            MoveGenerator.Pseudo(position, moves);\n", "red"),
    ("quiescence: recursion never looks for check", search,
     "        bool inCheck = knownCheck ?? position.InCheck();\n", "        bool inCheck = knownCheck ?? false;\n", "red"),
    ("hand-off: told it is in check", search,
     "            return Quiescence(position, alpha, beta, ply, inCheck);\n",
     "            return Quiescence(position, alpha, beta, ply, true);\n", "red"),
    ("hand-off: told not in check (it never is)", search,
     "            return Quiescence(position, alpha, beta, ply, inCheck);\n",
     "            return Quiescence(position, alpha, beta, ply, false);\n", "equivalent"),
]


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
    try:
        result = subprocess.run(
            ["dotnet", "test", os.path.join(repo, "tests", "ChessBruteforcer.Tests"), "--disable-build-servers",
             "-p:UseSharedCompilation=false", "--filter", FILTER, "-v", "q", "--nologo"],
            capture_output=True, text=True, timeout=timeout)
        out = result.stdout + result.stderr
        tail = [line for line in out.splitlines() if "Passed!" in line or "Failed!" in line or "error" in line.lower()]
        return result.returncode, " | ".join(tail[-2:]), time.time() - started
    except subprocess.TimeoutExpired:
        return "timeout", "", time.time() - started


results = []
for index, (name, rel, old, new, expected) in enumerate(MUTANTS):
    if index < first or index > last:
        continue
    original = read(rel)
    text = original.decode("utf-8")
    eol = "\r\n" if "\r\n" in text else "\n"
    body = text.replace("\r\n", "\n")
    if body.count(old) != 1:
        sys.exit(f"mutant {index} ({name}): expected one of\n{old}\nfound {body.count(old)}")
    try:
        write(rel, body.replace(old, new).replace("\n", eol).encode("utf-8"))
        code, tail, seconds = run_tests(timeout=300)
    finally:
        write(rel, original)
    verdict = "green" if code == 0 else "red"
    ok = (verdict == "red") == (expected == "red")
    results.append((index, name, verdict, expected, ok))
    print(f"{index:2} {verdict:5} ({'as expected' if ok else 'NOT AS EXPECTED'}, {expected}) {seconds:5.0f} s  {name}  [{code}] {tail}", flush=True)

# The sources back as they were, and the build of them clean (a battery leaves its last mutant's binaries).
code, tail, seconds = run_tests(timeout=600)
print(f"restored: tests exit {code} {tail}", flush=True)
print(f"{sum(1 for r in results if r[4])} of {len(results)} as expected", flush=True)
