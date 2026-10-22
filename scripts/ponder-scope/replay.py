"""P6 in the bot's own misses: each logged miss replayed on the old table and on P6's.

The bot's log can't show P6's effect (misscheck.py: the position's difficulty swamps it), so this replays the bot's
real misses in the lab instead, each as the bot met it: our own search on the position before (A), then a ponder on
the predicted reply (P) for the ponder's real length (capped at 15 s), on 8 threads as the bot ponders, then `stop`
and a search of the position the game reached (B) to a fixed depth D. The same with no ponder in between is the
yardstick. One thread for the searches of A and B, so their trees repeat node for node; the ponder's 8 threads make
the table state vary a little, as in the bot.

Arms (each a separate engine process, the bot's network, no endgame tables):
  old     the last build before P6 (e1868ae), Hash 256 (the old 24-bytes-a-slot sizing: ~11.2 M slots)
  p6      the build with P6, Hash 256 (megabytes now: 16.8 M slots, the bot's real setting)
  p6-171  the build with P6, Hash 171 (the old table's slot count, to tell the replacement rule from the size)
Arm order rotates from miss to miss, so drift on the machine falls on all of them alike.

    python replay.py <samples.json> <out.jsonl> <old engine.exe> <p6 engine.exe> <network.nnue> [first] [last]
"""
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from uci import Engine, infos, bestmove  # noqa: E402

PONDER_CAP_S = 15.0


class Low(Engine):
    def __init__(self, exe, options):
        orig = subprocess.Popen

        def popen(*a, **k):
            k["creationflags"] = subprocess.BELOW_NORMAL_PRIORITY_CLASS
            return orig(*a, **k)
        subprocess.Popen = popen
        try:
            super().__init__(exe, options)
        finally:
            subprocess.Popen = orig


def search(e, position, go):
    e.send(position)
    t0 = e.send(go)
    lines = e.wait("bestmove")
    tb, move, reply = bestmove(lines)
    rows = infos(lines)
    return {"move": move, "wall_ms": 1000 * (tb - t0), "depth": rows[-1]["depth"] if rows else 0,
            "nodes": rows[-1]["nodes"] if rows else 0,
            "by_depth": {i["depth"]: [i["nodes"], 1000 * (i["t"] - t0)] for i in rows}}


def fresh(e):
    e.send("ucinewgame")
    e.ready()


def one(e, s, k, d, ponder_s):
    """(no ponder, after the miss) at B, each after our own search at A."""
    fresh(e)
    search(e, s["A"], f"go depth {k}")
    plain = search(e, s["B"], f"go depth {d}")
    fresh(e)
    search(e, s["A"], f"go depth {k}")
    e.send("setoption name Threads value 8")
    e.send(s["P"])
    t0 = e.send(f"go ponder {s['clockP']}")
    time.sleep(ponder_s)
    e.send("stop")
    lines = e.wait("bestmove")
    pondered = infos(lines)
    e.send("setoption name Threads value 1")
    e.ready()
    after = search(e, s["B"], f"go depth {d}")
    after["ponder_depth"] = pondered[-1]["depth"] if pondered else 0
    after["hashfull"] = pondered[-1]["hashfull"] if pondered else 0
    after["ponder_wall_ms"] = 1000 * (time.perf_counter() - t0)
    return plain, after


def main():
    samples_path, out, old_exe, new_exe, net = sys.argv[1:6]
    first = int(sys.argv[6]) if len(sys.argv) > 6 else 0
    last = int(sys.argv[7]) if len(sys.argv) > 7 else 10 ** 6
    for path in (old_exe, new_exe, net):
        if not os.path.exists(path):
            raise SystemExit(f"not found: {path!r}")
    samples = json.load(open(samples_path, encoding="utf-8"))["misses"]
    base = {"Threads": 1, "Move Overhead": 100, "EvalFile": net}
    arms = {"old": Low(old_exe, {**base, "Hash": 256}),
            "p6": Low(new_exe, {**base, "Hash": 256}),
            "p6-171": Low(new_exe, {**base, "Hash": 171})}
    names = list(arms)
    for e in arms.values():   # warm the JIT on every arm
        fresh(e)
        search(e, "position startpos moves e2e4 e7e5 g1f3 b8c6 f1b5 a7a6", "go depth 12")
    with open(out, "a", encoding="utf-8", newline="\n") as f:
        for n in range(first, min(last, len(samples) - 1) + 1):
            s = samples[n]
            cal = arms["old"]
            fresh(cal)
            k = search(cal, s["A"], "go movetime 2500")["depth"]
            fresh(cal)
            d = search(cal, s["B"], "go movetime 3000")["depth"]
            ponder_s = min(PONDER_CAP_S, s["bot_ponder_ms"] / 1000)
            rec = {"n": n, "K": k, "D": d, "bot_ponder_ms": s["bot_ponder_ms"], "ponder_s": ponder_s,
                   "pieces": s["pieces"], "arms": {}}
            order = names[n % 3:] + names[:n % 3]
            for name in order:
                plain, after = one(arms[name], s, k, d, ponder_s)
                rec["arms"][name] = {"plain": plain, "after": after}
            rec["order"] = order
            f.write(json.dumps(rec) + "\n")
            f.flush()
            print(n, k, d, round(ponder_s, 1), {m: round(rec["arms"][m]["after"]["nodes"] / max(1, rec["arms"][m]["plain"]["nodes"]), 2) for m in names}, flush=True)
    for e in arms.values():
        e.quit()


if __name__ == "__main__":
    main()
