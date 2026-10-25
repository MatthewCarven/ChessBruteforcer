"""A match played in parts, summed: Elo with its 95% interval, LOS and the LLR of SPRT [0, 10], by MatchStats'
formulas (src/ChessBruteforcer.Core/Match/MatchStats.cs). scripts\\nnue-match.cmd can't resume a match, so one stopped
and restarted plays as parts, each in its own log; give each part's wins, losses and draws, for the same side.

python scripts\\match-parts.py W L D [W L D ...]
"""
import math
import sys

v = list(map(int, sys.argv[1:]))
w, l, d = sum(v[0::3]), sum(v[1::3]), sum(v[2::3])
n = w + l + d
s = (w + d / 2) / n


def elo(x):
    x = min(max(x, 1e-6), 1 - 1e-6)
    return -400 * math.log10(1 / x - 1)


def score(e):
    return 1 / (1 + 10 ** (-e / 400))


var = (w * (1 - s) ** 2 + d * (0.5 - s) ** 2 + l * s ** 2) / n
m = 1.96 * math.sqrt(var / n)
los = 0.5 * (1 + math.erf((w - l) / math.sqrt(2 * (w + l))))
s0, s1 = score(0), score(10)
llr = n * (s1 - s0) * (2 * s - s0 - s1) / (2 * var)
lo, hi = math.log(0.05 / 0.95), math.log(0.95 / 0.05)
print(f"+{w} -{l} ={d}  {n} games  score {s:.1%}  Elo {elo(s):+.0f} [{elo(s - m):+.0f}, {elo(s + m):+.0f}]  "
      f"LOS {los:.1%}  LLR {llr:.2f} (bounds {lo:.2f}, {hi:.2f})")
