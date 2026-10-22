"""How many moves lie within 20 / 30 / 50 cp of the best, by ply, in positions the bot actually reaches?

Positions: the bot's network-era games (record.csv's first 20 plies), distinct, up to PER positions a ply.
Evaluations: Lichess's cloud evaluations (deep Stockfish, multiPv 5), one request a second; a position the cloud
doesn't hold is skipped and counted. No engine runs here. Results to margins.jsonl beside this file.
"""
import csv, json, os, random, sys, time, urllib.parse, urllib.request, urllib.error, collections
import chess

import subprocess


def main_checkout():
    """bot\\games and bot\\books aren't in git: from a worktree, the main checkout's."""
    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    if os.path.isdir(os.path.join(repo, "bot", "games")):
        return repo
    common = subprocess.run(["git", "-C", repo, "rev-parse", "--git-common-dir"], capture_output=True, text=True).stdout.strip()
    return os.path.dirname(os.path.abspath(os.path.join(repo, common)))


MAIN = main_checkout()
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "results", "margins.jsonl")
PER = int(sys.argv[1]) if len(sys.argv) > 1 else 8

rows = [r for r in csv.DictReader(open(os.path.join(MAIN, "bot", "games", "record.csv"), encoding="utf-8"))
        if r["net"].startswith(("nnue", "h"))]
by_ply = collections.defaultdict(set)
for r in rows:
    board = chess.Board()
    for ply, san in enumerate(r["line"].split()[:20]):
        by_ply[ply].add(board.epd())
        try:
            board.push_san(san)
        except ValueError:
            break
random.seed(7)
todo = []
for ply in range(20):
    pos = sorted(by_ply[ply])
    random.shuffle(pos)
    todo += [(ply, p) for p in pos[:PER]]
done = set()
if os.path.exists(OUT):
    done = {json.loads(l)["epd"] for l in open(OUT, encoding="utf-8") if l.strip()}
print(len(todo), "positions,", len(done), "done before", flush=True)

for ply, epd in todo:
    if epd in done:
        continue
    fen = epd + " 0 1"
    url = "https://lichess.org/api/cloud-eval?" + urllib.parse.urlencode({"fen": fen, "multiPv": 5})
    req = urllib.request.Request(url, headers={"User-Agent": "chessbruteforcer-book-scope (one-off, 1 req/s)"})
    rec = {"ply": ply, "epd": epd}
    try:
        with urllib.request.urlopen(req, timeout=20) as resp:
            data = json.load(resp)
        turn = 1 if " w " in epd else -1
        cps = []
        for pv in data.get("pvs", []):
            if "cp" in pv:
                cps.append(pv["cp"] * turn)
            elif "mate" in pv:
                cps.append((100000 if pv["mate"] > 0 else -100000) * turn)
        rec.update(cps=sorted(cps, reverse=True), depth=data.get("depth"), legal=chess.Board(fen).legal_moves.count())
    except urllib.error.HTTPError as e:
        rec["miss"] = e.code
        if e.code == 429:
            print("429: waiting 60 s", flush=True)
            time.sleep(60)
            continue
    except Exception as e:  # network trouble: record and move on
        rec["miss"] = str(e)[:80]
    with open(OUT, "a", encoding="utf-8") as out:
        out.write(json.dumps(rec) + "\n")
    time.sleep(1.0)
print("finished", flush=True)
