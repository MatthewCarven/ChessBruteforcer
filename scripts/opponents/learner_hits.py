"""Does the bot ever meet a position its book learner has already worked out?  (P8, OPPONENTS.md, step 3.)

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/opponents/learner_hits.py <bot folder> <record.csv>

bot/learn.py writes a move for positions where no book had one (or the wide book's was poor) into
bot/books/ours.jsonl; lichess-bot plays it from ours.bin if the position comes again. The bot's log shows how often
that happened. This replays every game's first 12 moves of ours (the books' reach) and counts the positions the learner
had learnt before that game started: if the log's count is far below this, the book isn't being read; if both are
small, the positions just don't come back. Also: against a bot met before with the same colours, how far our moves
and theirs agreed with the last game, and whether that last game's first out-of-book position (where the learner
works) came up again.
"""
import csv
import io
import json
import os
import sys
from collections import defaultdict

import chess
import chess.pgn
import chess.polyglot

bot, record = sys.argv[1], sys.argv[2]
rows = list(csv.DictReader(open(record, encoding="utf-8")))
learnt = {}                                  # polyglot key -> when learnt (local 'YYYY-MM-DD HH:MM')
for line in open(os.path.join(bot, "books", "ours.jsonl"), encoding="utf-8"):
    e = json.loads(line)
    learnt.setdefault(int(e["key"]), e["when"])

pgn_of = {}
for name in os.listdir(os.path.join(bot, "games")):
    if name.endswith(".pgn"):
        pgn_of[name.rsplit(" - ", 1)[-1][:-4]] = os.path.join(bot, "games", name)

reached = known = 0
games_with = 0
last = {}                                    # (bot, colour) -> our keys of the last game, its first out-of-book key
again = again_first = pairs = 0
in_book = [0, 0]                             # of those: learnt at some time, learnt before that game
in_now = {"lichess-2200": 0, "lichess-2200-wide": 0}   # of those: in today's main and wide books
for r in rows:
    path = pgn_of.get(r["id"])
    if not path:
        continue
    game = chess.pgn.read_game(io.StringIO(open(path, encoding="utf-8", errors="replace").read()))
    us = chess.WHITE if r["colour"] == "white" else chess.BLACK
    board = game.board()
    keys, boards = [], []
    for ply, move in enumerate(game.mainline_moves()):
        if ply >= 24:
            break
        if board.turn == us:
            keys.append(chess.polyglot.zobrist_hash(board))
            boards.append(board.fen())
        board.push(move)
    start = r["log_start"][:16]
    hit = [k for k in keys if k in learnt and learnt[k] < start]
    reached += len(keys)
    known += len(hit)
    games_with += bool(hit)
    book = int(r["book_main"]) + int(r["book_ours"]) + int(r["book_wide"])
    first_out = keys[book] if book < len(keys) else None
    k2 = (r["opponent"], r["colour"])
    if k2 in last:
        pairs += 1
        prev_keys, prev_first = last[k2]
        again += bool(set(keys) & set(prev_keys[1:]))
        if prev_first is not None and prev_first in keys:
            again_first += 1
            in_book[0] += prev_first in learnt
            in_book[1] += prev_first in learnt and learnt[prev_first] < start
            fen = boards[keys.index(prev_first)]
            for name in ("lichess-2200", "lichess-2200-wide"):
                with chess.polyglot.open_reader(os.path.join(bot, "books", name + ".bin")) as reader:
                    in_now[name] += any(True for _ in reader.find_all(chess.Board(fen)))
    last[k2] = (keys, first_out)

print(f"{len(rows)} games: our positions in the first 12 moves {reached}; already learnt before the game {known}, "
      f"in {games_with} games")
print(f"against a bot met before with the same colours ({pairs} games): any of our positions after move 1 the same "
      f"as last time in {again}; last time's first out-of-book position reached again in {again_first}, "
      f"which the learner had in its book in {in_book[0]} (in time for that game in {in_book[1]}); in today's main "
      f"book {in_now['lichess-2200']}, wide book {in_now['lichess-2200-wide']}")
