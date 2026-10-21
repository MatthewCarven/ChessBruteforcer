"""Per-game detail for the level-stretch games, to check the rating sums and read the losses.

    python -I detail.py data.json N
"""
import json
import sys

N = int(sys.argv[2])
sys.argv = sys.argv[:2]
import importlib.util
spec = importlib.util.spec_from_file_location("an", __file__.replace("detail.py", "analyze.py"))
an = importlib.util.module_from_spec(spec)
spec.loader.exec_module(an)

rows = []
for g in an.done:
    t = an.stretch(g, N)
    if not t:
        continue
    b, w, n, m = an.after(g, t[0])
    rows.append((an.rd_bucket(g), g["date"], g["id"], an.pool(g), g["tc"], g["opponent"], g["opp_elo"] - g["our_elo"],
                 g["our_elo"], t[0] // 2 + 1, t[1], an.S(g), g["termination"], g["our_diff"], round(an.K(g), 1),
                 round(an.E(g), 2), b, w, n, round(m, 1), g["final_pieces"]))
rows.sort()
print("bucket | date | id | pool | tc | opponent | opp-us | our elo | trig move | pieces | S | term | diff | K | E | best | worst | plies left | min left | final pieces")
for r in rows:
    print(" | ".join(str(x) for x in r))
