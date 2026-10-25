"""Time losses in a match's PGN: per side, how many, the pieces on the board, and how far over the clock it went.

python scripts\\time-losses.py <match .pgn>     (needs python-chess: the bot's venv python has it)

The match tool calls a game lost on time when a move takes longer than the time left plus 100 ms; its Termination
tag says "<name> lost on time (used X ms with Y ms left)".
"""
import collections
import re
import sys

import chess.pgn

LOST = re.compile(r"^(?P<name>.+) lost on time \(used (?P<used>\d+) ms with (?P<left>\d+) ms left\)$")

games = collections.Counter()
losses = collections.defaultdict(list)
with open(sys.argv[1], encoding="utf-8") as f:
    while (game := chess.pgn.read_game(f)) is not None:
        games[game.headers["White"]] += 1
        games[game.headers["Black"]] += 1
        m = LOST.match(game.headers.get("Termination", ""))
        if not m:
            continue
        board = game.end().board()
        losses[m["name"]].append((len(board.piece_map()), int(m["used"]) - int(m["left"]), int(m["left"]),
                                  board.fullmove_number, game.headers["Round"]))

total = sum(games.values()) // 2
print(f"{total} games; {sum(len(v) for v in losses.values())} lost on time")
for name in games:
    rows = sorted(losses.get(name, []))
    print(f"{name}: {len(rows)} of {games[name]} games on time")
    for pieces, over, left, move, rnd in rows:
        print(f"  round {rnd:>5}: {pieces:2} pieces, move {move:3}, over by {over:4} ms with {left:4} ms left")
    if rows:
        print(f"  16 or fewer pieces: {sum(r[0] <= 16 for r in rows)}; median over {sorted(r[1] for r in rows)[len(rows) // 2]} ms")
