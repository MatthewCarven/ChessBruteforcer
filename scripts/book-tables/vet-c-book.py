"""BT1 (c), step 4: the vetted copy of the human book, and the veto list.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/book-tables/vet-c-book.py [--worse 50]

A book move is vetoed when it's --worse (50) cp or more behind our best both in the screen (results/vet-c-screen.csv,
depth 16) and in the confirm (results/vet-c-confirm.csv, depth 18). The copy of bot\\books\\lichess-2200.bin (main
checkout) drops the vetoed moves. Where no move we checked and kept is left in a position (every move with 5%+ of its
weight vetoed), all its moves go and our best at depth 18 goes in, alone, so a rare unchecked move can't take over.
Out: games\\lichess-2200-vetted-black.bin in the main checkout (gitignored; never bot\\books, the live bot's), and
results/vet-c-vetoes.csv (with Stockfish's verdict where results/vet-c-sf-vetoes.csv has one).
"""
import csv, os, struct, subprocess, sys
import chess, chess.polyglot

HERE = os.path.dirname(os.path.abspath(__file__))
RESULTS = os.path.join(HERE, "results")


def main_checkout():
    """bot\\books isn't in git: from a worktree, the main checkout's."""
    repo = os.path.dirname(os.path.dirname(HERE))
    if os.path.isdir(os.path.join(repo, "bot", "books")):
        return repo
    common = subprocess.run(["git", "-C", repo, "rev-parse", "--git-common-dir"], capture_output=True, text=True).stdout.strip()
    return os.path.dirname(os.path.abspath(os.path.join(repo, common)))


WORSE = int(sys.argv[sys.argv.index("--worse") + 1]) if "--worse" in sys.argv else 50
MAIN = main_checkout()
SOURCE = os.path.join(MAIN, "bot", "books", "lichess-2200.bin")
TARGET = os.path.join(MAIN, "games", "lichess-2200-vetted-black.bin")


def polyglot_move(board, move):
    """A move in Polyglot's 16 bits: castling as the king taking its own rook (as bot/learn.py)."""
    to = move.to_square
    if board.is_castling(move):
        to = chess.square(7 if chess.square_file(move.to_square) == 6 else 0, chess.square_rank(move.from_square))
    promotion = {None: 0, chess.KNIGHT: 1, chess.BISHOP: 2, chess.ROOK: 3, chess.QUEEN: 4}[move.promotion]
    return (chess.square_file(to) | chess.square_rank(to) << 3 | chess.square_file(move.from_square) << 6
            | chess.square_rank(move.from_square) << 9 | promotion << 12)


def rows_of(name):
    path = os.path.join(RESULTS, name)
    return list(csv.DictReader(open(path, encoding="utf-8"))) if os.path.exists(path) else []


positions = {r["key"]: r for r in csv.DictReader(open(os.path.join(RESULTS, "vet-c-positions.csv"), encoding="utf-8"))}
screen = {(r["key"], r["move"]): r for r in rows_of("vet-c-screen.csv") if r["kind"] == "move"}
confirm = {(r["key"], r["move"]): r for r in rows_of("vet-c-confirm.csv") if r["kind"] == "move"}
best18 = {r["key"]: r for r in rows_of("vet-c-confirm.csv") if r["kind"] == "best"}
sf = {(r["key"], r["move"]): r for r in rows_of("vet-c-sf-vetoes.csv")}

flagged = {km for km, r in screen.items() if int(r["loss"]) >= WORSE}
unconfirmed = sorted(km for km in flagged if km not in confirm)
vetoed = {km for km in flagged if km in confirm and int(confirm[km]["loss"]) >= WORSE}
if unconfirmed:
    print(f"{len(unconfirmed)} flagged moves have no confirm yet: they stay in the book")

by_key = {}
for key, move in vetoed:
    by_key.setdefault(key, set()).add(move)
replace = {}
for key, moves in by_key.items():
    kept = [m for (k, m) in screen if k == key and (k, m) not in vetoed]
    if not kept:
        replace[key] = best18[key]["best"]

entries = []
data = open(SOURCE, "rb").read()
removed = 0
for i in range(0, len(data), 16):
    key, move, weight, learn = struct.unpack(">QHHI", data[i:i + 16])
    skey = str(key)
    if skey in by_key:
        board = chess.Board(positions[skey]["fen"])
        decoded = next((m for m in board.legal_moves if polyglot_move(board, m) == move), None)
        if skey in replace or (decoded is not None and decoded.uci() in by_key[skey]):
            removed += 1
            continue
    entries.append((key, move, weight, learn))
added = []
for skey, best in replace.items():
    board = chess.Board(positions[skey]["fen"])
    weight = max(int(e.split(":")[1]) for e in positions[skey]["book"].split())
    added.append((int(skey), polyglot_move(board, chess.Move.from_uci(best)), weight, 0))
entries = sorted(entries + added, key=lambda e: e[0])
temp = TARGET + ".tmp"
with open(temp, "wb") as out:
    for e in entries:
        out.write(struct.pack(">QHHI", *e))
os.replace(temp, TARGET)

# check it back
with chess.polyglot.open_reader(TARGET) as reader:
    for (key, move) in vetoed:
        board = chess.Board(positions[key]["fen"])
        assert all(e.move.uci() != move for e in reader.find_all(board)), (key, move)
    for key, best in replace.items():
        board = chess.Board(positions[key]["fen"])
        assert [e.move.uci() for e in reader.find_all(board)] == [best], key
print(f"{TARGET}: {len(entries)} entries ({len(data) // 16} in the human book): {removed} removed, {len(added)} added; "
      f"{len(vetoed)} moves vetoed in {len(by_key)} positions, {len(replace)} positions given our best alone")

with open(os.path.join(RESULTS, "vet-c-vetoes.csv"), "w", newline="", encoding="utf-8") as f:
    w = csv.writer(f)
    w.writerow(["rank", "tier", "move_no", "fen", "vetoed", "san", "share", "played", "loss_d16", "loss_d18",
                "best_d18", "best_san", "best_cp_d18", "replaced", "sf_loss", "sf_loss_ours", "families"])
    for key, move in sorted(vetoed, key=lambda km: int(positions[km[0]]["rank"])):
        p, s, c = positions[key], screen[(key, move)], confirm[(key, move)]
        board = chess.Board(p["fen"])
        best = best18[key]["best"]
        v = sf.get((key, move), {})
        w.writerow([p["rank"], p["tier"], p["move_no"], p["fen"], move, board.san(chess.Move.from_uci(move)), s["share"],
                    s["played"], s["loss"], c["loss"], best, board.san(chess.Move.from_uci(best)), best18[key]["best_cp"],
                    "yes" if key in replace else "", v.get("sf_loss", ""), v.get("sf_loss_ours", ""), p["families"]])
