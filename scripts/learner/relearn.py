"""Option (c) for the book learner (Matthew, 2026-10-22): where the bot's net thinks a move learnt with sf1 loses
--min-loss centipawns or more (50), the net's own move takes its place.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/learner/relearn.py <bot folder> <rescore.csv> [...]
        [--min-loss 50] [--eval h1024-3m] [--apply]

Reads rescore.py's files (any number; a position in several counts once) and the bot folder's books/ours.jsonl,
through the bot folder's own learn.py. A position qualifies when its loss is --min-loss or more and ours.jsonl's
latest row there still has the move that was scored (otherwise the learner has moved on, and it's skipped).
Without --apply it only lists them. With --apply it appends one row per position to ours.jsonl: why "re-learnt", the
re-score's best move at its depth, and the old move, its why and its loss in "was", "was_why" and "was_loss". It keeps
a copy of those rows in books/ours-relearnt-<date>.jsonl, and rebuilds ours.bin with the learner's own write_book.
The learner takes the latest row per position, so nothing is deleted. Removing the appended lines (they're the
copy's) undoes it, and running it twice adds nothing the second time. Apply it while no game can finish (the learner
appends after each game): the bot off, or the learner stopped.
"""
import csv, importlib.util, json, os, sys, time
import chess, chess.polyglot

bot = sys.argv[1]
files = [a for a in sys.argv[2:] if a.lower().endswith(".csv")]


def option(name, default, kind=int):
    return kind(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default


MIN_LOSS, EVAL_NAME, APPLY = option("--min-loss", 50), option("--eval", "h1024-3m", str), "--apply" in sys.argv

spec = importlib.util.spec_from_file_location("learn", os.path.join(bot, "learn.py"))
learn = importlib.util.module_from_spec(spec)
spec.loader.exec_module(learn)

scored = {}
for path in files:
    for r in csv.DictReader(open(path, encoding="utf-8")):
        scored[int(r["key"])] = r
latest = learn.learnt()
over = [r for r in scored.values() if int(r["loss"]) >= MIN_LOSS]
picked, moved_on = [], 0
when = time.strftime("%Y-%m-%d %H:%M")
for r in sorted(over, key=lambda r: -int(r["loss"])):
    key = int(r["key"])
    row = latest.get(key)
    if row is None or row["move"] != r["learnt"]:
        moved_on += 1
        continue
    board = chess.Board(row["fen"])
    best = chess.Move.from_uci(r["best"])
    if chess.polyglot.zobrist_hash(board) != key or best not in board.legal_moves:
        raise SystemExit(f"position {key}: the re-score's move {r['best']} doesn't fit {row['fen']}")
    picked.append({"why": "re-learnt", "best_cp": int(r["best_cp"]), "was": row["move"], "was_why": row["why"],
                   "was_loss": int(r["loss"]), "key": key, "polyglot": learn.polyglot_move(board, best),
                   "move": best.uci(), "san": board.san(best), "fen": row["fen"], "game": row.get("game"),
                   "played": row.get("played"), "depth": int(r["depth"]), "when": when, "eval": EVAL_NAME})

print(f"{len(scored)} positions re-scored; {len(over)} lose {MIN_LOSS} cp or more; {moved_on} of those already "
      f"changed in ours.jsonl (skipped); {len(picked)} to re-learn")
for p in picked[:12]:
    print(f"  {p['was']} -> {p['san']} ({p['was_loss']} cp) in {p['fen']}")
if APPLY and picked:
    lines = "".join(json.dumps(p) + "\n" for p in picked)
    with open(os.path.join(bot, "books", time.strftime("ours-relearnt-%Y-%m-%d.jsonl")), "a", encoding="utf-8") as out:
        out.write(lines)
    with open(learn.LEARNT, "a", encoding="utf-8") as out:
        out.write(lines)
    print(f"appended {len(picked)} rows to {learn.LEARNT}; ours.bin rebuilt: {learn.write_book()} positions")
elif APPLY:
    print("nothing to apply")
