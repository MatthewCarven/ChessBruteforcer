"""More from the bot's log: what each ponder hit did after ponderhit, and the clock at games' ends.
usage: python ponderlog2.py <log> [<log> ...]
"""
import os
import sys
import statistics as st
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ponderlog import parse, budget, q, row  # noqa: E402


def main():
    searches = parse(sys.argv[1:])
    ponders = [s for s in searches if s["kind"] == "ponder" and s["end"]]
    hits = [s for s in ponders if s["hit"]]
    at_once, finished, wasted = [], [], []
    for s in hits:
        after = 1000 * (s["end"] - s["hit"])
        n_after = len(s["infos"]) - s["infos_before_hit"]
        if n_after > 0:
            finished.append(s)
        elif after < 20:
            at_once.append(s)
        else:
            wasted.append(s)
    n = len(hits)
    print(f"ponder hits: {n}")
    print("\n| after ponderhit | hits | share | median ms after hit | median pondered / soft |")
    print("|---|---:|---:|---:|---:|")
    for name, group in (("stopped at once (depth begun past half the budget)", at_once),
                        ("went on and finished a depth (info sent)", finished),
                        ("went on, no depth finished (hard limit or ended)", wasted)):
        ms = [1000 * (s["end"] - s["hit"]) for s in group]
        fr = [1000 * (s["hit"] - s["start"]) / budget(s["rem"], s["inc"])[0] for s in group if s["rem"]]
        print(f"| {name} | {len(group)} | {len(group)/n:.1%} | {st.median(ms) if ms else float('nan'):.0f} | {st.median(fr) if fr else float('nan'):.2f} |")

    # stopped at once: how long the dropped depth had run, and how that compares to the last finished depth
    print("\n| stopped at once | n | p25 | median | p75 | mean |\n|---|---:|---:|---:|---:|---:|")
    dropped = [1000 * (s["hit"] - s["infos"][s["infos_before_hit"] - 1][0]) for s in at_once if s["infos_before_hit"]]
    row("ms the dropped depth had run at the hit", dropped)
    # time the last finished depth took (from the one before it)
    last_took = []
    for s in at_once:
        k = s["infos_before_hit"]
        if k >= 2:
            last_took.append(1000 * (s["infos"][k - 1][0] - s["infos"][k - 2][0]))
    row("ms the last finished depth took", last_took)
    ratio = [d / l for d, l in zip(dropped, last_took) if l > 0]
    row("dropped depth's run / last depth's time", ratio, "{:.2f}")
    # was the hit's own real budget left unspent? soft budget minus 0 used
    soft = [budget(s["rem"], s["inc"])[0] for s in at_once if s["rem"]]
    row("their soft budget (ms), all left on the clock", soft)

    # wasted ones: time after hit spent on a depth that never finished
    print("\n| went on, no depth finished | n | p25 | median | p75 | mean |\n|---|---:|---:|---:|---:|---:|")
    row("ms after ponderhit (spent on an unfinished depth)", [1000 * (s["end"] - s["hit"]) for s in wasted])
    hard = [budget(s["rem"], s["inc"])[1] for s in wasted if s["rem"]]
    row("their hard limit (ms from go ponder)", hard)
    row("pondered before hit (ms)", [1000 * (s["hit"] - s["start"]) for s in wasted])

    # finished ones: depth continued?
    cont = sum(1 for s in finished if s["infos_before_hit"] and s["infos"][s["infos_before_hit"]][1] == s["infos"][s["infos_before_hit"] - 1][1] + 1)
    restart = sum(1 for s in finished if s["infos"][s["infos_before_hit"]][1] == 1)
    print(f"\nfinished after hit: first depth reported after the hit = the depth before it + 1 in {cont} of {len(finished)}; "
          f"depth 1 (a restart) in {restart}")
    print("\n| went on and finished | n | p25 | median | p75 | mean |\n|---|---:|---:|---:|---:|---:|")
    row("depths finished after the hit", [len(s["infos"]) - s["infos_before_hit"] for s in finished])
    row("ms after ponderhit", [1000 * (s["end"] - s["hit"]) for s in finished])

    # clock at the end of each game (per engine pid): our clock in the last go, and the clock we started with
    games = defaultdict(list)
    for s in searches:
        if s["rem"] is not None:
            games[(s["file"], s["pid"])].append(s)
    print("\n| time control (base+inc s) | games | our clock at the last search, median s | start s | moves (median) |")
    print("|---|---:|---:|---:|---:|")
    by_tc = defaultdict(list)
    for key, group in games.items():
        first = group[0]
        last = group[-1]
        inc = first["inc"] // 1000 if first["inc"] is not None else 0
        start = first["rem"]
        tc = (round(start / 60000) * 60, inc)
        by_tc[tc].append((last["rem"] / 1000, start / 1000, len(group)))
    for tc in sorted(by_tc, key=lambda t: -len(by_tc[t])):
        rows = by_tc[tc]
        if len(rows) < 3:
            continue
        print(f"| ~{tc[0]//60}+{tc[1]} | {len(rows)} | {st.median(r[0] for r in rows):.1f} | {st.median(r[1] for r in rows):.0f} | {st.median(r[2] for r in rows):.0f} |")


if __name__ == "__main__":
    main()
