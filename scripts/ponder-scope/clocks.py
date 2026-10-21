"""Our clock at the last search of each game (one engine pid a game), by time control.
usage: python clocks.py <log> [...]
"""
import os
import sys
import statistics as st
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from ponderlog import parse  # noqa: E402

searches = parse(sys.argv[1:])
games = defaultdict(list)
for s in searches:
    if s["rem"] is not None:
        games[(s["file"], s["pid"])].append(s)
by_tc = defaultdict(list)
for group in games.values():
    first, last = group[0], group[-1]
    # the base: the larger of our first clock and our last (the first search may come after moves from the book)
    inc = (first["inc"] or 0) // 1000
    base = round(max(s["rem"] for s in group) / 60000)
    plies = len((last["position"] or "").split()) - 3
    by_tc[(base, inc)].append((last["rem"] / 1000, max(s["rem"] for s in group) / 1000, plies // 2))
print("| time control | games | our clock at the last search, median s | most we had, median s | left / most | moves (median) |")
print("|---|---:|---:|---:|---:|---:|")
for tc in sorted(by_tc, key=lambda t: -len(by_tc[t])):
    rows = by_tc[tc]
    if len(rows) < 3:
        continue
    print(f"| {tc[0]}+{tc[1]} | {len(rows)} | {st.median(r[0] for r in rows):.0f} | {st.median(r[1] for r in rows):.0f} | "
          f"{st.median(r[0] / r[1] for r in rows):.0%} | {st.median(r[2] for r in rows):.0f} |")
