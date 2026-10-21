"""P4 in the lab: how often a depth the clock cuts now changes the move played, on the bot's own positions.

sample: from the bot's DEBUG logs, every Nth plain `go` search and every Nth ponder hit whose search reached depth
        8 or more (so not a table move): its position line, its go line and, for a hit, how long it pondered.
run:    each sample searched as the bot searched it, from an empty table (ucinewgame): a plain go on its clock; a hit
        as `go ponder` on its clock, `ponderhit` after the time it pondered in the bot, and the answer.  The bot's
        settings: Hash 256, Move Overhead 100, the bot's network, no endgame tables, threads as given (the bot: 8).
        With --referee, a move the cut depth changed is searched again for four times the planned time, to see
        which of the two moves the longer search prefers.
summary: the tables in PONDER.md, "P4".

The engine is a build with P4: when a depth the clock cuts has proved a move better than the last finished depth's,
it sends that depth's line with "lowerbound" and plays the move.  The build before P4 would have played the last
finished depth's move, which is the move of the last line without "lowerbound", so one run gives both answers.  A
search ended with a depth in progress (cut) when its answer came 20 ms or more after its last finished depth.

usage: python p4.py sample <out.json> <every go> <every hit> <log>...
       python p4.py run <samples.json> <first> <last> <out.jsonl> [threads] [--referee]
       python p4.py summary <out.jsonl>...
Set PONDER_SCOPE_ENGINE to the build and PONDER_SCOPE_NET to the network, as for experiment.py.
"""
import json
import os
import statistics as st
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from uci import infos, bestmove, budget  # noqa: E402
from experiment import Low, EXE, NET  # noqa: E402
from ponderlog import parse, q  # noqa: E402

CUT_MS = 20


def sample(out, every_go, every_hit, logs):
    searches = parse(logs)
    rows, n_go, n_hit = [], 0, 0
    for s in searches:
        if not s["end"] or not s["infos"] or s["rem"] is None or max(i[1] for i in s["infos"]) < 8:
            continue
        if s["kind"] == "go":
            n_go += 1
            if n_go % every_go == 0:
                rows.append({"kind": "go", "position": s["position"], "go": s["go"], "rem": s["rem"], "inc": s["inc"]})
        elif s["hit"]:
            n_hit += 1
            if n_hit % every_hit == 0:
                rows.append({"kind": "hit", "position": s["position"], "go": s["go"], "rem": s["rem"], "inc": s["inc"],
                             "ponder_ms": round(1000 * (s["hit"] - s["start"]))})
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(rows, f, indent=0)
    print(f"{len(rows)} samples ({sum(r['kind'] == 'go' for r in rows)} go of {n_go}, "
          f"{sum(r['kind'] == 'hit' for r in rows)} hits of {n_hit})")


def answer(e, row):
    """Search a sample as the bot did: lines, the answer and when (seconds from the go)."""
    e.send("ucinewgame")
    e.ready()
    e.send(row["position"])
    if row["kind"] == "go":
        t0 = e.send(row["go"])
    else:
        t0 = e.send(row["go"])   # (go ponder ...)
        time.sleep(row["ponder_ms"] / 1000)
        e.send("ponderhit")
    lines = e.wait("bestmove")
    tb, move, reply = bestmove(lines)
    return lines, t0, tb, move


def run(samples, first, last, out, threads, referee):
    rows = json.load(open(samples, encoding="utf-8"))
    e = Low(EXE, {"Threads": threads, "Hash": 256, "Move Overhead": 100, "EvalFile": NET, "Ponder": "true"})
    with open(out, "a", encoding="utf-8", newline="\n") as f:
        for k in range(first, min(last, len(rows) - 1) + 1):
            row = rows[k]
            lines, t0, tb, move = answer(e, row)
            lines_all = infos(lines)
            partial = [i for i in lines_all if i.get("partial")]
            finished = [i for i in lines_all if not i.get("partial")]
            last_done = finished[-1] if finished else None
            soft, hard = budget(row["rem"], row["inc"], 100)
            rec = {"k": k, "kind": row["kind"], "soft": soft, "hard": hard, "ponder_ms": row.get("ponder_ms"),
                   "wall_ms": round(1000 * (tb - t0)), "move": move,
                   "finished_depth": last_done["depth"] if last_done else 0,
                   "finished_move": last_done["pv"][0] if last_done else None,
                   "finished_ms": round(1000 * (last_done["t"] - t0)) if last_done else None,
                   "finished_score": last_done["score"] if last_done else None,
                   "partial": [{"depth": i["depth"], "score": i["score"], "pv": i["pv"][:4]} for i in partial]}
            rec["tail_ms"] = rec["wall_ms"] - (rec["finished_ms"] or 0)
            rec["cut"] = rec["tail_ms"] >= CUT_MS
            rec["changed"] = bool(partial) and partial[-1]["pv"][0] != rec["finished_move"]
            if referee and rec["changed"]:
                e.send("ucinewgame")
                e.ready()
                e.send(row["position"])
                ms = 4 * soft
                t1 = e.send(f"go movetime {ms}")
                ref = e.wait("bestmove")
                _, ref_move, _ = bestmove(ref)
                ref_infos = [i for i in infos(ref) if not i.get("partial")]
                rec["referee"] = {"ms": ms, "move": ref_move, "depth": ref_infos[-1]["depth"] if ref_infos else 0,
                                  "pv": ref_infos[-1]["pv"][:4] if ref_infos else []}
            f.write(json.dumps(rec) + "\n")
            f.flush()
            print(k, row["kind"], rec["finished_depth"], rec["wall_ms"], "cut" if rec["cut"] else "",
                  f"changed {rec['finished_move']} -> {move}" if rec["changed"] else "", flush=True)
    e.quit()


def summary(paths):
    rows = [json.loads(line) for p in paths for line in open(p, encoding="utf-8")]
    print("| searches | n | ended with a depth in progress (cut) | the cut depth changed the move | of the cut ones | "
          "real clock after the last finished depth: all cut / where the move changed (median) |")
    print("|---|---:|---:|---:|---:|---:|")
    for name, group in (("plain go", [r for r in rows if r["kind"] == "go"]),
                        ("ponder hit", [r for r in rows if r["kind"] == "hit"]), ("all", rows)):
        if not group:
            continue
        cut = [r for r in group if r["cut"]]
        changed = [r for r in group if r["changed"]]
        tail = [r["tail_ms"] for r in cut]
        tail_changed = [r["tail_ms"] for r in changed]
        print(f"| {name} | {len(group)} | {len(cut)} ({len(cut) / len(group):.0%}) | {len(changed)} "
              f"({len(changed) / len(group):.1%}) | {len(changed) / max(1, len(cut)):.0%} | "
              f"{st.median(tail) if tail else 0:.0f} / {st.median(tail_changed) if tail_changed else 0:.0f} ms |")
    clock = sum(r["wall_ms"] - (r["ponder_ms"] or 0) if r["kind"] == "hit" else r["wall_ms"] for r in rows)
    tails = sum(min(r["tail_ms"], r["wall_ms"] - (r["ponder_ms"] or 0) if r["kind"] == "hit" else r["tail_ms"])
                for r in rows if r["cut"])
    tails_changed = sum(min(r["tail_ms"], r["wall_ms"] - (r["ponder_ms"] or 0) if r["kind"] == "hit" else r["tail_ms"])
                        for r in rows if r["changed"])
    print(f"\nour own clock spent (after the hit, for hits): {clock / 1000:.0f} s; of it on depths cut: "
          f"{tails / 1000:.0f} s ({tails / max(1, clock):.0%}); on cut depths that changed the move: "
          f"{tails_changed / 1000:.0f} s ({tails_changed / max(1, clock):.0%})")
    refs = [r for r in rows if r.get("referee")]
    if refs:
        new = sum(r["referee"]["move"] == r["move"] for r in refs)
        old = sum(r["referee"]["move"] == r["finished_move"] for r in refs)
        print(f"\nreferee (a search 4x the planned time, from an empty table), on the {len(refs)} changed moves: "
              f"the new move {new}, the old move {old}, another {len(refs) - new - old}; "
              f"its depth, median {st.median(r['referee']['depth'] for r in refs):.0f} "
              f"(the cut depth's {st.median(r['partial'][-1]['depth'] for r in refs):.0f})")
    depths = [r["finished_depth"] for r in rows]
    print(f"\ndepth finished: median {st.median(depths):.0f}, p25 {q(depths, 0.25):.0f}, p75 {q(depths, 0.75):.0f}")


def main():
    if sys.argv[1] == "sample":
        sample(sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), sys.argv[5:])
    elif sys.argv[1] == "run":
        args = [a for a in sys.argv[2:] if not a.startswith("--")]
        run(args[0], int(args[1]), int(args[2]), args[3], int(args[4]) if len(args) > 4 else 8, "--referee" in sys.argv)
    else:
        summary(sys.argv[2:])


if __name__ == "__main__":
    main()
