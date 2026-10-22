"""Tables from replay.py's JSONL: the search after each of the bot's misses, on the old table and on P6's.

The work ratio is nodes to depth D after the miss over nodes with no ponder, on the same arm and miss. Its geometric
mean is taken over misses, and "P6 / old" pairs the two arms on the same miss, with a bootstrap 95% interval.
    python replaysum.py <replay.jsonl>
"""
import json
import math
import random
import statistics as st
import sys

ARMS = ("old", "p6", "p6-171")


def geo(xs):
    return math.exp(st.mean(math.log(x) for x in xs))


def boot_geo(xs, n=4000, seed=1):
    rnd = random.Random(seed)
    means = sorted(geo([rnd.choice(xs) for _ in xs]) for _ in range(n))
    return means[int(0.025 * n)], means[int(0.975 * n) - 1]


def ratio(rec, arm):
    a = rec["arms"][arm]
    if a["plain"]["depth"] != rec["D"] or a["after"]["depth"] != rec["D"] or a["plain"]["nodes"] <= 0:
        return None
    return a["after"]["nodes"] / a["plain"]["nodes"]


def table(rows, title):
    print(f"### {title} ({len(rows)} misses)\n")
    print("| arm | next search's work after the miss / with no ponder, geometric mean [95%] | median | "
          "misses where the miss cost >10% more |")
    print("|---|---|---:|---:|")
    for arm in ARMS:
        xs = [ratio(r, arm) for r in rows]
        xs = [x for x in xs if x]
        if len(xs) < 5:
            continue
        lo, hi = boot_geo(xs)
        print(f"| {arm} | {geo(xs):.2f} [{lo:.2f}, {hi:.2f}] | {st.median(xs):.2f} | {sum(x > 1.1 for x in xs)}/{len(xs)} |")
    print()
    print("| paired, the same miss | geometric mean [95%] | P6 better on |")
    print("|---|---|---:|")
    for arm in ("p6", "p6-171"):
        pairs = [(ratio(r, arm), ratio(r, "old")) for r in rows]
        pairs = [a / b for a, b in pairs if a and b]
        if len(pairs) < 5:
            continue
        lo, hi = boot_geo(pairs)
        print(f"| {arm} / old | {geo(pairs):.2f} [{lo:.2f}, {hi:.2f}] | {sum(p < 1 for p in pairs)}/{len(pairs)} |")
    print()


def main():
    rows = [json.loads(line) for line in open(sys.argv[1], encoding="utf-8") if line.strip()]
    print(f"{len(rows)} misses replayed; the ponder's real length median {st.median(r['ponder_s'] for r in rows):.1f} s "
          f"(capped at 15 s: {sum(r['bot_ponder_ms'] > 15000 for r in rows)}); D median {st.median(r['D'] for r in rows):.0f}; "
          f"table filled at the stop, median: " + ", ".join(
              f"{arm} {st.median(r['arms'][arm]['after']['hashfull'] for r in rows) / 10:.0f}%" for arm in ARMS) + "\n")
    table(rows, "all")
    rows_sorted = sorted(rows, key=lambda r: r["ponder_s"])
    third = len(rows_sorted) // 3
    for name, part in (("the shortest third of ponders", rows_sorted[:third]),
                       ("the middle third", rows_sorted[third:2 * third]),
                       ("the longest third", rows_sorted[2 * third:])):
        if part:
            table(part, f"{name}: {part[0]['ponder_s']:.1f}-{part[-1]['ponder_s']:.1f} s")
    no_depth = sum(1 for r in rows for arm in ARMS if ratio(r, arm) is None)
    if no_depth:
        print(f"({no_depth} arm-misses left out: a search ended short of depth D)")


if __name__ == "__main__":
    main()
