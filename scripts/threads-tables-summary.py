# Summarise bench-threads --tables runs (their --tsv files): each arm against the one-thread arm of the
# same run, and the two builds against each other.  Usage:
#   python -I threads-tables-summary.py lock=<a.tsv>,<b.tsv> free=<c.tsv>,<d.tsv>
import sys, csv, statistics
from collections import defaultdict


def load(path):
    with open(path, newline="") as f:
        return list(csv.DictReader(f, delimiter="\t"))


builds = {}
for arg in sys.argv[1:]:
    name, files = arg.split("=", 1)
    builds[name] = [load(p) for p in files.split(",")]

threads_of = lambda arm: 1 if arm == "1" else int(arm[1:])
arms = []
for runs in builds.values():
    for rows in runs:
        for r in rows:
            if r["arm"] not in arms:
                arms.append(r["arm"])

print("| build | arm | threads | time s (all runs) | speedup (total) | median speedup | slower than 1 thread | "
      "nodes v 1 | nodes a second v 1 | blocks a search | decompressing, of thread time | waiting, of thread time |")
print("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
totals = defaultdict(float)   # (build, arm) -> seconds
for name, runs in builds.items():
    for arm in arms:
        ms = nodes = base_ms = base_nodes = blocks = block_ms = wait_ms = 0.0
        ratios, slower, searches = [], 0, 0
        for rows in runs:
            base = {(r["round"], r["position"]): r for r in rows if r["arm"] == "1"}
            for r in rows:
                if r["arm"] != arm:
                    continue
                b = base[(r["round"], r["position"])]
                searches += 1
                ms += float(r["ms"]); nodes += int(r["nodes"])
                base_ms += float(b["ms"]); base_nodes += int(b["nodes"])
                blocks += int(r["blocks"]); block_ms += float(r["block_ms"]); wait_ms += float(r["wait_ms"])
                if float(b["ms"]) >= 100:   # (searches the tables settle in a millisecond say nothing)
                    ratios.append(float(b["ms"]) / float(r["ms"]))
                    slower += float(r["ms"]) > float(b["ms"]) * 1.0
        if searches == 0:
            continue
        totals[(name, arm)] = ms / 1000
        thread_ms = ms * threads_of(arm)
        print(f"| {name} | {arm} | {threads_of(arm)} | {ms / 1000:.1f} | {base_ms / ms:.2f} | {statistics.median(ratios):.2f} | "
              f"{slower} of {len(ratios)} | {nodes / base_nodes:.2f} | {(nodes / ms) / (base_nodes / base_ms):.2f} | "
              f"{blocks / searches:,.0f} | {block_ms / thread_ms:.1%} | {wait_ms / thread_ms:.1%} |")

names = list(builds)
if len(names) == 2:
    a, b = names
    print()
    print(f"| arm | {a} time s | {b} time s | {a} / {b} |")
    print("|---|---:|---:|---:|")
    for arm in arms:
        if (a, arm) in totals and (b, arm) in totals:
            print(f"| {arm} | {totals[(a, arm)]:.1f} | {totals[(b, arm)]:.1f} | {totals[(a, arm)] / totals[(b, arm)]:.2f} |")
