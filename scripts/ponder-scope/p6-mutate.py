"""P6 mutation battery: each mutant applied to this checkout's source, built, and run against the table's tests
(TranspositionTableTests, ThreadTests and the UCI generation test; 43 of them).

usage: python scripts/ponder-scope/p6-mutate.py [first] [last]      (mutant numbers, 0-based; default all)
Restores every file in a finally. Prints one line a mutant: killed (red) or LIVED.
"""
import os
import re
import subprocess
import sys
import time

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TABLE = r"src\ChessBruteforcer.Core\Engine\TranspositionTable.cs"
SEARCH = r"src\ChessBruteforcer.Core\Engine\Search.cs"
FILTER = ("FullyQualifiedName~TranspositionTableTests|FullyQualifiedName~ThreadTests"
          "|FullyQualifiedName~GoAndGoPonderEachStartANewTableGeneration")
NL = "\r\n"

MUTANTS = [
    ("AgePenalty 1", [(TABLE, "public const int AgePenalty = 2;", "public const int AgePenalty = 1;")]),
    ("AgePenalty 3", [(TABLE, "public const int AgePenalty = 2;", "public const int AgePenalty = 3;")]),
    ("AgePenalty 0 (no age)", [(TABLE, "public const int AgePenalty = 2;", "public const int AgePenalty = 0;")]),
    ("NewSearch without the wrap", [(TABLE, "_generation = (_generation + 1) % Generations;", "_generation = _generation + 1;")]),
    ("Clear keeps the generation", [(TABLE, "Array.Clear(_words);" + NL + "        _generation = 0;", "Array.Clear(_words);")]),
    ("TryGet: an empty slot can match", [(TABLE, "if ((check ^ data) != key || BoundOf(data) == Bound.None)", "if ((check ^ data) != key)")]),
    ("TryGet: no refresh of a used entry", [(TABLE, "Write(at, key, data & ~GenerationMask | (ulong)_generation << GenerationShift);", "{ }")]),
    ("TryGet: refresh ORs the generation in", [(TABLE, "Write(at, key, data & ~GenerationMask | (ulong)_generation << GenerationShift);", "Write(at, key, data | (ulong)_generation << GenerationShift);")]),
    ("Lockless check taken out (Check = key)", [
        (TABLE, "_words[at] = key ^ data;", "_words[at] = key;"),
        (TABLE, "if ((check ^ data) != key || BoundOf(data) == Bound.None)", "if (check != key || BoundOf(data) == Bound.None)"),
        (TABLE, "if ((check ^ data) == key && BoundOf(data) != Bound.None)", "if (check == key && BoundOf(data) != Bound.None)"),
    ]),
    ("Store: an empty slot counts as the same position", [(TABLE, "if ((check ^ data) == key && BoundOf(data) != Bound.None)", "if ((check ^ data) == key)")]),
    ("Store: no break at the same position", [(TABLE, "                replace = at;" + NL + "                break;", "                replace = at;")]),
    ("Store: the same position not replaced in place", [(TABLE, "                replace = at;" + NL + "                break;", "                break;")]),
    ("Store: ties go to the last slot (expected equivalent)", [(TABLE, "if (worth < lowest)", "if (worth <= lowest)")]),
    ("Store: the same position's move not kept", [(TABLE, "                if (move == default)" + NL + "                    move = old.Move;" + NL, "")]),
    ("Worth: an empty slot scored as an entry", [(TABLE, "BoundOf(data) == Bound.None ? int.MinValue", "false ? int.MinValue")]),
    ("Worth: age without the wrap", [(TABLE, "AgePenalty * ((_generation - GenerationOf(data)) & (Generations - 1))", "AgePenalty * (_generation - GenerationOf(data))")]),
    ("Worth: age added, not taken off", [(TABLE, ": (sbyte)(byte)(data >> DepthShift) - AgePenalty", ": (sbyte)(byte)(data >> DepthShift) + AgePenalty")]),
    ("BucketStart: buckets half overlap", [(TABLE, "(int)Math.BigMul(key, _buckets, out _) * WordsPerBucket", "(int)Math.BigMul(key, _buckets, out _) * BucketSlots")]),
    ("BucketStart: no alignment offset", [(TABLE, "private int BucketStart(ulong key) => _first + ", "private int BucketStart(ulong key) => ")]),
    ("Allocate: no alignment", [(TABLE, "_first = (int)(-address & 63) / 8;", "_first = 0;")]),
    ("Allocate: the old 24-bytes-a-slot sizing", [(TABLE, "long buckets = bytes / 64;", "long buckets = bytes / 96;")]),
    ("Usage counts every generation", [(TABLE, "if (BoundOf(data) != Bound.None && GenerationOf(data) == _generation)", "if (BoundOf(data) != Bound.None)")]),
    ("Usage counts empty slots", [(TABLE, "if (BoundOf(data) != Bound.None && GenerationOf(data) == _generation)", "if (GenerationOf(data) == _generation)")]),
    ("Usage reads the check words", [(TABLE, "ulong data = _words[_first + 2 * i + 1];", "ulong data = _words[_first + 2 * i];")]),
    ("Pack: no generation", [(TABLE, "        | (ulong)generation << GenerationShift;", "        ;")]),
    ("BoundOf: the generation's bits read as the bound", [(TABLE, "(Bound)(data >> BoundShift & 3)", "(Bound)(data >> BoundShift)")]),
    ("Search.Run: no new generation", [(SEARCH, "        _table.NewSearch();", "")]),
]


def run(args, timeout):
    """Run a command in its own process group; kill the tree on timeout. (exit code, output) or (None, output)."""
    p = subprocess.Popen(args, cwd=REPO, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                         encoding="utf-8", errors="replace", creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
    try:
        out, _ = p.communicate(timeout=timeout)
        return p.returncode, out
    except subprocess.TimeoutExpired:
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(p.pid)], capture_output=True)
        out, _ = p.communicate()
        return None, out


def build_and_test():
    code, out = run(["dotnet", "build", r"tests\ChessBruteforcer.Tests", "-c", "Release", "--nologo", "-v", "q",
                     "--disable-build-servers", "-p:UseSharedCompilation=false"], 300)
    if code != 0:
        errors = [l.strip() for l in out.splitlines() if " error " in l][:2]
        return "build failed", errors, None
    code, out = run(["dotnet", "test", r"tests\ChessBruteforcer.Tests", "-c", "Release", "--no-build", "--nologo",
                     "--filter", FILTER, "--logger", "console;verbosity=normal"], 420)
    total = re.search(r"Total tests: (\d+)", out)
    failed = re.search(r"Failed: (\d+)", out)
    names = sorted(set(re.findall(r"^\s*Failed (ChessBruteforcer\.Tests\.\S+)", out, re.M)))
    if code is None:
        return "timeout", names, total and int(total.group(1))
    return ("red" if code != 0 else "green"), names, total and int(total.group(1))


def main():
    first = int(sys.argv[1]) if len(sys.argv) > 1 else 0
    last = int(sys.argv[2]) if len(sys.argv) > 2 else len(MUTANTS) - 1
    originals = {}
    for path in (TABLE, SEARCH):
        with open(os.path.join(REPO, path), "rb") as f:
            originals[path] = f.read()
    try:
        start = time.time()
        verdict, names, total = build_and_test()
        print(f"baseline: {verdict}, {total} tests, {time.time() - start:.0f} s {names}", flush=True)
        if verdict != "green":
            return 1
        lived = []
        for n in range(first, last + 1):
            title, edits = MUTANTS[n]
            texts = {path: originals[path].decode("utf-8") for path in originals}
            for path, old, new in edits:
                count = texts[path].count(old)
                if count != 1:
                    raise SystemExit(f"mutant {n} ({title}): {old!r} found {count} times in {path}")
                texts[path] = texts[path].replace(old, new)
            for path, text in texts.items():
                if text.encode("utf-8") != originals[path]:
                    with open(os.path.join(REPO, path), "wb") as f:
                        f.write(text.encode("utf-8"))
            start = time.time()
            verdict, names, count = build_and_test()
            for path in originals:
                with open(os.path.join(REPO, path), "wb") as f:
                    f.write(originals[path])
            if verdict == "green":
                lived.append(title)
            shown = "LIVED" if verdict == "green" else verdict
            print(f"{n:2} {shown:12} {title}  [{count} tests, {time.time() - start:.0f} s] {', '.join(n.split('.')[-1] for n in names[:4])}", flush=True)
        print(f"lived: {len(lived)} of {last - first + 1}: {lived}", flush=True)
    finally:
        for path, data in originals.items():
            with open(os.path.join(REPO, path), "wb") as f:
                f.write(data)
    return 0


if __name__ == "__main__":
    sys.exit(main())
