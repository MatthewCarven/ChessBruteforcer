"""Option (a)'s mutation battery: each mutant of the table walk (UciEngine.WithTableTail, TryPeek) applied to this
checkout's source, built, and run against the tests that should catch it.

usage: python scripts/ponder-scope/tabletail-mutate.py [first] [last]      (mutant numbers, 0-based; default all)
The baseline runs every class the change touches; each mutant its own filter.  Restores every file in a finally.
Prints one line a mutant: killed (red) or LIVED.  (The build and test runner are p6-mutate.py's.)
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import importlib.util  # noqa: E402

_spec = importlib.util.spec_from_file_location("p6mutate", os.path.join(os.path.dirname(os.path.abspath(__file__)), "p6-mutate.py"))
p6 = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(p6)

REPO = p6.REPO
TABLE = r"src\ChessBruteforcer.Core\Engine\TranspositionTable.cs"
UCI = r"src\ChessBruteforcer.Core\Engine\UciEngine.cs"
NL = "\r\n"
WALK = "FullyQualifiedName~TableLineTests|FullyQualifiedName~TranspositionTableTests"
PONDER = WALK + "|FullyQualifiedName~PonderTests"
BASELINE = PONDER + ("|FullyQualifiedName~CutDepthTests|FullyQualifiedName~KeptHistoryTests"
                     "|FullyQualifiedName~UciTests|FullyQualifiedName~ThreadTests")

MUTANTS = [
    ("TryPeek refreshes the age, as TryGet does", WALK, [(TABLE,
        "            entry = Unpack(key, data);" + NL + "            return true;",
        "            entry = Unpack(key, data);" + NL
        + "            if (GenerationOf(data) != _generation) Write(at, key, data & ~GenerationMask | (ulong)_generation << GenerationShift);"
        + NL + "            return true;")]),
    ("the walk reads through TryGet", WALK, [(UCI, "table.TryPeek(position.Hash, out var entry)", "table.TryGet(position.Hash, out var entry)")]),
    ("the walk follows upper bounds too", WALK, [(UCI, "(entry.Bound is Bound.Exact or Bound.Lower)", "(entry.Bound is not Bound.None)")]),
    ("the walk takes the table's move without checking it's legal", WALK, [(UCI,
        "position.ParseUciMove(entry.Move.ToUci()) is Move next", "entry.Move is Move next")]),
    ("the tail doesn't stop at a repetition", WALK, [(UCI,
        "            if (!seen.Add(position.Hash) || position.HalfmoveClock >= 100)" + NL + "                break;",
        "            if (position.HalfmoveClock >= 100)" + NL + "                break;")]),
    ("the own line's draw isn't checked", WALK, [(UCI,
        "            if (!seen.Add(position.Hash) || position.HalfmoveClock >= 100)" + NL + "                return line;",
        "            if (false)" + NL + "                return line;")]),
    ("no cap at the depth", WALK, [(UCI, "while (whole.Count < plies", "while (whole.Count < 200")]),
    ("the walk never runs (the search's own line sent)", WALK, [(UCI, "SendInfo(info, TableTail ? WithTableTail(", "SendInfo(info, false ? WithTableTail(")]),
    ("P1's resend walks the table again (may live: a race)", PONDER, [(UCI,
        "SendInfo(last with { Nodes = nodes, Elapsed = elapsed }, _lastLine);",
        "ReportInfo(last with { Nodes = nodes, Elapsed = elapsed });")]),
]


def main():
    first = int(sys.argv[1]) if len(sys.argv) > 1 else 0
    last = int(sys.argv[2]) if len(sys.argv) > 2 else len(MUTANTS) - 1
    originals = {}
    for path in (TABLE, UCI):
        with open(os.path.join(REPO, path), "rb") as f:
            originals[path] = f.read()
    try:
        p6.FILTER = BASELINE
        start = time.time()
        verdict, names, total = p6.build_and_test()
        print(f"baseline: {verdict}, {total} tests, {time.time() - start:.0f} s {names}", flush=True)
        if verdict != "green":
            return 1
        lived = []
        for n in range(first, last + 1):
            title, test_filter, edits = MUTANTS[n]
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
            p6.FILTER = test_filter
            start = time.time()
            verdict, names, count = p6.build_and_test()
            for path in originals:
                with open(os.path.join(REPO, path), "wb") as f:
                    f.write(originals[path])
            if verdict == "green":
                lived.append(title)
            shown = "LIVED" if verdict == "green" else verdict
            print(f"{n:2} {shown:12} {title}  [{count} tests, {time.time() - start:.0f} s] "
                  f"{', '.join(x.split('.')[-1] for x in names[:4])}", flush=True)
        print(f"lived: {len(lived)} of {last - first + 1}: {lived}", flush=True)
    finally:
        for path, data in originals.items():
            with open(os.path.join(REPO, path), "wb") as f:
                f.write(data)
    return 0


if __name__ == "__main__":
    sys.exit(main())
