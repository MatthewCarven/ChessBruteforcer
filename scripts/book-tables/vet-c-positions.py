"""BT1 (c), step 1: the positions to vet, in priority order (results/vet-c-positions.csv).

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/book-tables/vet-c-positions.py [--to-move 8] [--total 800]

A position here is one where the bot is Black, it's our move (our moves 1 to --to-move), and the human book
(lichess-2200.bin, the first in make-config.py's chain, so the one the bot plays from there) has a move. Tiers:
  1  on the book line of a network-era game (net nnue* or h*) where the bot was Black and its first move out of book
     was evaluated -0.50 or worse (bookexit.py's measure);
  2  on the book line of any other network-era Black game;
  3  the rest: older games, and the book's likely continuations, by how often the bot would reach them. White moves
     as the bot's opponents did wherever 10+ of its games went through, else by the book's weights; Black as
     lichess-bot plays (weighted_random by weight). Filled up to --total positions in all.
The moves vetted in a position: every book move with 5%+ of its weight, and any move the bot played there.
Within a tier, positions go most-reached first (games through them / Black games, or the continuations' estimate).
"""
import collections, csv, glob, io, os, re, subprocess, sys
import chess, chess.pgn, chess.polyglot


def main_checkout():
    """bot\\games and bot\\books aren't in git: from a worktree, the main checkout's."""
    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    if os.path.isdir(os.path.join(repo, "bot", "games")):
        return repo
    common = subprocess.run(["git", "-C", repo, "rev-parse", "--git-common-dir"], capture_output=True, text=True).stdout.strip()
    return os.path.dirname(os.path.abspath(os.path.join(repo, common)))


def option(name, default, kind=int):
    return kind(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default


TO_MOVE, TOTAL, SHARE = option("--to-move", 8), option("--total", 800), 0.05
MAIN = main_checkout()
ME = "matthewcarven"
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "results", "vet-c-positions.csv")
EVAL = re.compile(r"\[%eval (#?-?[\d.]+),(\d+)\]")
rows = {r["id"]: r for r in csv.DictReader(open(os.path.join(MAIN, "bot", "games", "record.csv"), encoding="utf-8"))}
reader = chess.polyglot.open_reader(os.path.join(MAIN, "bot", "books", "lichess-2200.bin"))


def book(board):
    return [(e.move, e.weight) for e in reader.find_all(board)]


games = collections.Counter()           # Black games through a position (all eras)
net_games, bad_games = collections.Counter(), collections.Counter()
played = collections.defaultdict(collections.Counter)
families = collections.defaultdict(collections.Counter)
fen, moveno, entries = {}, {}, {}
white_moves = collections.defaultdict(collections.Counter)
n_black = 0
for path in sorted(glob.glob(os.path.join(MAIN, "bot", "games", "*.pgn"))):
    raw = open(path, "rb").read()
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError:
        text = raw.decode("cp1252")
    game = chess.pgn.read_game(io.StringIO(text))
    if game is None or game.headers.get("Black") != ME:
        continue
    row = rows.get(game.headers.get("GameId"))
    net = row is not None and row["net"].startswith(("nnue", "h"))
    ev = EVAL.findall(text)
    bad = net and bool(ev) and not ev[0][0].startswith("#") and -float(ev[0][0]) <= -0.5
    family = row["opening"].split(":")[0] if row else "?"
    n_black += 1
    board = game.board()
    for move in game.mainline_moves():
        if len(board.move_stack) > 23:
            break
        key = chess.polyglot.zobrist_hash(board)
        if board.turn == chess.WHITE:
            white_moves[key][move] += 1
        elif board.fullmove_number <= TO_MOVE:
            b = book(board)
            if b:
                games[key] += 1
                net_games[key] += net
                bad_games[key] += bad
                played[key][move] += 1
                if net:
                    families[key][family] += 1
                fen[key], moveno[key], entries[key] = board.fen(), board.fullmove_number, b
        board.push(move)

reach = collections.defaultdict(float)


def expand(board, p):
    if p < 1e-4:
        return
    key = chess.polyglot.zobrist_hash(board)
    if board.turn == chess.WHITE:
        seen = white_moves.get(key)
        if seen and sum(seen.values()) >= 10:
            total = sum(seen.values())
            choices = [(m, c / total) for m, c in seen.items()]
        else:
            b = book(board)
            total = sum(w for _, w in b)
            choices = [(m, w / total) for m, w in b] if total else []
        for m, share in choices:
            board.push(m)
            expand(board, p * share)
            board.pop()
    elif board.fullmove_number <= TO_MOVE:
        b = book(board)
        if not b:
            return
        reach[key] += p
        fen.setdefault(key, board.fen())
        moveno[key], entries[key] = board.fullmove_number, b
        total = sum(w for _, w in b)
        for m, w in b:
            board.push(m)
            expand(board, p * w / total)
            board.pop()


expand(chess.Board(), 1.0)
keys = set(reach) | set(games)
prio = {k: max(reach.get(k, 0.0), games[k] / n_black) for k in keys}
tier = {k: 1 if bad_games[k] else 2 if net_games[k] else 3 for k in keys}
order = sorted(keys, key=lambda k: (tier[k], -prio[k], moveno[k]))
order = [k for k in order if tier[k] < 3] + [k for k in order if tier[k] == 3][:max(0, TOTAL - sum(tier[k] < 3 for k in keys))]

with open(OUT, "w", newline="", encoding="utf-8") as f:
    w = csv.writer(f)
    w.writerow(["rank", "tier", "key", "move_no", "fen", "games", "net_games", "bad_games", "reach", "book", "vet",
                "played", "families"])
    for rank, k in enumerate(order, 1):
        b = entries[k]
        total = sum(wt for _, wt in b)
        vet = [m.uci() for m, wt in sorted(b, key=lambda e: -e[1]) if wt / total >= SHARE]
        vet += [m.uci() for m in played[k] if m.uci() not in vet]
        w.writerow([rank, tier[k], k, moveno[k], fen[k], games[k], net_games[k], bad_games[k], f"{reach.get(k, 0.0):.6f}",
                    " ".join(f"{m.uci()}:{wt}" for m, wt in sorted(b, key=lambda e: -e[1])), " ".join(vet),
                    " ".join(f"{m.uci()}:{c}" for m, c in played[k].most_common()),
                    "; ".join(f"{name}:{c}" for name, c in families[k].most_common())])

counts = collections.Counter(tier[k] for k in order)
moves = collections.Counter()
for k in order:
    b = entries[k]
    total = sum(wt for _, wt in b)
    moves[tier[k]] += len({m for m, wt in b if wt / total >= SHARE} | set(played[k]))
print(f"Black games {n_black}; positions to our move {TO_MOVE}: " +
      ", ".join(f"tier {t} {counts[t]} ({moves[t]} moves to vet)" for t in sorted(counts)) + f" -> {OUT}")
