"""Summarise margins.jsonl: moves within 20 / 30 / 50 cp of the best, by ply band (cloud multiPv 5, so 5 = "5 or
more"); and the geometric mean (what a tree's growth follows)."""
import json, os, math, statistics, collections

HERE = os.path.dirname(os.path.abspath(__file__))
rows = [json.loads(l) for l in open(os.path.join(HERE, "results", "margins.jsonl"), encoding="utf-8") if l.strip()]
got = [r for r in rows if r.get("cps")]
print(f"{len(rows)} positions asked, {len(got)} in the cloud, {len(rows) - len(got)} not; "
      f"pvs returned: {collections.Counter(len(r['cps']) for r in got)}; depth median {statistics.median(r['depth'] for r in got)}")
bands = [(0, 3), (4, 7), (8, 11), (12, 15), (16, 19)]
print("plies  | n | asked | within 20 | 30 | 50 (mean; geo-mean) | share capped at 5 (50 cp)")
for lo, hi in bands + [(0, 19)]:
    sel = [r for r in got if lo <= r["ply"] <= hi and len(r["cps"]) >= 5]
    asked = sum(1 for r in rows if lo <= r["ply"] <= hi)
    if not sel:
        print(lo, hi, "none")
        continue
    out = []
    for m in (20, 30, 50):
        ks = [sum(1 for c in r["cps"] if r["cps"][0] - c <= m) for r in sel]
        out.append(f"{statistics.mean(ks):.2f}; {math.exp(statistics.mean(math.log(k) for k in ks)):.2f}")
    cap = sum(1 for r in sel if sum(1 for c in r["cps"] if r["cps"][0] - c <= 50) == 5) / len(sel)
    print(f"{lo}-{hi} | {len(sel)} | {asked} | " + " | ".join(out) + f" | {cap:.0%}")
