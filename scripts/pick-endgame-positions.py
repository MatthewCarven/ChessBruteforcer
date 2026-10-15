# Bench positions from the bot's games whose search reaches the 5-piece tables: the first position
# in each game with 6 or 7 pieces on the board and the bot (matthewcarven) to move, not in check,
# no castling rights.  Then `count` of those spread evenly over the games (sorted by file name),
# skipping a material already taken while there are others to take.
# Usage: python -I pick-endgame-positions.py <games dir> <out file> [count]
import sys, os, io
import chess, chess.pgn

games_dir, out_path = sys.argv[1], sys.argv[2]
count = int(sys.argv[3]) if len(sys.argv) > 3 else 20
BOT = "matthewcarven"


def material(board):
    # e.g. KRPPvKR: white's pieces, then black's, strongest first
    order = "KQRBNP"
    sides = []
    for colour in (chess.WHITE, chess.BLACK):
        pieces = [p.symbol().upper() for p in board.piece_map().values() if p.color == colour]
        sides.append("".join(sorted(pieces, key=order.index)))
    return "v".join(sides)


names = sorted(n for n in os.listdir(games_dir) if n.endswith(".pgn"))
candidates = []
for name in names:
    raw = open(os.path.join(games_dir, name), "rb").read()
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError:
        text = raw.decode("cp1252")
    game = chess.pgn.read_game(io.StringIO(text))
    if game is None:
        continue
    bot = chess.WHITE if game.headers.get("White") == BOT else chess.BLACK if game.headers.get("Black") == BOT else None
    if bot is None:
        continue
    board = game.board()
    for ply, move in enumerate(game.mainline_moves(), 1):
        board.push(move)
        pieces = len(board.piece_map())
        if pieces < 6:
            break
        if (pieces <= 7 and board.turn == bot and not board.is_check() and not board.has_castling_rights(chess.WHITE)
                and not board.has_castling_rights(chess.BLACK) and not board.is_game_over()):
            candidates.append((name, ply, pieces, material(board), board.fen()))
            break

# Evenly spaced over the games, a material once while others remain.
picked, seen = [], set()
step = max(1, len(candidates) / count)
for pass_ in range(2):
    k = 0.0
    while k < len(candidates) and len(picked) < count:
        c = candidates[int(k)]
        if c not in picked and (pass_ == 1 or c[3] not in seen):
            picked.append(c)
            seen.add(c[3])
        k += step
picked.sort(key=lambda c: names.index(c[0]))
with open(out_path, "w", newline="\n") as out:
    out.write(f"# {len(picked)} positions from bot/games (of {len(candidates)} games reaching 6-7 pieces with the bot to move): "
              f"the first such position in each (pick-endgame-positions.py)\n")
    for name, ply, pieces, mat, fen in picked:
        out.write(f"# {name}, ply {ply}, {pieces} pieces, {mat}\n{fen}\n")
print(len(candidates), "candidates,", len(picked), "picked")
for c in picked:
    print(c[2], c[3], c[0])
