"""After a ponder miss, does the next search lose the head start our own search left it? (P6's check from the log.)

Our own search, a move earlier, searched the position the game reached a couple of plies short of its own depth, so
with its table entries intact the next search's early depths come nearly free. On the old table a long ponder on the
wrong reply pushes those entries out (the 8-thread lab, 2026-10-21: four plies short of the end, 33 ms with no ponder,
143 ms after a 3x-budget miss, 130 ms from scratch). P6's table keeps the deep entries.

Raw node counts can't show it: a long ponder means the opponent thought long, so the position is usually harder and
the next search costs more anyway (the search after a long miss needed 2-8x the nodes of one after a short miss on
the old table, a gap that changes with the build). So the measure is the share of the search's nodes to depth 12
already spent by depth 8: a hard position makes both depths dearer, and the share mostly cancels it. Lost entries
raise the share. On the old table it should grow with the ponder's length; on P6 it shouldn't.

For every miss (`go ponder`, then `stop`, then the next `go` of the same engine, within 30 s): the ponder's length,
and the next search's nodes at depths 8 and 12 (a cut depth's "lowerbound" line, P4, doesn't count). The misses are
split at the median ponder length. The table gives the median share after a short and after a long miss, their ratio
long / short with a bootstrap 95% interval, and Spearman's correlation between ponder length and the share. Compare
one window before P6 with one after it, each on one build.

    python misscheck.py <log> [<log> ...] [--since "2026-10-20 03:28"] [--until "2026-10-20 22:10"] [--label before]
"""
import random
import statistics as st
import sys
from datetime import datetime
import os

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ponderlog import parse  # noqa: E402

DEPTHS = (8, 10, 12)
EARLY, LATE = 8, 12


def option(args, name, default=None):
    if name in args:
        i = args.index(name)
        value = args[i + 1]
        del args[i:i + 2]
        return value
    return default


def at_depth(search, k):
    """(nodes, ms) when the search finished depth k, or None."""
    for t, depth, ms, nodes, score, cut in search["infos"]:
        if depth == k and not cut:
            return nodes, ms
    return None


def misses(searches, since, until):
    rows = []
    by_pid = {}
    for s in searches:
        by_pid.setdefault(s["pid"], []).append(s)
    for group in by_pid.values():
        for a, b in zip(group, group[1:]):
            if a["kind"] != "ponder" or a["hit"] or not a.get("stopped") or b["kind"] != "go" or not b["end"]:
                continue
            if since and a["start"] < since or until and a["start"] > until:
                continue
            if b["start"] - a["stopped"] > 30:
                continue   # not the search straight after the miss
            row = {"ponder_ms": 1000 * (a["stopped"] - a["start"]), "file": a["file"], "start": a["start"]}
            for k in DEPTHS:
                row[k] = at_depth(b, k)
            rows.append(row)
    return rows


def spearman(xs, ys):
    def ranks(v):
        order = sorted(range(len(v)), key=lambda i: v[i])
        r = [0.0] * len(v)
        i = 0
        while i < len(order):
            j = i
            while j + 1 < len(order) and v[order[j + 1]] == v[order[i]]:
                j += 1
            for m in range(i, j + 1):
                r[order[m]] = (i + j) / 2 + 1
            i = j + 1
        return r
    rx, ry = ranks(xs), ranks(ys)
    mx, my = st.mean(rx), st.mean(ry)
    num = sum((a - mx) * (b - my) for a, b in zip(rx, ry))
    den = (sum((a - mx) ** 2 for a in rx) * sum((b - my) ** 2 for b in ry)) ** 0.5
    return num / den if den else float("nan")


def ratio_ci(short, long, n=2000, seed=1):
    rnd = random.Random(seed)
    point = st.median(long) / st.median(short)
    boots = []
    for _ in range(n):
        s = [rnd.choice(short) for _ in short]
        l_ = [rnd.choice(long) for _ in long]
        boots.append(st.median(l_) / st.median(s))
    boots.sort()
    return point, boots[int(0.025 * n)], boots[int(0.975 * n) - 1]


def main():
    args = sys.argv[1:]
    fmt = "%Y-%m-%d %H:%M"
    since = option(args, "--since")
    until = option(args, "--until")
    label = option(args, "--label", "")
    since = datetime.strptime(since, fmt).timestamp() if since else None
    until = datetime.strptime(until, fmt).timestamp() if until else None
    rows = misses(parse(args), since, until)
    if not rows:
        raise SystemExit("no misses in that window")
    have = [r for r in rows if r[EARLY] and r[LATE] and r[LATE][0] > 0]
    if len(have) < 20:
        raise SystemExit(f"only {len(have)} misses reach depth {LATE}")
    cut = st.median(r["ponder_ms"] for r in have)
    for r in have:
        r["share"] = r[EARLY][0] / r[LATE][0]
    short = [r["share"] for r in have if r["ponder_ms"] < cut]
    long = [r["share"] for r in have if r["ponder_ms"] >= cut]
    point, lo, hi = ratio_ci(short, long)
    rho = spearman([r["ponder_ms"] for r in have], [r["share"] for r in have])
    print(f"## {label}\n")
    print(f"| misses reaching depth {LATE} | ponder length, median | share of depth {LATE}'s nodes spent by depth "
          f"{EARLY}: after a short miss | after a long miss | long / short [95%] | Spearman (ponder length, share) | "
          f"median ms to depth {LATE} |")
    print("|---:|---:|---:|---:|---|---:|---:|")
    print(f"| {len(have)} | {cut / 1000:.1f} s | {100 * st.median(short):.1f}% | {100 * st.median(long):.1f}% | "
          f"{point:.2f} [{lo:.2f}, {hi:.2f}] | {rho:+.2f} | {st.median(r[LATE][1] for r in have):.0f} |")


if __name__ == "__main__":
    main()
