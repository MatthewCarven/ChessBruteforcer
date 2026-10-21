"""Unfinished last depths: real clock spent after the last finished depth, by kind of search.
Since P4 a cut depth that proved a better move sends its line (with "lowerbound") and its move is played: the last
column counts the clock on such depths.  Neither that line nor P1's line sent again (the same depth again) counts as
a finished depth.
usage: python ponderlog3.py <log> [...]
"""
import os
import sys
import statistics as st

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from ponderlog import parse, budget, q  # noqa: E402


def finished(infos):
    """The lines of finished depths: not a cut depth's (P4), and not the last line sent again (P1)."""
    out = []
    for i in infos:
        if not i[5] and not (out and i[1] == out[-1][1]):
            out.append(i)
    return out


def main():
    searches = parse(sys.argv[1:])
    rows = {"go (not a table move)": [], "ponder hit (from ponderhit)": []}
    clock_used = {"go (not a table move)": [], "ponder hit (from ponderhit)": []}
    played = {"go (not a table move)": [], "ponder hit (from ponderhit)": []}
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
        done = finished(s["infos"])
        last = done[-1][0] if done else s["start"]
        waste = 1000 * (s["end"] - max(last, start))
        rows[key].append(waste)
        clock_used[key].append(used)
        played[key].append(waste if any(i[5] for i in s["infos"]) else 0)
    print("| search | n | real clock used, total s | of it after the last finished depth, s | share | searches where that tail > 1 s "
          "| of the tail, on cut depths whose move was played (P4), s |")
    print("|---|---:|---:|---:|---:|---:|---:|")
    for key in rows:
        w, u, p = rows[key], clock_used[key], played[key]
        print(f"| {key} | {len(w)} | {sum(u)/1000:.0f} | {sum(w)/1000:.0f} | {sum(w)/max(1,sum(u)):.0%} | "
              f"{sum(x > 1000 for x in w)} ({sum(x > 1000 for x in w)/max(1,len(w)):.0%}) | "
              f"{sum(p)/1000:.0f} ({sum(p)/max(1,sum(w)):.0%}; {sum(x > 0 for x in p)} searches) |")


if __name__ == "__main__":
    main()
