"""P5 in the lab: does keeping the move-ordering history between searches save nodes, on the bot's own games?

sample:  from the bot's DEBUG logs, runs of <length> searches one game's engine ran one after another (each `go` and
         `go ponder`, as the bot met them: a ponder two plies on from the search before it, and after a miss the
         search of the move really played, beside the ponder's), every position with 10 pieces or more and none
         answered from the endgame tables.  Runs don't overlap; <count> of them, spread evenly over all there are.
bench:   every run searched by every arm, one engine process an arm (all at once: on one thread a search to a fixed
         depth is the same node for node whatever the machine is doing): `ucinewgame`, then each search of the run
         in turn, `position ...` and `go depth <depth>`, the table and (as the arm has it) the history carried from
         one to the next.  An arm is a HistoryKept value (0: cleared at each search, as before P5), or with --old an
         earlier build (no HistoryKept option), which must match arm 0 node for node.  The bot's settings otherwise:
         Hash 256, the bot's network, no endgame tables; one thread.
summary: each arm's nodes against arm 0's, search by search (a run's first search starts from nothing in every arm,
         so it must match): by what came before the search, and by its place in the run.  The geometric mean of the
         ratio has a 95% interval from resampling whole runs (a run's searches aren't independent).

usage: python p5.py sample <out.json> <count> <length> <log>...     (the bot venv's python, for python-chess)
       python p5.py bench <samples.json> <depth> <arms, e.g. 0,25,50,100> <out.jsonl> <engine.exe> [--old <exe>]
           [--first n] [--last n]
       python p5.py summary <out.jsonl>...
Set PONDER_SCOPE_NET to the network (bot\\eval\\h1024-3m.nnue in the main checkout), as for experiment.py.
"""
import json
import math
import os
import random
import statistics as st
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from uci import infos, bestmove  # noqa: E402
from ponderlog import parse, q  # noqa: E402

BASE = "0"


def moves_of(position_line):
    w = position_line.split()
    return w[w.index("moves") + 1:] if "moves" in w else []


def relation(prev, cur):
    """What came just before a search: 'next' (the search before was two plies back: our move and the reply),
    'miss' (the search before pondered the wrong reply: this one is beside it), else 'other'."""
    a, b = moves_of(prev["position"]), moves_of(cur["position"])
    if b[:-2] == a:
        return "next"
    if len(a) == len(b) and a[:-1] == b[:-1] and a != b and prev["kind"] == "ponder":
        return "miss"
    return "other"


def sample(out, count, length, logs):
    import chess
    searches = [s for s in parse(logs) if s["end"] and s["position"] and s["go"]]
    # One engine process a game (lichess-bot): a pid's searches, split where a position doesn't follow from the last.
    games, game_of = [], {}
    for s in searches:
        prev = game_of.get(s["pid"])
        before = moves_of(prev[-1]["position"]) if prev else None
        if prev is None or moves_of(s["position"])[:len(before) - 1] != before[:-1]:
            prev = game_of[s["pid"]] = []
            games.append(prev)
        prev.append(s)

    def usable(s):
        if s["infos"] and s["infos"][-1][1] <= 2:
            return False   # an endgame-table move (or a forced one)
        w = s["position"].split()
        board = chess.Board() if w[1] == "startpos" else chess.Board(" ".join(w[2:w.index("moves") if "moves" in w else len(w)]))
        for m in moves_of(s["position"]):
            board.push_uci(m)
        return len(board.piece_map()) >= 10 and not board.is_game_over()

    runs = []
    for game in games:
        ok = [usable(s) for s in game]
        i = 0
        while i + length <= len(game):
            window = game[i:i + length]
            if all(ok[i:i + length]) and all(relation(a, b) != "other" for a, b in zip(window, window[1:])):
                runs.append(window)
                i += length
            else:
                i += 1
    picked = [runs[int(k * len(runs) / count)] for k in range(count)] if len(runs) > count else runs
    rows = [{"file": os.path.basename(w[0]["file"]), "pid": w[0]["pid"],
             "searches": [{"position": s["position"], "kind": s["kind"],
                           "after": None if j == 0 else relation(w[j - 1], s),
                           "hit": bool(s["hit"]), "bot_depth": s["infos"][-1][1] if s["infos"] else None}
                          for j, s in enumerate(w)]} for w in picked]
    kinds = {}
    for r in rows:
        for s in r["searches"][1:]:
            kinds[s["after"]] = kinds.get(s["after"], 0) + 1
    print(f"{len(games)} games, {len(runs)} runs of {length}; picked {len(rows)}; searches after: {kinds}")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(rows, f, indent=1)


def bench(samples, depth, arms, out, exe, old, first, last):
    from experiment import Low, NET
    rows = json.load(open(samples, encoding="utf-8"))[first:last + 1]
    engines = {}
    for arm in arms:   # (made one at a time: Low patches subprocess.Popen while it starts one)
        options = {"Hash": 256, "Threads": 1, "Move Overhead": 100, "EvalFile": NET}
        if arm != "old":
            options["HistoryKept"] = int(arm)
        engines[arm] = Low(old if arm == "old" else exe, options)
    lock = threading.Lock()
    f = open(out, "a", encoding="utf-8", newline="\n")

    def run(arm):
        e = engines[arm]
        e.send("ucinewgame")   # (a JIT warm-up, off the record; nodes don't depend on it)
        e.ready()
        e.send("position startpos moves e2e4 e7e5 g1f3 b8c6 f1b5 a7a6")
        e.send("go depth 10")
        e.wait("bestmove")
        for k, row in enumerate(rows):
            e.send("ucinewgame")
            e.ready()
            done = []
            for s in row["searches"]:
                e.send(s["position"])
                t0 = e.send(f"go depth {depth}")
                lines = e.wait("bestmove")
                tb, move, _ = bestmove(lines)
                depths = [i for i in infos(lines) if not i["partial"]]
                done.append({"after": s["after"], "nodes": depths[-1]["nodes"], "depth": depths[-1]["depth"],
                             "move": move, "ms": round(1000 * (tb - t0)), "by_depth": [i["nodes"] for i in depths]})
            with lock:
                f.write(json.dumps({"arm": arm, "run": first + k, "depth": depth, "searches": done}) + "\n")
                f.flush()
                print(f"{time.strftime('%H:%M:%S')} arm {arm} run {first + k} done", flush=True)
        e.quit()

    threads = [threading.Thread(target=run, args=(arm,)) for arm in arms]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    f.close()


def geo(ratios):
    return math.exp(st.mean(math.log(r) for r in ratios))


GROUPS = [
    ("every search after a run's first", lambda j, s: j > 0),
    ("two plies on from the last search", lambda j, s: j > 0 and s["after"] == "next"),
    ("after a miss (beside the ponder)", lambda j, s: j > 0 and s["after"] == "miss"),
    ("a run's 2nd search", lambda j, s: j == 1),
    ("3rd-4th", lambda j, s: j in (2, 3)),
    ("5th on", lambda j, s: j >= 4),
]


def summary(paths):
    records = [json.loads(line) for p in paths for line in open(p, encoding="utf-8") if line.strip()]
    for depth in sorted({r["depth"] for r in records}):
        by = {(r["arm"], r["run"]): r["searches"] for r in records if r["depth"] == depth}
        runs = sorted({run for arm, run in by if arm == BASE})
        arms = sorted({arm for arm, _ in by if arm != BASE}, key=lambda a: (a == "old", int(a) if a != "old" else 0))
        base = [s for run in runs for s in by[(BASE, run)]]
        print(f"\n### depth {depth}: {len(runs)} runs, arms {', '.join(arms)} against {BASE} (cleared at each search)\n")
        print(f"arm {BASE}: {len(base)} searches, median {st.median(s['nodes'] for s in base) / 1e6:.2f} M nodes "
              f"and {st.median(s['ms'] for s in base) / 1000:.2f} s a search")
        print("\n| arm | searches | nodes v arm 0: geometric mean (95% interval) | median | p25 | p75 | fewer nodes | "
              "total nodes v arm 0 | other move |")
        print("|---|---:|---|---:|---:|---:|---:|---:|---:|")
        for arm in arms:
            have = [run for run in runs if (arm, run) in by]
            pairs = {run: list(zip(by[(arm, run)], by[(BASE, run)])) for run in have}
            if arm == "old":
                off = sum(a["by_depth"] != b["by_depth"] for run in have for a, b in pairs[run])
                print(f"| old build | {sum(len(pairs[run]) for run in have)} | {off} differ from arm 0 at some depth "
                      "(must be 0) | | | | | | |")
                continue
            first_off = sum(a["by_depth"] != b["by_depth"] for run in have for a, b in pairs[run][:1])
            print(f"| **{arm}% kept** | | | | | | | | |")
            for name, pick in GROUPS:
                chosen = lambda runs_: [(a, b) for run in runs_ for j, (a, b) in enumerate(pairs[run]) if pick(j, a)]
                got = chosen(have)
                if not got:
                    continue
                rs = [a["nodes"] / b["nodes"] for a, b in got]
                rng = random.Random(5)
                boot = []
                for _ in range(2000):
                    again = chosen([rng.choice(have) for _ in have])
                    if again:
                        boot.append(geo([a["nodes"] / b["nodes"] for a, b in again]))
                total = sum(a["nodes"] for a, _ in got) / sum(b["nodes"] for _, b in got)
                other = sum(a["move"] != b["move"] for a, b in got) / len(got)
                print(f"| {name} | {len(rs)} | {geo(rs):.3f} ({q(boot, 0.025):.3f}-{q(boot, 0.975):.3f}) | "
                      f"{st.median(rs):.3f} | {q(rs, 0.25):.3f} | {q(rs, 0.75):.3f} | {sum(r < 1 for r in rs) / len(rs):.0%} | "
                      f"{total:.3f} | {other:.0%} |")
            print(f"| a run's first search | {len(have)} | {first_off} differ from arm 0 (must be 0) | | | | | | |")


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return
    if args[0] == "sample":
        sample(args[1], int(args[2]), int(args[3]), args[4:])
    elif args[0] == "bench":
        rest = args[6:]
        opt = lambda name, default: rest[rest.index(name) + 1] if name in rest else default
        old = opt("--old", None)
        arms = args[3].split(",") + (["old"] if old else [])
        if BASE not in arms:
            arms.insert(0, BASE)
        bench(args[1], int(args[2]), arms, args[4], args[5], old, int(opt("--first", 0)), int(opt("--last", 10 ** 6)))
    elif args[0] == "summary":
        summary(args[1:])
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
