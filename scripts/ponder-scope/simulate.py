"""Replay ponder hits at chosen times on long ponder traces (experiment.py trace), under three rules.

today   - the engine now: clock from "go ponder"; a depth begun past S/2 is dropped at the hit; else it goes on
          (and further depths begun before S/2), each to finish before the hard limit H.
finish  - proposed: as today, but a depth begun past S/2 (or past H) may finish if it does so within S of real time
          after the hit; no further depth.
capped  - finish, and whatever runs on after the hit stops by the hit + S (not at H from "go ponder").
fresh   - ponder time free: the hit starts a whole new budget (S/2 to begin a depth, H to finish), from the hit.
usage: python simulate.py <trace.jsonl>
"""
import json
import statistics as st
import sys


def finishes(trace):
    """[(depth, finish ms from go ponder)] in order; a depth begins when the one before it finishes."""
    return [(i["depth"], 1000 * i["t"]) for i in trace["infos"]]


def play(fin, h, S, H, rule, trace_end):
    """(depth played, real ms after the hit, known) for a hit at h ms."""
    done = [(d, f) for d, f in fin if f <= h]
    depth = done[-1][0] if done else 0
    begun = done[-1][1] if done else 0.0          # the depth in progress began when the last one finished
    pending = [(d, f) for d, f in fin if f > h]
    if rule == "fresh":
        soft_at, hard_at = h + S / 2, h + H
        t = h
        for d, f in pending:
            if f > hard_at:
                return depth, hard_at - h, hard_at <= trace_end
            depth, t = d, f
            if f > soft_at:
                return depth, f - h, True
        # the depth in progress at the trace's end finishes after it: cut at the hard limit if that comes first
        return depth, hard_at - h, hard_at <= trace_end
    if begun > S / 2 or h >= H:
        if rule == "today":
            return depth, 0.0, True
        # finish (and capped): the begun depth, within S after the hit
        # finish: the begun depth, within S after the hit
        if pending and pending[0][1] <= h + S:
            return pending[0][0], pending[0][1] - h, True
        return depth, S, h + S <= trace_end
    # the depth in progress began before S/2: it goes on, as do any begun before S/2, each before H
    # (capped: before the hit + S, too)
    cap = min(H, h + S) if rule == "capped" else H
    t = h
    for d, f in pending:
        if f > cap:
            return depth, cap - h, cap <= trace_end
        depth, t = d, f
        if f > S / 2:
            return depth, f - h, True
    return depth, cap - h, cap <= trace_end


def main():
    rows = [json.loads(line) for line in open(sys.argv[1], encoding="utf-8") if line.strip()]
    S, H = rows[0]["soft"], rows[0]["hard"]
    print(f"{len(rows)} traces, soft S {S} ms, hard H {H} ms, one thread\n")
    print("| hit at | rule | depth played (median) | +1 or more v today | real clock after the hit, median ms | mean ms | clock spent, no depth gained |")
    print("|---|---|---:|---:|---:|---:|---:|")
    for frac in (0.5, 0.75, 1.0, 1.5, 2.0):
        h = frac * S
        base = {}
        for rule in ("today", "finish", "capped", "fresh"):
            if rule == "fresh" and h + H > 4 * S:
                continue
            out = []
            for r in rows:
                fin = finishes(r["trace"])
                end = r["trace"]["te_ms"]
                out.append(play(fin, h, S, H, rule, end))
            if any(not known for _, _, known in out):
                note = " (some past the trace's end)"
            else:
                note = ""
            depths = [d for d, _, _ in out]
            if rule == "today":
                base = depths
            at_hit = [max([d for d, f in finishes(r["trace"]) if f <= h], default=0) for r in rows]
            gain = sum(d > b for d, b in zip(depths, base))
            ms = [m for _, m, _ in out if m is not None]
            idle = sum(1 for (d, m, _), a in zip(out, at_hit) if m and m > 50 and d <= a)
            print(f"| {frac} S | {rule}{note} | {st.median(depths):.1f} | {gain}/{len(rows)} | {st.median(ms):.0f} | {st.mean(ms):.0f} | {idle}/{len(rows)} |")


if __name__ == "__main__":
    main()
