"""A check of table-b.py's book from outside its walk: play the book against every reply of theirs and count.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/book-tables/table-b-check.py <book .bin> <log .jsonl>
        [--plies 5] [--depth 18]

From the start (our White positions) and after each of White's first moves (our Black ones): at our turns the book
must have exactly one move, and it must be legal; at theirs every legal reply is followed, less those prune.py's
capture check blocks at T = 3, until --plies. Any of our positions with no entry, or more than one, is a hole. Then
the log: no search twice, and every human move the book stores is within 20 cp of the best by the log's own scores.
"""
import json, os, sys
import chess, chess.polyglot as pg

HERE = os.path.dirname(os.path.abspath(__file__))
_src = open(os.path.join(HERE, "prune.py"), encoding="utf-8").read().split("T = float")[0]
_ns = {"__file__": os.path.join(HERE, "prune.py")}
exec(compile(_src, "prune_head", "exec"), _ns)
blocked, MAIN = _ns["blocked"], _ns["MAIN"]

args = sys.argv[1:]
PLIES = int(args[args.index("--plies") + 1]) if "--plies" in args else 5
DEPTH = int(args[args.index("--depth") + 1]) if "--depth" in args else 18
book_path, log_path = args[0], args[1]
book = pg.open_reader(book_path)
human = pg.open_reader(os.path.join(MAIN, "bot", "books", "lichess-2200.bin"))
counts, holes, seen = {}, [], set()


def ours(board, ply):
    key = pg.zobrist_hash(board)
    if key in seen:
        return
    seen.add(key)
    counts[(board.turn, ply)] = counts.get((board.turn, ply), 0) + 1
    entries = list(book.find_all(board))
    if len(entries) != 1 or entries[0].move not in board.legal_moves:
        holes.append((board.fen(), [e.move.uci() for e in entries]))
        return
    if ply + 2 >= PLIES:
        return
    board.push(entries[0].move)
    for r in list(board.legal_moves):
        board.push(r)
        if not board.is_game_over() and not blocked(board, 3):
            ours(board, ply + 2)
        board.pop()
    board.pop()


start = chess.Board()
ours(start, 0)
for m in list(start.legal_moves):
    start.push(m)
    if not blocked(start, 3):
        ours(start, 1)
    start.pop()
total = sum(counts.values())
print(f"book {book_path}: {os.path.getsize(book_path) // 16} entries; walk reaches {total} of our "
      f"positions: " + ", ".join(f"{'white' if t else 'black'} ply {p}: {c}" for (t, p), c in sorted(counts.items(),
                                                                                                  key=lambda x: x[0][1])))
print(f"holes (no entry, several, or illegal): {len(holes)}" + (f", first {holes[:3]}" if holes else ""))

rows = [json.loads(line) for line in open(log_path, encoding="utf-8") if line.strip()]
rows = [r for r in rows if r.get("table_depth") == DEPTH]
ids = [(r["key"], r["kind"], r["move"]) for r in rows]
print(f"log: {len(rows)} searches at table depth {DEPTH}, {len(ids) - len(set(ids))} twice")
best = {r["key"]: r for r in rows if r["kind"] == "best"}
after = {(r["key"], r["move"]): r for r in rows if r["kind"] == "after"}
stored_human = over = in_book_best = 0
for key, r in best.items():
    board = chess.Board(r["fen"])
    entries = list(book.find_all(board))
    if not entries:
        continue
    move = entries[0].move.uci()
    if move == r["bestmove"]:
        in_book_best += move in [e.move.uci() for e in human.find_all(board)]
        continue
    stored_human += 1
    a = after.get((key, move))
    if a is None or r["cp"] - (-a["cp"]) > 20:
        over += 1
print(f"book moves other than our best: {stored_human} (each a human move within 20 cp by the log: "
      f"{stored_human - over} of them); our best and in the human book: {in_book_best}")
