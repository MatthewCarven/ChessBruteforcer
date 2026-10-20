"""Unfinished last depths: real clock spent after the last finished depth, by kind of search.
usage: python ponderlog3.py <log> [...]
"""
import os
import sys
import statistics as st

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from ponderlog import parse, budget, q  # noqa: E402


def main():
    searches = parse(sys.argv[1:])
    rows = {"go (not a table move)": [], "ponder hit (from ponderhit)": []}
    clock_used = {"go (not a table move)": [], "ponder hit (from ponderhit)": []}
    for s in searches:
        if not s["end"] or not s["infos"]:
            continue
        if s["kind"] == "go":
            if s["infos"][-1][1] <= 2 and 1000 * (s["end"] - s["start"]) < 50:
                continue
            start = s["start"]
            key = "go (not a table move)"
        elif s["hit"]:
            start = s["hit"]
            key = "ponder hit (from ponderhit)"
        else:
            continue
        used = 1000 * (s["end"] - start)
        last = s["infos"][-1][0]
        waste = 1000 * (s["end"] - max(last, start))
        rows[key].append(waste)
        clock_used[key].append(used)
    print("| search | n | real clock used, total s | of it after the last finished depth, s | share | searches where that tail > 1 s |")
    print("|---|---:|---:|---:|---:|---:|")
    for key in rows:
        w, u = rows[key], clock_used[key]
        print(f"| {key} | {len(w)} | {sum(u)/1000:.0f} | {sum(w)/1000:.0f} | {sum(w)/max(1,sum(u)):.0%} | {sum(x > 1000 for x in w)} ({sum(x > 1000 for x in w)/max(1,len(w)):.0%}) |")


if __name__ == "__main__":
    main()
