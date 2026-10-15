# Bench positions from the bot's games: every 8th game (sorted by file name), one position from
# each, at ply 30, 40 or 50 in turn (back 10 plies at a time if that's past the end, in check,
# or under 8 pieces).  Usage: python -I pick-positions.py <games dir> <out file>
import sys, os, io
import chess, chess.pgn

games_dir, out_path = sys.argv[1], sys.argv[2]
start = int(sys.argv[3]) if len(sys.argv) > 3 else 0     # (a tuning set: other games, e.g. start 4 stride 30)
stride = int(sys.argv[4]) if len(sys.argv) > 4 else 8
names = sorted(n for n in os.listdir(games_dir) if n.endswith(".pgn"))
picked = []
for k, name in enumerate(names[start::stride]):
    raw = open(os.path.join(games_dir, name), "rb").read()
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError:
        text = raw.decode("cp1252")
    game = chess.pgn.read_game(io.StringIO(text))
    if game is None:
        continue
    boards = []
    board = game.board()
    for move in game.mainline_moves():
        board.push(move)
        boards.append(board.copy(stack=False))
    ply = 30 + 10 * (k % 3)
    while ply >= 10:
        if ply <= len(boards):
            b = boards[ply - 1]
            if not b.is_check() and len(b.piece_map()) >= 8 and not b.is_game_over():
                picked.append((name, ply, b.fen()))
                break
        ply -= 10
with open(out_path, "w", newline="\n") as out:
    out.write(f"# {len(picked)} positions from bot/games, every 8th game, ply 30/40/50 (pick-positions.py)\n")
    for name, ply, fen in picked:
        out.write(f"# {name}, ply {ply}\n{fen}\n")
print(len(picked), "positions")
