"""How the books leave the bot: our engine's own evaluation on our first move out of book (network-era games).

The bot's PGNs carry [%eval x,depth] after each engine move (White's view, python-chess's set_eval); book moves
carry none. record.csv says which net played and how many of our moves came from books.
"""
import csv, glob, os, re, statistics, collections, sys

import subprocess


def main_checkout():
    """bot\\games and bot\\books aren't in git: from a worktree, the main checkout's."""
    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    if os.path.isdir(os.path.join(repo, "bot", "games")):
        return repo
    common = subprocess.run(["git", "-C", repo, "rev-parse", "--git-common-dir"], capture_output=True, text=True).stdout.strip()
    return os.path.dirname(os.path.abspath(os.path.join(repo, common)))


MAIN = main_checkout()
rows = {r["id"]: r for r in csv.DictReader(open(os.path.join(MAIN, "bot", "games", "record.csv"), encoding="utf-8"))}
EVAL = re.compile(r"\[%eval (#?-?[\d.]+),(\d+)\]")
GAMEID = re.compile(r'\[GameId "([^"]+)"\]')

out = []
for path in glob.glob(os.path.join(MAIN, "bot", "games", "*.pgn")):
    text = open(path, encoding="utf-8", errors="replace").read()
    m = GAMEID.search(text)
    if not m or m.group(1) not in rows:
        continue
    r = rows[m.group(1)]
    if not r["net"].startswith(("nnue", "h")):
        continue
    evals = EVAL.findall(text)
    if not evals:
        continue
    first, depth = evals[0]
    if first.startswith("#"):
        continue
    v = float(first)
    if r["colour"] == "black":
        v = -v
    later = None
    if len(evals) >= 6 and not evals[5][0].startswith("#"):
        later = float(evals[5][0]) * (-1 if r["colour"] == "black" else 1)
    book = sum(int(r[k] or 0) for k in ("book_main", "book_ours", "book_wide"))
    score = float(r["score"]) if r["score"] not in ("", None) else None
    out.append((r["colour"], book, v, int(depth), later, score, r["net"], r["opp_elo"], r["our_elo"]))

print("network-era games with an eval:", len(out))
for colour in ("white", "black", None):
    sel = [o for o in out if colour is None or o[0] == colour]
    vs = sorted(o[2] for o in sel)
    q = statistics.quantiles(vs, n=4)
    print(f"{colour or 'both'}: n {len(sel)} first-engine-move eval (our view, pawns): mean {statistics.mean(vs):+.2f} "
          f"quartiles {q[0]:+.2f} {q[1]:+.2f} {q[2]:+.2f}; <= -0.50: {sum(v <= -0.5 for v in vs)}; "
          f"<= -1.00: {sum(v <= -1.0 for v in vs)}; >= +0.50: {sum(v >= 0.5 for v in vs)}; depth median {statistics.median(o[3] for o in sel)}")
# score by exit eval band
bands = [(-99, -0.5), (-0.5, -0.15), (-0.15, 0.15), (0.15, 0.5), (0.5, 99)]
for lo, hi in bands:
    s = [o for o in out if lo < o[2] <= hi and o[5] is not None]
    if s:
        print(f"exit eval ({lo:+.2f}, {hi:+.2f}]: games {len(s)} score {statistics.mean(o[5] for o in s):.3f} "
              f"mean opp-minus-us elo {statistics.mean(int(o[7]) - int(o[8]) for o in s if o[7] and o[8]):+.0f}")
# eval at exit by book length
for lo, hi in ((0, 6), (7, 9), (10, 12)):
    s = [o[2] for o in out if lo <= o[1] <= hi]
    if s:
        print(f"book moves {lo}-{hi}: n {len(s)} mean exit eval {statistics.mean(s):+.2f} median {statistics.median(s):+.2f}")
# drift: eval five engine moves later minus at exit
d = [o[4] - o[2] for o in out if o[4] is not None]
print(f"five engine moves on, minus exit: n {len(d)} mean {statistics.mean(d):+.2f} median {statistics.median(d):+.2f}")
