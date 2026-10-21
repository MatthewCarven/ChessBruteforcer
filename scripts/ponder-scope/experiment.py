"""Lab runs for the ponder scoping pass, one thread, Hash 256, the bot's network, no endgame tables.

hits:   per (A, P): own search at A (go depth K), then
        cold  - ucinewgame, P, go <clock>              (no table from the move before)
        go    - own@A, P, go <clock>                   (no pondering: the opponent played r)
        hit f - own@A, P, go ponder <clock>, f*S, ponderhit
misses: per (A, P, B): own@A, then
        coldB - ucinewgame, B, go movetime 3000        (sets D, the depth compared)
        noponder - own@A, B, go depth D
        miss f   - own@A, P, go ponder <clock>, f*S, stop, B, go depth D
trace:  per (A, P): own@A, P, go ponder <clock>, 4*S, stop: every depth's finish time, for simulate.py
p2:     per (A, P), with K (own@A's depth) taken from results/trace.jsonl, so each ponder starts from the table the
        trace's did: for each hit time f*S, f in 0.75 1.0 1.5 2.0 (simulate.py's rows), and PonderFinish true and false,
        own@A, P, go ponder <clock>, f*S, ponderhit.  On one thread every depth has the trace's node count.
        PONDER_SCOPE_ARMS=old for a build without the option (no setoption).
usage: python experiment.py hits|misses|trace|p2 <first> <last> <out.jsonl> [threads_ponder]
"""
import json
import subprocess
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import uci  # noqa: E402
from uci import Engine, infos, bestmove, budget  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
EXE = os.environ.get("PONDER_SCOPE_ENGINE") or os.path.join(REPO, "src", "ChessBruteforcer.Engine", "bin", "Release", "net8.0", "ChessBruteforcer.Engine.exe")
NET = os.environ.get("PONDER_SCOPE_NET") or os.path.join(REPO, "bot", "eval", "h1024-3m.nnue")   # (bot/eval is not in git: in a worktree, point PONDER_SCOPE_NET at the main checkout's)
CLOCK = "wtime 100000 btime 100000 winc 2000 binc 2000"
SOFT, HARD = budget(100000, 2000, 100)


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
    return {"go": go, "t0": t0, "tb": tb, "move": move, "reply": reply,
            "infos": [{**i, "t": i["t"] - t0} for i in infos(lines)], "wall_ms": 1000 * (tb - t0)}


def fresh(e):
    e.send("ucinewgame")
    e.ready()


def own(e, a, k):
    fresh(e)
    return search(e, a, f"go depth {k}")


def ponder(e, position, seconds, end, threads_ponder=None):
    """go ponder for `seconds`, then `end` ('ponderhit' or 'stop'); returns the record (times from go)."""
    if threads_ponder:
        e.send(f"setoption name Threads value {threads_ponder}")
    e.send(position)
    t0 = e.send(f"go ponder {CLOCK}")
    time.sleep(seconds)
    te = e.send(end)
    lines = e.wait("bestmove")
    if threads_ponder:
        e.send("setoption name Threads value 1")
    tb, move, reply = bestmove(lines)
    rows = infos(lines)
    return {"end": end, "after_s": seconds, "t0": t0, "te_ms": 1000 * (te - t0), "tb_ms": 1000 * (tb - t0),
            "after_end_ms": 1000 * (tb - te), "move": move, "reply": reply,
            "infos": [{**i, "t": i["t"] - t0, "after_end": i["t"] > te} for i in rows]}


def main():
    which, first, last, out = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), sys.argv[4]
    threads_ponder = int(sys.argv[5]) if len(sys.argv) > 5 else None
    samples = json.load(open(os.path.join(HERE, "samples.json"), encoding="utf-8"))["hits" if which in ("trace", "p2") else which]
    traced = {}
    if which == "p2":
        for line in open(os.path.join(HERE, "results", "trace.jsonl"), encoding="utf-8"):
            if line.strip():
                r = json.loads(line)
                traced[r["n"]] = r["K"]
    e = Low(EXE, {"Hash": 256, "Threads": 1, "Move Overhead": 100, "EvalFile": NET})
    # JIT warm-up on a position of no interest
    fresh(e)
    search(e, "position startpos moves e2e4 e7e5 g1f3 b8c6 f1b5 a7a6", "go depth 13")
    with open(out, "a", encoding="utf-8", newline="\n") as f:
        for n in range(first, min(last, len(samples) - 1) + 1):
            s = samples[n]
            rec = {"which": which, "n": n, "soft": SOFT, "hard": HARD, "sample": s, "threads_ponder": threads_ponder}
            if which == "p2":
                k = traced[n]
            else:
                fresh(e)
                cal = search(e, s["A"], "go movetime 2500")
                k = cal["infos"][-1]["depth"]
            rec["K"] = k
            rec["own"] = own(e, s["A"], k)
            if which == "p2":
                arms = ["old"] if os.environ.get("PONDER_SCOPE_ARMS") == "old" else ["true", "false"]
                for frac in (0.75, 1.0, 1.5, 2.0):
                    for arm in arms:
                        if arm != "old":
                            e.send(f"setoption name PonderFinish value {arm}")
                        own(e, s["A"], k)
                        rec[f"hit{frac}-{arm}"] = ponder(e, s["P"], frac * SOFT / 1000, "ponderhit")
                if arms != ["old"]:
                    e.send("setoption name PonderFinish value true")
            elif which == "trace":
                # one long ponder (4 x soft), every depth's finish time: replay any hit time under any rule
                own(e, s["A"], k)
                rec["trace"] = ponder(e, s["P"], 4 * SOFT / 1000, "stop")
            elif which == "hits":
                fresh(e)
                rec["cold"] = search(e, s["P"], f"go {CLOCK}")
                own(e, s["A"], k)
                rec["go"] = search(e, s["P"], f"go {CLOCK}")
                for frac in (0.25, 0.75, 1.5):
                    own(e, s["A"], k)
                    rec[f"hit{frac}"] = ponder(e, s["P"], frac * SOFT / 1000, "ponderhit")
            else:
                fresh(e)
                rec["coldB"] = search(e, s["B"], "go movetime 3000")
                d = rec["coldB"]["infos"][-1]["depth"]
                rec["D"] = d
                own(e, s["A"], k)
                rec["noponder"] = search(e, s["B"], f"go depth {d}")
                for frac in (0.75, 1.5, 3.0):
                    own(e, s["A"], k)
                    rec[f"miss{frac}"] = ponder(e, s["P"], frac * SOFT / 1000, "stop", threads_ponder)
                    rec[f"miss{frac}"]["after"] = search(e, s["B"], f"go depth {d}")
            f.write(json.dumps(rec) + "\n")
            f.flush()
            print(which, n, "done", flush=True)
    e.quit()


if __name__ == "__main__":
    main()
