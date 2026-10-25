"""Which of the bot's searches ran to the engine's hard time limit, from lichess-bot's INFO log (bot\\logs).

    python bot\\hard-limit.py bot\\logs\\bot-2026-10-26.log [--since 06:21] [--mem memory.csv] [--game q4lxHiCe]

Each "Searching for wtime .. btime .." line (our clock less lichess-bot's move_overhead, 2 s) is paired with the
engine's info block that follows for the same move (its Pv starts with the move number), and the search's wall time
(the Searching line to the info line) is set against Search.TimeBudget's hard limit for that clock:
    soft = (clock - Move Overhead) / 30 + max(0, 3/4 inc - MoveLag)
    hard = min(3 soft, (clock - Move Overhead) / 3 + max(0, inc - MoveLag)), at most (clock - Move Overhead) - 20 ms
The increment comes from the game's PGN in bot\\games. Ponder hits are left out (their time runs from go ponder).
A search counts as "at the hard limit" when its wall time is 90% of it or more.

--mem takes a CSV of time,ram_avail_mb,commit_free_mb,engines (one row a minute) and bands the searches by the RAM
available that minute; the 2026-10-26 run's is bot\\memory\\bot-memory-2026-10-26.csv. Where two games are on the
same move number with the same side to move, their searches can't be told apart and are left out.
"""
import argparse
import csv
import glob
import os
import re
from collections import defaultdict
from datetime import datetime
from statistics import median

HERE = os.path.dirname(os.path.abspath(__file__))
SEARCHING = re.compile(r"Searching for wtime (\d+) btime (\d+) for game (\w+)")
INFO = re.compile(r"INFO (Source|Depth|Nodes|Speed|Pv): (.*)")


def when(line):
    return datetime.strptime(line[:23], "%Y-%m-%d %H:%M:%S,%f")


def num(s):
    m = re.match(r"([\d.]+)([KM]?)", s)
    return float(m.group(1)) * {"": 1, "K": 1e3, "M": 1e6}[m.group(2)] if m else 0.0


def hard_limit(clock_ms, inc_ms, overhead=100, lag=400, moves_to_go=30):
    remaining = max(1, clock_ms - overhead)
    soft = remaining // moves_to_go + max(0, inc_ms * 3 // 4 - lag)
    hard = min(soft * 3, remaining // 3 + max(0, inc_ms - lag))
    return max(5, min(hard, remaining - 20))


def increments(games):
    inc = {}
    for path in glob.glob(os.path.join(games, "*.pgn")):
        gid = os.path.splitext(os.path.basename(path))[0].rsplit(" - ", 1)[-1]
        with open(path, encoding="utf-8", errors="replace") as f:
            m = re.search(r'\[TimeControl "\d+\+(\d+)"\]', f.read(2000))
        if m:
            inc[gid] = int(m.group(1)) * 1000
    return inc


def searches(log, since):
    move, pending, block = None, {}, {}
    with open(log, encoding="utf-8", errors="replace") as f:
        for line in f:
            if line[11:16] < since:
                continue
            m = re.search(r"INFO move: (\d+)", line)
            if m:
                move = int(m.group(1))
                continue
            m = SEARCHING.search(line)
            if m and move is not None:
                for black in (False, True):
                    key = (move, black)
                    clock = int(m.group(2) if black else m.group(1))
                    pending[key] = None if key in pending else (when(line), m.group(3), clock)
                continue
            m = INFO.search(line)
            if not m:
                continue
            k, v = m.group(1), m.group(2).strip()
            if k == "Source":
                block = {"Source": v}
                continue
            block[k] = v
            pm = re.match(r"(\d+)(\.\.\.|\.)", v) if k == "Pv" else None
            if not pm:
                continue
            key = (int(pm.group(1)), pm.group(2) == "...")
            p = pending.pop(key, None)
            pending.pop((key[0], not key[1]), None)
            if p:
                t0, game, clock = p
                yield dict(t=t0, game=game, clock=clock, wall=(when(line) - t0).total_seconds() * 1000,
                           ponder_hit="ponder hit" in block["Source"], mnps=num(block.get("Speed", "0")) / 1e6)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("log")
    ap.add_argument("--since", default="00:00", help="HH:MM on the log's day")
    ap.add_argument("--mem", help="CSV of time,ram_avail_mb,commit_free_mb,engines")
    ap.add_argument("--game", help="list every search of this game")
    ap.add_argument("--lag", type=int, default=400, help="MoveLag (ms)")
    ap.add_argument("--overhead", type=int, default=100, help="Move Overhead (ms)")
    ap.add_argument("--games", default=os.path.join(HERE, "games"), help="folder of the bot's PGNs")
    a = ap.parse_args()

    ram = {}
    if a.mem:
        with open(a.mem, encoding="utf-8-sig") as f:
            ram = {r["time"][:5]: int(r["ram_avail_mb"]) for r in csv.DictReader(f)}
    inc = increments(a.games)
    rows = []
    for s in searches(a.log, a.since):
        if s["game"] not in inc or s["ponder_hit"]:
            continue
        s["hard"] = hard_limit(s["clock"], inc[s["game"]], a.overhead, a.lag)
        s["at_hard"] = s["wall"] >= 0.9 * s["hard"]
        s["ram"] = ram.get(s["t"].strftime("%H:%M"))
        rows.append(s)

    print(f"{len(rows)} searches paired (ponder hits left out); "
          f"{sum(s['at_hard'] for s in rows)} at the hard limit")
    if ram:
        print(f"\n{'RAM available MB':>16} {'searches':>8} {'at hard':>7} {'share':>6} {'median Mnps':>11}")
        for lo, hi in [(0, 1000), (1000, 2000), (2000, 3000), (3000, 10 ** 6)]:
            r = [s for s in rows if s["ram"] is not None and lo <= s["ram"] < hi]
            n = sum(s["at_hard"] for s in r)
            band = f"{lo}-{hi}" if hi < 10 ** 6 else f"{lo}+"
            print(f"{band:>16} {len(r):>8} {n:>7} {n / len(r) if r else 0:>6.1%} "
                  f"{median(s['mnps'] for s in r) if r else 0:>11.1f}")
    by_game = defaultdict(list)
    for s in rows:
        by_game[s["game"]].append(s)
    print("\ngames with a search at the hard limit and under 30 s on the engine's clock:")
    for game, r in by_game.items():
        low = [s for s in r if s["at_hard"] and s["clock"] < 30000]
        if low:
            print(f"  {game}: {len(low)} such searches, engine clock lowest {min(s['clock'] for s in r) / 1000:.1f} s")
    if a.game:
        print(f"\n{a.game}: time, engine clock s, wall s, hard limit s, Mnps, RAM")
        for s in by_game.get(a.game, []):
            print(f"  {s['t']:%H:%M:%S} {s['clock'] / 1000:6.1f} {s['wall'] / 1000:5.1f} {s['hard'] / 1000:5.1f}"
                  f"{' *' if s['at_hard'] else '  '} {s['mnps']:5.1f} {s['ram'] if s['ram'] is not None else ''}")


if __name__ == "__main__":
    main()
