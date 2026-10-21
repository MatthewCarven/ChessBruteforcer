"""rescore.py's files, side by side: how often each evaluation disagrees with the learnt move, and by how much.

    python -I scripts/learner/summary.py <arm.csv> [<arm.csv> ...] [--entries 1656]

Per arm: positions, how many best moves differ from the learnt one, the learnt move's loss in that arm's own
centipawns (best - learnt; 0 where they agree), and how many lose 25 / 50 / 100 cp or more. With two arms on the
same positions: the disagreements one arm has and the other doesn't. --entries scales the shares up to the whole
book, for option (c)'s size (re-learn entries the net thinks lose more than X).
"""
import csv, math, statistics, sys

files = [a for a in sys.argv[1:] if a.endswith(".csv")]
ENTRIES = int(sys.argv[sys.argv.index("--entries") + 1]) if "--entries" in sys.argv else 1656
arms = {}
for path in files:
    for r in csv.DictReader(open(path, encoding="utf-8")):
        arms.setdefault(r["arm"], {})[r["key"]] = r


def share(k, n):
    """k of n as a percentage with a 95% Wilson interval."""
    if n == 0:
        return "-"
    p, z = k / n, 1.96
    centre = (p + z * z / (2 * n)) / (1 + z * z / n)
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / (1 + z * z / n)
    return f"{k} of {n} ({100 * p:.0f}% [{100 * (centre - half):.0f}, {100 * (centre + half):.0f}])"


for arm, rows in arms.items():
    n = len(rows)
    differ = [r for r in rows.values() if r["best"] != r["learnt"]]
    losses = sorted(int(r["loss"]) for r in rows.values())
    print(f"{arm}: {n} positions, depth {statistics.median(int(r['depth']) for r in rows.values()):g} (median), "
          f"{statistics.median(float(r['seconds']) for r in rows.values()):.1f} s a position (median)")
    print(f"  best move differs from the learnt one: {share(len(differ), n)}")
    if differ:
        d = sorted(int(r["loss"]) for r in differ)
        print(f"  the learnt move's loss where they differ: median {statistics.median(d):g} cp, "
              f"quartiles {statistics.quantiles(d, n=4) if len(d) > 1 else d}")
    for x in (25, 50, 100):
        k = sum(1 for v in losses if v >= x)
        print(f"  loss >= {x} cp: {share(k, n)}; of the whole book ~{round(ENTRIES * k / n)} entries")
    print(f"  mean loss over all positions: {statistics.mean(losses):.1f} cp")

if len(arms) == 2:
    (a, ra), (b, rb) = arms.items()
    both = sorted(set(ra) & set(rb))
    da = {k for k in both if ra[k]["best"] != ra[k]["learnt"]}
    db = {k for k in both if rb[k]["best"] != rb[k]["learnt"]}
    print(f"\n{len(both)} positions in both: {a} disagrees with the learnt move in {len(da)}, {b} in {len(db)}; "
          f"{a} only {len(da - db)}, {b} only {len(db - da)}, both {len(da & db)}")
    same = sum(1 for k in both if ra[k]["best"] == rb[k]["best"])
    print(f"  {a} and {b} pick the same move in {share(same, len(both))}")
