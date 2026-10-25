"""The Stockfish check (MATCHES.md): the half with tables split by how many games ran at once (games 0-37 at 2,
the rest at 1), against the half without, by book-match.py's sums. From the main checkout:
  bot\lichess-bot\.venv\Scripts\python.exe -I scripts\sf-check\sf3190-split.py games\sf-check\sf3190.jsonl games\sf-check\sf3190-notables.jsonl
"""
import json, math, sys
from collections import Counter

def elo(score):
    score = min(max(score, 0.001), 0.999)
    return -400 * math.log10(1 / score - 1)

def line(name, games):
    pts = [g["points"] for g in games]
    n = len(pts)
    mean = sum(pts) / n
    sd = math.sqrt(sum((p - mean) ** 2 for p in pts) / max(1, n - 1))
    half = 1.96 * sd / math.sqrt(n)
    w = sum(p == 1 for p in pts); l = sum(p == 0 for p in pts); d = n - w - l
    wh = [g["points"] for g in games if g["we"] == "white"]
    bl = [g["points"] for g in games if g["we"] == "black"]
    plies = sum(g["plies"] for g in games) / n
    terms = Counter(g["termination"] for g in games)
    print(f"{name:22} {n:4} +{w} -{l} ={d}  {100*mean:.1f}% (+-{100*half:.1f})  Elo {elo(mean):+.0f} [{elo(mean-half):+.0f}, {elo(mean+half):+.0f}]"
          f"  White {100*sum(wh)/len(wh):.1f}% ({len(wh)})  Black {100*sum(bl)/len(bl):.1f}% ({len(bl)})  plies {plies:.0f}")
    print(" " * 23 + ", ".join(f"{k} {v}" for k, v in terms.most_common()))
    clocks = [g["our_clock_end"] for g in games if g.get("our_clock_end") is not None]
    if clocks:
        print(" " * 23 + f"our clock at the end: min {min(clocks):.1f} s, under 1 s {sum(c < 1 for c in clocks)}")
    return n, mean, half

def load(path):
    return [json.loads(x) for x in open(path, encoding="utf-8") if x.strip()]

tables = load(sys.argv[1])
notables = load(sys.argv[2])
two = [g for g in tables if g["game"] <= 37]
one = [g for g in tables if g["game"] >= 38]
a = line("with tables, 2 at once", two)
b = line("with tables, 1 at once", one)
c = line("with tables, all", tables)
z = line("no tables (3 at once)", notables)
for name, (n1, m1, h1) in (("all", c), ("1 at once", b)):
    diff, half = m1 - z[1], math.sqrt(h1 ** 2 + z[2] ** 2)
    print(f"tables minus none ({name}): {100*diff:+.1f} points [{100*(diff-half):+.1f}, {100*(diff+half):+.1f}], "
          f"Elo {elo(m1) - elo(z[1]):+.0f}")
    # As Elo around the pair's mean score: convert the point interval there.
    mid = (m1 + z[1]) / 2
    per_point = 400 / (math.log(10) * mid * (1 - mid))
    print(f"   about {per_point*diff:+.0f} Elo [{per_point*(diff-half):+.0f}, {per_point*(diff+half):+.0f}]")
