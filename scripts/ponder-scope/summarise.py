"""Tables from experiment.py's JSONL. usage: python summarise.py <hits.jsonl> <misses.jsonl> [more misses...]"""
import json
import statistics as st
import sys


def load(path):
    try:
        return [json.loads(line) for line in open(path, encoding="utf-8") if line.strip()]
    except FileNotFoundError:
        return []


def med(xs):
    xs = [x for x in xs if x is not None]
    return st.median(xs) if xs else float("nan")


def geo(xs):
    xs = [x for x in xs if x and x > 0]
    if not xs:
        return float("nan")
    p = 1.0
    for x in xs:
        p *= x
    return p ** (1 / len(xs))


def at_depth(search, k, key="nodes"):
    for i in search["infos"]:
        if i["depth"] == k:
            return i[key] if key != "t" else 1000 * i["t"]
    return None


def hits(rows):
    if not rows:
        return
    print(f"## hits: {len(rows)} positions; clock 100 s + 2 s, soft {rows[0]['soft']} ms, hard {rows[0]['hard']} ms; one thread\n")
    print("| arm | pondered ms | real clock used ms (median) | depth played (median) | nodes at the move (median) | same move as go | same nodes as go |")
    print("|---|---:|---:|---:|---:|---:|---:|")
    go = [r["go"] for r in rows]
    cold = [r["cold"] for r in rows]
    print(f"| cold go (no table from the move before) | 0 | {med(c['wall_ms'] for c in cold):.0f} | {med(c['infos'][-1]['depth'] for c in cold):.0f} | {med(c['infos'][-1]['nodes'] for c in cold)/1e6:.1f} M | {sum(c['move']==g['move'] for c,g in zip(cold,go))}/{len(rows)} | |")
    print(f"| go (after our own search, no pondering) | 0 | {med(g['wall_ms'] for g in go):.0f} | {med(g['infos'][-1]['depth'] for g in go):.0f} | {med(g['infos'][-1]['nodes'] for g in go)/1e6:.1f} M | | |")
    for f in ("0.25", "0.75", "1.5"):
        h = [r[f"hit{f}"] for r in rows]
        same_move = sum(x["move"] == g["move"] for x, g in zip(h, go))
        same_nodes = sum(x["infos"][-1]["nodes"] == g["infos"][-1]["nodes"] for x, g in zip(h, go))
        print(f"| ponder {f} x soft, then ponderhit | {med(x['te_ms'] for x in h):.0f} | {med(x['after_end_ms'] for x in h):.0f} | "
              f"{med(x['infos'][-1]['depth'] for x in h):.0f} | {med(x['infos'][-1]['nodes'] for x in h)/1e6:.1f} M | {same_move}/{len(rows)} | {same_nodes}/{len(rows)} |")
    print()
    print("| arm | depth played v go: deeper / same / shallower | info sent after the hit | median ms of a dropped depth (thrown away) |")
    print("|---|---|---:|---:|")
    for f in ("0.25", "0.75", "1.5"):
        h = [r[f"hit{f}"] for r in rows]
        dd = [x["infos"][-1]["depth"] - g["infos"][-1]["depth"] for x, g in zip(h, go)]
        info_after = sum(any(i["after_end"] for i in x["infos"]) for x in h)
        dropped = []
        for x in h:
            last = x["infos"][-1]["t"] * 1000
            dropped.append(x["tb_ms"] - last)
        print(f"| ponder {f} x soft | {sum(d>0 for d in dd)} / {sum(d==0 for d in dd)} / {sum(d<0 for d in dd)} | {info_after}/{len(rows)} | {med(dropped):.0f} |")
    print()
    # Q3: the table from our own move
    print("## the table from the move before (go v cold, on the pondered position P)\n")
    print("| depth | cold: ms (median) | go: ms (median) | nodes go / cold (geometric mean) | n |")
    print("|---:|---:|---:|---:|---:|")
    for k in (6, 8, 10, 12, 13, 14, 15):
        pairs = [(at_depth(c, k), at_depth(g, k), at_depth(c, k, "t"), at_depth(g, k, "t")) for c, g in zip(cold, go)]
        pairs = [p for p in pairs if None not in p]
        if pairs:
            print(f"| {k} | {med(p[2] for p in pairs):.0f} | {med(p[3] for p in pairs):.0f} | {geo([p[1]/p[0] for p in pairs]):.2f} | {len(pairs)} |")
    print("\n| the deepest depth both reached | positions | nodes go / cold (median) | ms go / cold (median) |")
    print("|---|---:|---:|---:|")
    for name, pred in (("all", None), ("P was our own search's line (its PV)", True), ("P was not", False)):
        nr, tr = [], []
        for r in rows:
            is_pred = r["own"]["move"] == r["sample"]["P"].split()[-2] and r["own"]["reply"] == r["sample"]["P"].split()[-1]
            if pred is not None and is_pred != pred:
                continue
            d = min(r["cold"]["infos"][-1]["depth"], r["go"]["infos"][-1]["depth"])
            nr.append(at_depth(r["go"], d) / at_depth(r["cold"], d))
            tr.append(at_depth(r["go"], d, "t") / max(1, at_depth(r["cold"], d, "t")))
        if nr:
            print(f"| {name} | {len(nr)} | {med(nr):.2f} | {med(tr):.2f} |")
    # the ponder search's depths before the hit equal go's
    agree = 0
    total = 0
    for r in rows:
        g = {i["depth"]: i["nodes"] for i in r["go"]["infos"]}
        for f in ("0.25", "0.75", "1.5"):
            for i in r[f"hit{f}"]["infos"]:
                if i["depth"] in g:
                    total += 1
                    agree += g[i["depth"]] == i["nodes"]
    print(f"\nponder searches' depths with the same node count as go's same depth: {agree} of {total}")
    own_match = sum(r["own"]["move"] == r["sample"]["P"].split()[-2] and r["own"]["reply"] == r["sample"]["P"].split()[-1] for r in rows)
    print(f"our own search at A predicted the bot's line (move and reply): {own_match} of {len(rows)}\n")


def misses(rows):
    if not rows:
        return
    tp = rows[0].get("threads_ponder")
    print(f"## misses: {len(rows)} positions; the ponder on {tp or 1} thread(s); the search at B to a fixed depth D on one thread\n")
    print("| search at B, to depth D | nodes (median) | ms (median) | nodes / no-ponder (geometric mean) | hashfull at the stop (median, per mille) |")
    print("|---|---:|---:|---:|---:|")
    base = [r["noponder"]["infos"][-1]["nodes"] for r in rows]
    cold = [at_depth(r["coldB"], r["D"]) for r in rows]
    cold_ms = [at_depth(r["coldB"], r["D"], "t") for r in rows]
    print(f"| cold (ucinewgame first) | {med(cold)/1e6:.1f} M | {med(cold_ms):.0f} | {geo([c/b for c,b in zip(cold, base)]):.2f} | |")
    print(f"| after our own search, no ponder | {med(base)/1e6:.1f} M | {med(r['noponder']['wall_ms'] for r in rows):.0f} | 1.00 | |")
    for f in ("0.75", "1.5", "3.0"):
        a = [r[f"miss{f}"]["after"]["infos"][-1]["nodes"] for r in rows]
        hf = [r[f"miss{f}"]["infos"][-1]["hashfull"] for r in rows if r[f"miss{f}"]["infos"]]
        print(f"| after our own search, then {f} x soft pondering the wrong reply | {med(a)/1e6:.1f} M | {med(r[f'miss{f}']['after']['wall_ms'] for r in rows):.0f} | {geo([x/b for x,b in zip(a, base)]):.2f} | {med(hf):.0f} |")
    print()
    print("| depth at B | cold ms | no-ponder ms | miss 0.75 ms | miss 3.0 ms | n |")
    print("|---:|---:|---:|---:|---:|---:|")
    for back in (6, 4, 2, 0):
        vals = []
        for r in rows:
            k = r["D"] - back
            v = (at_depth(r["coldB"], k, "t"), at_depth(r["noponder"], k, "t"),
                 at_depth(r["miss0.75"]["after"], k, "t"), at_depth(r["miss3.0"]["after"], k, "t"))
            if None not in v:
                vals.append(v)
        if vals:
            print(f"| D-{back} | {med(v[0] for v in vals):.0f} | {med(v[1] for v in vals):.0f} | {med(v[2] for v in vals):.0f} | {med(v[3] for v in vals):.0f} | {len(vals)} |")
    print(f"\nD (median) {med(r['D'] for r in rows):.0f}; own search at A predicted the bot's move: "
          f"{sum(r['own']['move'] == r['sample']['P'].split()[-2] for r in rows)} of {len(rows)}; "
          f"moves at B the same as no-ponder: " + ", ".join(f"{f}: {sum(r[f'miss{f}']['after']['move'] == r['noponder']['move'] for r in rows)}" for f in ("0.75", "1.5", "3.0")))


if __name__ == "__main__":
    hits(load(sys.argv[1]))
    for path in sys.argv[2:]:
        misses(load(path))
