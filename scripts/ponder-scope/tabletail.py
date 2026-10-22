"""Option (a), the whole line from the table: the build before against the build with the walk, and a deeper look.

  run <old exe> <new exe> <net> <out.jsonl> [--depth 10] [--check 12] [--every 2]
      Our positions from the two games plan.py read (results/plan/<id>.json, their FENs from <id>-sf.jsonl), each
      game in order in one engine (`ucinewgame` first, the table carrying over as in the bot), one thread, Hash 256:
      the old build and the new to --depth, then the new to --check (a deeper look at the same positions).
  summary <out.jsonl>
      Node for node (every depth's score, nodes and hashfull, and bestmove, old against new); the new line starts
      with the old; the lines' lengths; what the walk costs (the line's arrival after the depth's own time, new less
      old); and whether to believe the tail: how far a search two plies deeper agrees with the search's own part
      against the table's part.

Plain Python (scripts/ponder-scope/uci.py).  usage: python scripts/ponder-scope/tabletail.py run|summary ...
"""
import json
import os
import statistics as st
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from uci import Engine, infos  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
GAMES = ["61InZpXn", "DpEHbqlQ"]


def option(name, default, kind=str):
    return kind(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default


def positions(game, every):
    plan = json.load(open(os.path.join(HERE, "results", "plan", f"{game}.json"), encoding="utf-8"))
    fens = {}
    with open(os.path.join(HERE, "results", "plan", f"{game}-sf.jsonl"), encoding="utf-8") as f:
        for line in f:
            if line.strip():
                r = json.loads(line)
                fens[r["ply"]] = r["fen"]
    plies = [r["ply"] for r in plan["rows"] if r["ply"] in fens]
    return [(ply, fens[ply]) for ply in plies[::every]]


def search(engine, fen, depth):
    engine.send(f"position fen {fen}")
    t0 = engine.send(f"go depth {depth}")
    lines = engine.wait("bestmove", timeout=900)
    out = infos(lines)
    for i in out:
        i["late_ms"] = 1000 * (i["t"] - t0) - i["ms"]   # the line's arrival after the depth's own clock: the walk, and the pipe
        del i["t"]
    return out, lines[-1][1]


def run(old, new, net, out_path):
    depth, check, every = option("--depth", 10, int), option("--check", 12, int), option("--every", 2, int)
    options = {"Hash": 256, "Threads": 1, "EvalFile": net}
    with open(out_path, "a", encoding="utf-8", newline="\n") as out:
        for game in GAMES:
            todo = positions(game, every)
            for arm, exe, d in (("old", old, depth), ("new", new, depth), ("check", new, check)):
                engine = Engine(exe, options)
                engine.send("ucinewgame")
                engine.ready()
                start = time.perf_counter()
                for ply, fen in todo:
                    lines, best = search(engine, fen, d)
                    out.write(json.dumps({"game": game, "ply": ply, "arm": arm, "depth": d, "infos": lines,
                                          "bestmove": best}) + "\n")
                    out.flush()
                engine.quit()
                print(f"{game} {arm} depth {d}: {len(todo)} positions in {time.perf_counter() - start:.0f} s", flush=True)


def summary(path):
    rows = [json.loads(line) for line in open(path, encoding="utf-8") if line.strip()]
    by = {(r["game"], r["ply"], r["arm"]): r for r in rows}
    keys = sorted({(r["game"], r["ply"]) for r in rows})
    lines = same = prefix = searches = best_same = 0
    old_len, new_len, late_old, late_new = [], [], [], []
    final_old, final_new = [], []
    for key in keys:
        o, n = by.get(key + ("old",)), by.get(key + ("new",))
        if not o or not n:
            continue
        searches += 1
        best_same += o["bestmove"] == n["bestmove"]
        for a, b in zip(o["infos"], n["infos"]):
            lines += 1
            same += all(a[k] == b[k] for k in ("depth", "score", "nodes", "hashfull", "partial"))
            prefix += b["pv"][:len(a["pv"])] == a["pv"]
            old_len.append(len(a["pv"]))
            new_len.append(len(b["pv"]))
            late_old.append(a["late_ms"])
            late_new.append(b["late_ms"])
        lines += abs(len(o["infos"]) - len(n["infos"]))
        final_old.append((o["infos"][-1]["depth"], len(o["infos"][-1]["pv"])))
        final_new.append((n["infos"][-1]["depth"], len(n["infos"][-1]["pv"])))
    print(f"searches {searches}; info lines {lines}: depth, score, nodes, hashfull the same on {same}; "
          f"the new line starts with the old on {prefix}; bestmove (and ponder) the same on {best_same} of {searches}")

    def share(values, test):
        return f"{sum(1 for v in values if test(v))} ({sum(1 for v in values if test(v)) / len(values):.0%})"

    print("\n| lines | n | PV plies (median) | 1 ply | 1-3 plies | under half the depth |\n|---|---:|---:|---:|---:|---:|")
    for name, final in (("last depth, old build", final_old), ("last depth, new build", final_new)):
        print(f"| {name} | {len(final)} | {st.median(l for _, l in final):.0f} | {share(final, lambda v: v[1] == 1)} | "
              f"{share(final, lambda v: v[1] <= 3)} | {share(final, lambda v: v[1] < v[0] / 2)} |")
    print(f"| every depth, old build | {len(old_len)} | {st.median(old_len):.0f} | {share(old_len, lambda v: v == 1)} | "
          f"{share(old_len, lambda v: v <= 3)} | |")
    print(f"| every depth, new build | {len(new_len)} | {st.median(new_len):.0f} | {share(new_len, lambda v: v == 1)} | "
          f"{share(new_len, lambda v: v <= 3)} | |")
    diffs = [b - a for a, b in zip(late_old, late_new)]
    print(f"\na line's arrival after its depth's clock (ms, mean): old {st.mean(late_old):.3f}, new {st.mean(late_new):.3f}; "
          f"new less old, line by line: mean {st.mean(diffs):.3f}, median {st.median(diffs):.3f} "
          f"(the clock counts whole ms, so read the mean over {len(diffs)} lines)")

    # Believe the tail?  The deeper search's last line against the new build's last line, ply by ply, while they
    # agree: at a ply in the search's own part (before where the old build's line ends) and at one in the table's.
    agree = {"own": [0, 0], "table": [0, 0]}
    for key in keys:
        o, n, c = (by.get(key + (arm,)) for arm in ("old", "new", "check"))
        if not (o and n and c):
            continue
        own = len(o["infos"][-1]["pv"])
        shown, deeper = n["infos"][-1]["pv"], c["infos"][-1]["pv"]
        for i in range(min(len(shown), len(deeper))):
            part = "own" if i < own else "table"
            agree[part][1] += 1
            if shown[i] != deeper[i]:
                break
            agree[part][0] += 1
    print("\n| part of the line shown | plies compared (the deeper line agreeing up to them) | the deeper search agrees |"
          "\n|---|---:|---:|")
    for part, (yes, total) in agree.items():
        print(f"| the search's own | {total} | {yes} ({yes / max(1, total):.0%}) |" if part == "own" else
              f"| the table's tail | {total} | {yes} ({yes / max(1, total):.0%}) |")


if __name__ == "__main__":
    if sys.argv[1] == "run":
        run(*sys.argv[2:6])
    else:
        summary(sys.argv[2])
