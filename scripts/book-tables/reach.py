"""Free statistics for the self-play book scope: the bot's book reach (record.csv) and the 2200 book's shape."""
import csv, os, struct, sys, collections, statistics

import subprocess


def main_checkout():
    """bot\\games and bot\\books aren't in git: from a worktree, the main checkout's."""
    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    if os.path.isdir(os.path.join(repo, "bot", "games")):
        return repo
    common = subprocess.run(["git", "-C", repo, "rev-parse", "--git-common-dir"], capture_output=True, text=True).stdout.strip()
    return os.path.dirname(os.path.abspath(os.path.join(repo, common)))


MAIN = main_checkout()
rec = os.path.join(MAIN, "bot", "games", "record.csv")
rows = list(csv.DictReader(open(rec, encoding="utf-8")))
print("games", len(rows))

def n(x):
    try:
        return int(x)
    except (TypeError, ValueError):
        return 0

# our book moves per game (main + ours + wide), split by era
for label, sel in (("all", rows), ("networks (net startswith nnue or h)", [r for r in rows if r["net"].startswith(("nnue", "h"))])):
    ours = [n(r["book_main"]) + n(r["book_ours"]) + n(r["book_wide"]) for r in sel]
    if not ours:
        continue
    c = collections.Counter(min(x, 12) for x in ours)
    print(label, "games", len(sel), "mean our book moves", round(statistics.mean(ours), 2),
          "median", statistics.median(ours))
    print("  distribution (our book moves: games):", dict(sorted(c.items())))
    res = [float(r["score"]) for r in sel if r["score"] not in ("", None)]
    print("  score", round(statistics.mean(res), 3), "n", len(res))
    # score by book length: short (<=4), mid (5-8), long (9+)
    for lo, hi in ((0, 4), (5, 8), (9, 12)):
        s = [float(r["score"]) for r, o in zip(sel, ours) if lo <= min(o, 12) <= hi and r["score"] not in ("", None)]
        if s:
            print(f"  book moves {lo}-{hi}: games {len(s)} score {statistics.mean(s):.3f}")
    # draw share
    d = sum(1 for r in sel if r["score"] == "0.5")
    print("  draws", d, f"{d/len(sel):.1%}")
    # by speed
    sp = collections.Counter(r["speed"] for r in sel)
    print("  speeds", dict(sp))


def book_shape(path):
    """Positions and moves per position in a polyglot book (16-byte entries, sorted by key)."""
    data = open(path, "rb").read()
    count = len(data) // 16
    per = collections.Counter()
    last, k = None, 0
    for i in range(count):
        key = data[i * 16:i * 16 + 8]
        if key != last:
            if last is not None:
                per[k] += 1
            last, k = key, 0
        k += 1
    per[k] += 1
    positions = sum(per.values())
    print(os.path.basename(path), "entries", count, "positions", positions,
          "moves/position mean", round(count / positions, 2),
          "share with 1 move", f"{per[1]/positions:.1%}", "2", f"{per[2]/positions:.1%}", "3+", f"{1-(per[1]+per[2])/positions:.1%}")

for b in ("lichess-2200.bin", "lichess-2200-wide.bin"):
    book_shape(os.path.join(MAIN, "bot", "books", b))
