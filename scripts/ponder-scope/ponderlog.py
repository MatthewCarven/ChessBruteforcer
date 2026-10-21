"""Parse lichess-bot DEBUG logs for the UCI traffic around pondering.

Each search: kind ('go' or 'ponder'), start, its info lines (wall time, depth, engine ms, nodes),
how it ended (bestmove / ponderhit / stop) and when. Prints stats tables.
usage: python ponderlog.py <log> [<log> ...]
"""
import re
import sys
import statistics as st
from datetime import datetime

LINE = re.compile(r"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d,\d{3}) chess\.engine .*?<UciProtocol \(pid=(\d+)\)>: (<<|>>) (.*)$")
# (" lowerbound": since P4, a depth the clock cut, sent because it proved the move played better than the last
# finished depth's; an info tuple's sixth field says so)
INFO = re.compile(r"^info depth (\d+) score (cp -?\d+|mate -?\d+)(?: lowerbound)? nodes (\d+) nps \d+ time (\d+)")


def ts(text):
    return datetime.strptime(text, "%Y-%m-%d %H:%M:%S,%f").timestamp()


def clock_for(go, white):
    def val(key):
        m = re.search(key + r" (\d+)", go)
        return int(m.group(1)) if m else None
    return (val("wtime") if white else val("btime")), (val("winc") if white else val("binc"))


def budget(remaining, inc, overhead=100):
    # Search.TimeBudget, movestogo 30
    remaining = max(1, remaining - overhead)
    soft = remaining // 30 + inc * 3 // 4
    hard = min(soft * 3, remaining // 3 + inc)
    hard = max(5, min(hard, remaining - 20))
    return min(soft, hard), hard


def side_to_move(position_line):
    # count moves after 'moves'; startpos White to move first
    words = position_line.split()
    if words[1] != "startpos":
        fen_side = words[3] if len(words) > 3 else "w"
        start_white = fen_side == "w"
    else:
        start_white = True
    n = len(words) - words.index("moves") - 1 if "moves" in words else 0
    return start_white == (n % 2 == 0)


def parse(paths):
    searches = []
    current = {}      # pid -> search
    position = {}     # pid -> last position line
    for path in paths:
        with open(path, encoding="utf-8", errors="replace") as f:
            for raw in f:
                m = LINE.match(raw.rstrip("\n"))
                if not m:
                    continue
                t, pid, way, text = ts(m.group(1)), m.group(2), m.group(3), m.group(4)
                s = current.get(pid)
                if way == "<<":
                    if text.startswith("position"):
                        position[pid] = text
                    elif text.startswith("go"):
                        white = side_to_move(position.get(pid, "position startpos"))
                        rem, inc = clock_for(text, white)
                        s = {"pid": pid, "kind": "ponder" if text.startswith("go ponder") else "go", "start": t,
                             "go": text, "position": position.get(pid), "infos": [], "end": None, "hit": None,
                             "white": white, "rem": rem, "inc": inc, "file": path}
                        searches.append(s)
                        current[pid] = s
                    elif text == "ponderhit" and s:
                        s["hit"] = t
                        s["infos_before_hit"] = len(s["infos"])
                    elif text == "stop" and s and s["kind"] == "ponder" and s.get("stopped") is None:
                        s["stopped"] = t
                    elif text == "ucinewgame":
                        pass
                else:
                    if s is None:
                        continue
                    mi = INFO.match(text)
                    if mi:
                        s["infos"].append((t, int(mi.group(1)), int(mi.group(4)), int(mi.group(3)), mi.group(2),
                                           " lowerbound " in text))
                    elif text.startswith("bestmove"):
                        s["end"] = t
                        s["bestmove"] = text
                        current[pid] = None
    return searches


def new_infos_after_hit(s):
    """A hit's info lines after ponderhit that report a depth of their own. Since P1 (2026-10-21) a hit that answers
    with no new depth finished sends its last line again, so python-chess has its stats; that one isn't counted.
    Nor is a cut depth's line (P4, "lowerbound"): no depth finished there either (cut_depth_after_hit has it)."""
    k = s["infos_before_hit"]
    after = [i for i in s["infos"][k:] if not i[5]]
    if len(after) == 1 and k and after[0][1] == s["infos"][k - 1][1]:
        return []
    return after


def cut_depth_after_hit(s):
    """Whether a hit's answer came from a depth the clock cut, which had proved the move played (P4)."""
    return any(i[5] for i in s["infos"][s["infos_before_hit"]:])


def q(values, p):
    values = sorted(values)
    if not values:
        return float("nan")
    k = (len(values) - 1) * p
    lo = int(k)
    hi = min(lo + 1, len(values) - 1)
    return values[lo] + (values[hi] - values[lo]) * (k - lo)


def row(name, values, fmt="{:.0f}"):
    if not values:
        print(f"| {name} | 0 | | | | |")
        return
    print(f"| {name} | {len(values)} | " + " | ".join(fmt.format(x) for x in
          (q(values, 0.25), st.median(values), q(values, 0.75), st.mean(values))) + " |")


def main():
    searches = parse(sys.argv[1:])
    goes = [s for s in searches if s["kind"] == "go" and s["end"]]
    ponders = [s for s in searches if s["kind"] == "ponder" and s["end"]]
    hits = [s for s in ponders if s["hit"]]
    misses = [s for s in ponders if not s["hit"] and s.get("stopped")]
    print(f"searches: go {len(goes)}, go ponder {len(ponders)} (hits {len(hits)}, stopped {len(misses)}, other {len(ponders)-len(hits)-len(misses)})")
    print(f"hit rate: {len(hits) / max(1, len(hits) + len(misses)):.1%}")

    print("\n| quantity (ms or plies) | n | p25 | median | p75 | mean |\n|---|---:|---:|---:|---:|---:|")
    # hits
    pondered = [1000 * (s["hit"] - s["start"]) for s in hits]
    after = [1000 * (s["end"] - s["hit"]) for s in hits]
    row("hit: pondered before ponderhit (ms)", pondered)
    row("hit: ponderhit to bestmove (ms)", after)
    soft = [budget(s["rem"], s["inc"])[0] for s in hits if s["rem"]]
    row("hit: planned soft budget (ms)", soft)
    frac = [1000 * (s["hit"] - s["start"]) / budget(s["rem"], s["inc"])[0] for s in hits if s["rem"]]
    row("hit: pondered / soft budget", frac, "{:.2f}")
    no_info_after = [s for s in hits if len(s["infos"]) == s["infos_before_hit"]]
    print(f"\nhits with no info after ponderhit: {len(no_info_after)} of {len(hits)} ({len(no_info_after)/max(1,len(hits)):.1%})")
    cut = [s for s in hits if cut_depth_after_hit(s)]
    sent_again = [s for s in hits if len(s["infos"]) > s["infos_before_hit"] and not new_infos_after_hit(s)
                  and not cut_depth_after_hit(s)]
    print(f"hits whose only info after ponderhit is the last one sent again (P1): {len(sent_again)} of {len(hits)} "
          f"({len(sent_again)/max(1,len(hits)):.1%})")
    print(f"hits answered by a cut depth's move, proved better there (P4): {len(cut)} of {len(hits)} "
          f"({len(cut)/max(1,len(hits)):.1%})")
    d_before = [s["infos"][s["infos_before_hit"] - 1][1] for s in hits if s["infos_before_hit"] > 0]
    d_final = [s["infos"][-1][1] for s in hits if s["infos"]]
    print("\n| quantity (ms or plies) | n | p25 | median | p75 | mean |\n|---|---:|---:|---:|---:|---:|")
    row("hit: depth done at ponderhit", d_before)
    row("hit: depth played", d_final)
    # instant answers
    instant = [a for a in after if a < 20]
    print(f"hits answered within 20 ms of ponderhit: {len(instant)} of {len(after)}")

    # misses
    print("\n| quantity (ms or plies) | n | p25 | median | p75 | mean |\n|---|---:|---:|---:|---:|---:|")
    row("miss: pondered before stop (ms)", [1000 * (s["stopped"] - s["start"]) for s in misses])
    row("miss: depth done at stop", [s["infos"][-1][1] for s in misses if s["infos"]])
    row("miss: stop to bestmove (ms)", [1000 * (s["end"] - s["stopped"]) for s in misses])

    # the go that follows a miss v a go that follows no ponder (our own move after book etc.)
    follow = {}
    for i, s in enumerate(searches):
        if s["kind"] == "ponder" and not s["hit"] and s.get("stopped"):
            for t in searches[i + 1:i + 3]:
                if t["pid"] == s["pid"] and t["kind"] == "go":
                    follow[id(t)] = s
                    break
    after_miss = [s for s in goes if id(s) in follow]
    other_go = [s for s in goes if id(s) not in follow]
    row("go after a miss: time used (ms)", [1000 * (s["end"] - s["start"]) for s in after_miss])
    row("go after a miss: depth played", [s["infos"][-1][1] for s in after_miss if s["infos"]])
    row("other go: time used (ms)", [1000 * (s["end"] - s["start"]) for s in other_go])
    row("other go: depth played", [s["infos"][-1][1] for s in other_go if s["infos"]])
    row("hit (whole, from go ponder): depth played", d_final)

    # time to reach depth 8 and 10, go after miss v other go v ponder search (from its start)
    print("\n| engine ms to finish depth k | search | n | median | p75 |\n|---|---|---:|---:|---:|")
    for k in (6, 8, 10, 12):
        for name, group in (("go after a miss", after_miss), ("other go", other_go), ("go ponder (from its start)", ponders)):
            times = [next((i[2] for i in s["infos"] if i[1] == k), None) for s in group]
            times = [x for x in times if x is not None]
            if times:
                print(f"| {k} | {name} | {len(times)} | {st.median(times):.0f} | {q(times, 0.75):.0f} |")


if __name__ == "__main__":
    main()
