"""Does the exit evaluation predict the result once the ratings are allowed for? Score minus the Elo expectation,
regressed on the exit evaluation (clipped to +-2 pawns), with a bootstrap interval. Reuses bookexit.py's parsing."""
import random, statistics, runpy, os, io, contextlib

here = os.path.dirname(os.path.abspath(__file__))
with contextlib.redirect_stdout(io.StringIO()):
    g = runpy.run_path(os.path.join(here, "bookexit.py"))
out = [o for o in g["out"] if o[5] is not None and o[7] and o[8]]


def fit(rows):
    xs = [max(-2.0, min(2.0, o[2])) for o in rows]
    ys = [o[5] - 1 / (1 + 10 ** ((int(o[7]) - int(o[8])) / 400)) for o in rows]
    mx, my = statistics.mean(xs), statistics.mean(ys)
    sxx = sum((x - mx) ** 2 for x in xs)
    return sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / sxx, my


b, mean_resid = fit(out)
random.seed(1)
boots = sorted(fit([random.choice(out) for _ in out])[0] for _ in range(2000))
print(f"n {len(out)}; score per pawn at exit, ratings allowed for: {b:+.3f} [{boots[50]:+.3f}, {boots[1949]:+.3f}]; "
      f"mean score over expectation {mean_resid:+.3f}")
for colour in ("white", "black"):
    sel = [o for o in out if o[0] == colour]
    bb, mr = fit(sel)
    print(f"  {colour}: n {len(sel)} slope {bb:+.3f}, over expectation {mr:+.3f}")
# Black exits brought to White's median gap: what would the slope say?
white_med = statistics.median(o[2] for o in out if o[0] == "white")
black_med = statistics.median(o[2] for o in out if o[0] == "black")
print(f"white median {white_med:+.2f}, black median {black_med:+.2f}; "
      f"a black exit 0.3 pawn better at slope {b:+.3f} -> {0.3 * b:+.3f} score a black game")
