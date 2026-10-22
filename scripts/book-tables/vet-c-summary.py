"""BT1 (c), step 5: the numbers for results/bt-c.md, from the vet-c result files.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/book-tables/vet-c-summary.py [--worse 50]
"""
import collections, csv, glob, io, os, re, statistics, subprocess, sys
import chess, chess.pgn, chess.polyglot

HERE = os.path.dirname(os.path.abspath(__file__))
RESULTS = os.path.join(HERE, "results")
WORSE = int(sys.argv[sys.argv.index("--worse") + 1]) if "--worse" in sys.argv else 50
EVAL = re.compile(r"\[%eval (#?-?[\d.]+),(\d+)\]")


def main_checkout():
    repo = os.path.dirname(os.path.dirname(HERE))
    if os.path.isdir(os.path.join(repo, "bot", "games")):
        return repo
    common = subprocess.run(["git", "-C", repo, "rev-parse", "--git-common-dir"], capture_output=True, text=True).stdout.strip()
    return os.path.dirname(os.path.abspath(os.path.join(repo, common)))


def rows_of(name):
    path = os.path.join(RESULTS, name)
    return list(csv.DictReader(open(path, encoding="utf-8"))) if os.path.exists(path) else []


def share(part, whole):
    return f"{part} ({part / whole:.0%})" if whole else str(part)


MAIN = main_checkout()
positions = {r["key"]: r for r in rows_of("vet-c-positions.csv")}
screen_rows = rows_of("vet-c-screen.csv")
confirm_rows = rows_of("vet-c-confirm.csv")
screen = {(r["key"], r["move"]): r for r in screen_rows if r["kind"] == "move"}
confirm = {(r["key"], r["move"]): r for r in confirm_rows if r["kind"] == "move"}
sfv = {(r["key"], r["move"]): r for r in rows_of("vet-c-sf-vetoes.csv")}
flagged = {km for km, r in screen.items() if int(r["loss"]) >= WORSE}
vetoed = {km for km in flagged if km in confirm and int(confirm[km]["loss"]) >= WORSE}

print("## Totals")
keys = {r["key"] for r in screen_rows if r["kind"] == "best"}
tiers = collections.Counter(positions[k]["tier"] for k in keys)
secs = sum(float(r["seconds"]) for r in screen_rows + confirm_rows)
print(f"positions searched {len(keys)} (tier 1 {tiers['1']}, tier 2 {tiers['2']}); moves scored {len(screen)}; "
      f"searches {sum(1 for r in screen_rows + confirm_rows if r['kind'] == 'best' or float(r['seconds']) > 0)}; "
      f"engine time {secs / 3600:.1f} core-h")
print(f"{WORSE}+ cp behind at depth 16: {len(flagged)} moves; still at depth 18: {len(vetoed)}; "
      f"in {len({k for k, _ in vetoed})} positions")
played = [(r, int(r["played"])) for r in screen.values() if int(r["played"])]
plays = sum(c for _, c in played)
for line in (25, 50, 100):
    print(f"  the bot's book moves (as played, all eras) {line}+ cp behind at depth 16: "
          f"{share(sum(c for r, c in played if int(r['loss']) >= line), plays)} of {plays}")

# the book's loss along each network-era Black game's line, by defence
rows = {r["id"]: r for r in csv.DictReader(open(os.path.join(MAIN, "bot", "games", "record.csv"), encoding="utf-8"))}
fam = collections.defaultdict(list)
for path in sorted(glob.glob(os.path.join(MAIN, "bot", "games", "*.pgn"))):
    raw = open(path, "rb").read()
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError:
        text = raw.decode("cp1252")
    game = chess.pgn.read_game(io.StringIO(text))
    if game is None or game.headers.get("Black") != "matthewcarven":
        continue
    row = rows.get(game.headers.get("GameId"))
    ev = EVAL.findall(text)
    if row is None or not row["net"].startswith(("nnue", "h")) or not ev or ev[0][0].startswith("#"):
        continue
    board, total, n, worst, crossed = game.board(), 0, 0, None, False
    for move in game.mainline_moves():
        if board.turn == chess.BLACK and board.fullmove_number <= 8:
            r = screen.get((str(chess.polyglot.zobrist_hash(board)), move.uci()))
            if r:
                loss = int(r["loss"])
                total, n = total + loss, n + 1
                crossed |= (r["key"], r["move"]) in vetoed
                if worst is None or loss > worst[0]:
                    worst = (loss, board.fullmove_number, board.san(move))
        board.push(move)
    fam[row["opening"].split(":")[0]].append((-float(ev[0][0]), total, n, worst, crossed))
print("\n## By defence (network-era games as Black, 3+ games): exit eval, and the book's loss along the line (depth 16)")
print("| defence | games | median exit (pawns) | median book loss on the line (cp) | games through a veto | worst move seen |")
print("|---|---:|---:|---:|---:|---|")
for name, g in sorted(fam.items(), key=lambda kv: statistics.median(x[0] for x in kv[1])):
    if len(g) < 3:
        continue
    worst = max((x[3] for x in g if x[3]), default=None)
    print(f"| {name} | {len(g)} | {statistics.median(x[0] for x in g):+.2f} | {statistics.median(x[1] for x in g):.0f} | "
          f"{sum(x[4] for x in g)} | {f'{worst[1]}...{worst[2]} ({worst[0]} cp)' if worst else ''} |")
allg = [x for g in fam.values() for x in g]
if allg:
    lo = [x for x in allg if x[0] <= -0.5]
    hi = [x for x in allg if x[0] > -0.5]
    print(f"\nexits at -0.50 or worse: {len(lo)} games, median book loss {statistics.median(x[1] for x in lo) if lo else 0:.0f} cp; "
          f"the rest: {len(hi)}, {statistics.median(x[1] for x in hi) if hi else 0:.0f} cp")

print("\n## Vetoes (depth 18), most-reached first")
print("| our move | position (FEN) | vetoed | share | played | loss d16 / d18 | our best | Stockfish's loss |")
print("|---:|---|---|---:|---:|---|---|---:|")
for key, move in sorted(vetoed, key=lambda km: int(positions[km[0]]["rank"])):
    p, s, c = positions[key], screen[(key, move)], confirm[(key, move)]
    board = chess.Board(p["fen"])
    best = next(r["best"] for r in confirm_rows if r["kind"] == "best" and r["key"] == key)
    print(f"| {p['move_no']} | `{p['fen']}` | {board.san(chess.Move.from_uci(move))} | {float(s['share']):.0%} | {s['played']} | "
          f"{s['loss']} / {c['loss']} | {board.san(chess.Move.from_uci(best))} | {sfv.get((key, move), {}).get('sf_loss', '')} |")

sfc = rows_of("vet-c-sf-controls.csv")
if sfv or sfc:
    print("\n## Stockfish (depth 20)")
    for name, rs in (("vetoes", list(sfv.values())), ("controls (moves we kept)", sfc)):
        if rs:
            ls = [int(r["sf_loss"]) for r in rs]
            print(f"{name}: {len(rs)}; Stockfish {WORSE}+ cp behind its best: {share(sum(x >= WORSE for x in ls), len(ls))}; "
                  f"25+: {share(sum(x >= 25 for x in ls), len(ls))}; median {statistics.median(ls):.0f} cp")
    if sfv:
        lo = [int(r["sf_loss_ours"]) for r in sfv.values()]
        print(f"our replacement move, by Stockfish: median {statistics.median(lo):.0f} cp behind its best")
ex = rows_of("vet-c-sf-exits.csv")
if ex:
    print("\n## Black's exits: our engine in the game against Stockfish at depth 20 (Black's view, pawns)")
    print("| defence | games | ours, median | Stockfish, median | ours minus Stockfish, median |")
    print("|---|---:|---:|---:|---:|")
    by = collections.defaultdict(list)
    for r in ex:
        by[r["family"]].append((int(r["ours_cp"]) / 100, int(r["sf_cp"]) / 100))
    by["(all)"] = [x for v in list(by.values()) for x in v]
    for name, v in sorted(by.items(), key=lambda kv: statistics.median(a - b for a, b in kv[1])):
        if len(v) >= 3:
            print(f"| {name} | {len(v)} | {statistics.median(a for a, _ in v):+.2f} | {statistics.median(b for _, b in v):+.2f} | "
                  f"{statistics.median(a - b for a, b in v):+.2f} |")
