"""Black's exits by opening: is the low evaluation spread out, or a few defences? Joins bookexit.py's rows to
record.csv's opening name and first moves."""
import csv, os, re, glob, statistics, collections, io, contextlib, runpy

here = os.path.dirname(os.path.abspath(__file__))
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

fam = collections.defaultdict(list)
first2 = collections.defaultdict(list)
for path in glob.glob(os.path.join(MAIN, "bot", "games", "*.pgn")):
    text = open(path, encoding="utf-8", errors="replace").read()
    m = GAMEID.search(text)
    if not m or m.group(1) not in rows:
        continue
    r = rows[m.group(1)]
    if not r["net"].startswith(("nnue", "h")):
        continue
    ev = EVAL.findall(text)
    if not ev or ev[0][0].startswith("#"):
        continue
    v = float(ev[0][0]) * (-1 if r["colour"] == "black" else 1)
    family = r["opening"].split(":")[0]
    fam[(r["colour"], family)].append(v)
    first2[(r["colour"], " ".join(r["line"].split()[:2]))].append(v)

for colour in ("black", "white"):
    print(f"== {colour}: families with 4+ games (n, median exit, share <= -0.50)")
    items = [(k[1], v) for k, v in fam.items() if k[0] == colour and len(v) >= 4]
    for name, v in sorted(items, key=lambda kv: statistics.median(kv[1])):
        print(f"  {name[:45]:45s} {len(v):3d} {statistics.median(v):+.2f} {sum(x <= -0.5 for x in v) / len(v):.0%}")
    print(f"== {colour}: first two plies with 5+ games")
    items = [(k[1], v) for k, v in first2.items() if k[0] == colour and len(v) >= 5]
    for name, v in sorted(items, key=lambda kv: statistics.median(kv[1])):
        print(f"  {name:12s} {len(v):3d} {statistics.median(v):+.2f} {sum(x <= -0.5 for x in v) / len(v):.0%}")
