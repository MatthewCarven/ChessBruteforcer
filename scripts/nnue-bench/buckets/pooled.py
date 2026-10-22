"""Pooled per-round pairs over several bench.ps1 TSVs, each given as path:first:last.

    python pooled.py <path:first:last> ...
"""
import csv
import math
import statistics
import sys

pairs = {("new", 0): [], ("new", 1): [], ("new-v3x8", 0): [], ("new-v3x8", 1): []}
for spec in sys.argv[1:]:
    path, first, last = spec.rsplit(":", 2)
    rounds = {}
    with open(path, newline="") as f:
        for row in csv.DictReader(f, delimiter="\t"):
            r = int(row["Round"])
            if int(first) <= r <= int(last):
                rounds.setdefault(r, {})[row["Arm"]] = (int(row["Knps"]), int(row["CpuKnps"]))
    for r in rounds.values():
        for (arm, k), d in pairs.items():
            d.append(100 * (r[arm][k] / r["old"][k] - 1))


def sign_p(below, n):
    """Two-sided sign test."""
    tail = sum(math.comb(n, i) for i in range(min(below, n - below) + 1)) / 2 ** n
    return min(1.0, 2 * tail)


for (arm, k), d in pairs.items():
    below = sum(1 for x in d if x < 0)
    trimmed = sorted(d)[len(d) // 10: len(d) - len(d) // 10]
    print(f"{arm:9} by {'clock' if k == 0 else 'CPU':5}: {len(d)} rounds, median {statistics.median(d):+.2f}%, "
          f"mean {statistics.mean(d):+.2f}% +- {statistics.stdev(d) / len(d) ** 0.5:.2f}, "
          f"10%-trimmed mean {statistics.mean(trimmed):+.2f}%, below old in {below} (sign test p {sign_p(below, len(d)):.3f})")
