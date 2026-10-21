"""P2 in the lab: experiment.py p2's hits, PonderFinish on and off, against the replay (simulate.py) of the same
positions' traces, which start from the same table.  usage: python p2.py <p2.jsonl> [<trace.jsonl>]"""
import json
import os
import statistics as st
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from simulate import finishes, play  # noqa: E402


def load(path):
    return [json.loads(line) for line in open(path, encoding="utf-8") if line.strip()]


def first_per_depth(search):
    """{depth: nodes} from a search's own lines (not P1's line sent again after the hit, which has later nodes)."""
    out = {}
    for i in search["infos"]:
        out.setdefault(i["depth"], i["nodes"])
    return out


def depth_at_hit(search):
    return max([i["depth"] for i in search["infos"] if not i["after_end"]], default=0)


def main():
    rows = load(sys.argv[1])
    traces = {r["n"]: r for r in load(sys.argv[2] if len(sys.argv) > 2 else os.path.join(HERE, "results", "trace.jsonl"))}
    S, H = rows[0]["soft"], rows[0]["hard"]
    arms = sorted({k.split("-", 1)[1] for k in rows[0] if k.startswith("hit")})
    print(f"{len(rows)} positions, soft S {S} ms, hard H {H} ms, one thread; arms {', '.join(arms)}\n")

    same = total = 0
    for r in rows:
        t = first_per_depth(traces[r["n"]]["trace"])
        for key in (k for k in r if k.startswith("hit")):
            for d, nodes in first_per_depth(r[key]).items():
                if d in t:
                    total += 1
                    same += nodes == t[d]
    print(f"depths with the trace's node count (same table, one thread): {same} of {total}\n")
    if arms != ["false", "true"]:
        return

    print("| hit at | arm | depth played (median) | deeper than off | replay says deeper | agrees with the replay | "
          "real clock after the hit: median | mean | most | clock spent, no depth gained | same move as off |")
    print("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
    for frac in (0.75, 1.0, 1.5, 2.0):
        on = [r[f"hit{frac}-true"] for r in rows]
        off = [r[f"hit{frac}-false"] for r in rows]
        deeper = [x["infos"][-1]["depth"] > y["infos"][-1]["depth"] for x, y in zip(on, off)]
        predicted = []
        for r in rows:
            tr = traces[r["n"]]["trace"]
            fin, end = finishes(tr), tr["te_ms"]
            predicted.append(play(fin, frac * S, S, H, "capped", end)[0] > play(fin, frac * S, S, H, "today", end)[0])
        for name, arm in (("off", off), ("on", on)):
            ms = [x["after_end_ms"] for x in arm]
            idle = sum(1 for x in arm if x["after_end_ms"] > 50 and x["infos"][-1]["depth"] <= depth_at_hit(x))
            if name == "on":
                extra = (f"{sum(deeper)}/{len(rows)} | {sum(predicted)}/{len(rows)} | "
                         f"{sum(a == b for a, b in zip(deeper, predicted))}/{len(rows)}")
                moves = f"{sum(x['move'] == y['move'] for x, y in zip(on, off))}/{len(rows)}"
            else:
                extra, moves = " | | ", ""
            print(f"| {frac} S | {name} | {st.median(x['infos'][-1]['depth'] for x in arm):.1f} | {extra} | "
                  f"{st.median(ms):.0f} ms | {st.mean(ms):.0f} ms | {max(ms):.0f} ms | {idle}/{len(rows)} | {moves} |")


if __name__ == "__main__":
    main()
