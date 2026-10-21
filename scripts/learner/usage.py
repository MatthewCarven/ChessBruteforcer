"""The book learner's entries (bot/books/ours.jsonl), and how often the bot's games met them. Reads only.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/learner/usage.py <the main checkout's bot folder>

Prints ours.jsonl by why, day, depth and move number; then replays our first 12 moves of every game in bot/games and
counts the positions that were in ours.jsonl, and those learnt before that game (times: ours.jsonl's are local,
UTC+11 through October; the PGNs' are UTC); then record.csv's book columns (scripts/opponents/collect.py).
"""
import collections, csv, datetime, glob, io, json, os, statistics, sys
import chess, chess.pgn, chess.polyglot

bot = sys.argv[1]
books = os.path.join(bot, "books")
MAIN, WIDE = os.path.join(books, "lichess-2200.bin"), os.path.join(books, "lichess-2200-wide.bin")
ME = "matthewcarven"
LOCAL = datetime.timedelta(hours=11)   # AEDT through 12-22 Oct

rows = [json.loads(l) for l in open(os.path.join(books, "ours.jsonl"), encoding="utf-8") if l.strip()]
latest = {}
first_seen = {}
for r in rows:
    t = datetime.datetime.strptime(r["when"], "%Y-%m-%d %H:%M") - LOCAL
    first_seen.setdefault(r["key"], t)
    latest[r["key"]] = r
print(f"lines {len(rows)}, positions {len(latest)}")
print("why:", collections.Counter(r["why"] for r in latest.values()))
print("by day learnt:", sorted(collections.Counter(r["when"][:10] for r in latest.values()).items()))
depths = [r["depth"] for r in latest.values() if r.get("depth")]
print(f"depth: median {statistics.median(depths)}, min {min(depths)}, max {max(depths)}")
print("move number:", sorted(collections.Counter(int(r["fen"].split()[5]) for r in latest.values()).items()))
same = sum(1 for r in latest.values() if r["why"] == "no book" and r["move"] == r["played"])
nb = sum(1 for r in latest.values() if r["why"] == "no book")
print(f"no-book entries whose learnt move is the move the bot played in the game: {same} of {nb}")
cps = [r["best_cp"] for r in latest.values()]
print("best_cp quartiles:", statistics.quantiles(cps, n=4))


def read_game(path):
    raw = open(path, "rb").read()
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError:
        text = raw.decode("cp1252")
    return chess.pgn.read_game(io.StringIO(text))


def in_book(path, board):
    with chess.polyglot.open_reader(path) as reader:
        return any(True for _ in reader.find_all(board))


games = 0
met = []          # (game id, ply, played == learnt, main book has a move now)
met_any = []      # learnt at any time, incl. after the game (repeats within the data)
for path in sorted(glob.glob(os.path.join(bot, "games", "*.pgn"))):
    g = read_game(path)
    if g is None or g.headers.get("Variant", "Standard") != "Standard" or "FEN" in g.headers:
        continue
    h = g.headers
    try:
        start = datetime.datetime.strptime(h["UTCDate"] + " " + h["UTCTime"], "%Y.%m.%d %H:%M:%S")
    except (KeyError, ValueError):
        continue
    games += 1
    us = chess.WHITE if h.get("White") == ME else chess.BLACK
    board = g.board()
    gid = h.get("Site", "").rsplit("/", 1)[-1]
    for move in g.mainline_moves():
        if len(board.move_stack) > 23:
            break
        if board.turn == us:
            key = chess.polyglot.zobrist_hash(board)
            if key in latest:
                r = latest[key]
                met_any.append((gid, len(board.move_stack), start, first_seen[key]))
                if first_seen[key] < start:
                    met.append((gid, len(board.move_stack), move.uci() == r["move"], in_book(MAIN, board)))
        board.push(move)
print(f"\ngames {games}")
print(f"our-move positions in games that were in ours.jsonl at any time: {len(met_any)} "
      f"(of which learnt before that game: {len(met)})")
print(f"  learnt before the game, bot played the learnt move: {sum(1 for m in met if m[2])}; "
      f"main book has a move there now: {sum(1 for m in met if m[3])}")
for m in met:
    print("  ", m)

# record.csv
rec = list(csv.DictReader(open(os.path.join(bot, "games", "record.csv"), encoding="utf-8")))
print(f"\nrecord.csv: {len(rec)} games; book_ours total {sum(int(r['book_ours'] or 0) for r in rec)} "
      f"in {sum(1 for r in rec if int(r['book_ours'] or 0))} games; book_main {sum(int(r['book_main'] or 0) for r in rec)}, "
      f"book_wide {sum(int(r['book_wide'] or 0) for r in rec)}")
for r in rec:
    if int(r["book_ours"] or 0):
        print("  ", r["id"], r["utc"], r["net"], r["opponent"], r["score"], "ours", r["book_ours"], "main", r["book_main"])
since = [r for r in rec if r["utc"] >= "2026-10-14 08:00"]
print(f"games since the learner started (~2026-10-14 07:47 UTC): {len(since)}")
