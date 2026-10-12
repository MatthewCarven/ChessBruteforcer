"""An opening book from a Lichess month: what strong players played, and how it scored, as a Polyglot .bin.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/make-book.py <in .pgn.zst> <out .bin>
        [--min-elo 2200] [--plies 24] [--min-games 10] [--min-score 0.40] [--max-bytes N]

Games kept: rated, from the standard start, a result, both players rated --min-elo or more, and
blitz or slower (base + 40 x increment >= 180 s: bullet moves are rushed). For each of their
first --plies positions, the move played and how the game went for the side that played it.
A move goes in the book if it was played --min-games times or more and scored --min-score or
better for the side playing it (a draw is half). Its weight is 2 x wins + draws (Polyglot
make-book's rule), scaled within the position so the largest fits 65,535: lichess-bot's
"weighted_random" then plays the popular, successful moves most often.

Needs python-chess (the bot's venv has it) for the Polyglot keys. Reads across the file's
Zstandard frames and stops at a cut (a partial download); --max-bytes reads only the start.
"""
import array, io, re, struct, sys, time
from collections import defaultdict
from compression import zstd
import chess, chess.polyglot

args = sys.argv[1:]
def option(name, default, kind):
    if name in args:
        i = args.index(name)
        value = kind(args[i + 1])
        del args[i:i + 2]
        return value
    return default
min_elo = option("--min-elo", 2200, int)
plies = option("--plies", 24, int)
min_games = option("--min-games", 10, int)
min_score = option("--min-score", 0.40, float)
max_bytes = option("--max-bytes", None, int)
src, dst = args
COMMENT = re.compile(r"\{[^}]*\}")


class Limited(io.RawIOBase):
    """The first `limit` bytes of a file."""
    def __init__(self, raw, limit):
        self.raw, self.left = raw, limit
    def readable(self):
        return True
    def readinto(self, buffer):
        n = min(len(buffer), self.left)
        if n <= 0:
            return 0
        got = self.raw.readinto(memoryview(buffer)[:n])
        self.left -= got
        return got


def polyglot_move(board, move):
    """A move in Polyglot's 16 bits: castling as the king taking its own rook."""
    to = move.to_square
    if board.is_castling(move):
        to = chess.square(7 if chess.square_file(move.to_square) == 6 else 0, chess.square_rank(move.from_square))
    promotion = {None: 0, chess.KNIGHT: 1, chess.BISHOP: 2, chess.ROOK: 3, chess.QUEEN: 4}[move.promotion]
    return (chess.square_file(to) | chess.square_rank(to) << 3 | chess.square_file(move.from_square) << 6
            | chess.square_rank(move.from_square) << 9 | promotion << 12)


# Counts per (position key, move): an index into three parallel arrays (wins, draws, losses for the mover).
index = {}
wins, draws, losses = array.array("I"), array.array("I"), array.array("I")
games = kept = bad = 0
start = time.time()


def finish(tags, movetext):
    """Count one game's opening if it qualifies."""
    global kept, bad
    t = dict(tags)
    if "FEN" in t or "Rated" not in t.get("Event", "") or t.get("Result") not in ("1-0", "0-1", "1/2-1/2"):
        return
    try:
        if int(t.get("WhiteElo", 0)) < min_elo or int(t.get("BlackElo", 0)) < min_elo:
            return
        base, inc = (int(x) for x in t.get("TimeControl", "-").split("+"))
    except ValueError:
        return
    if base + 40 * inc < 180:
        return
    white_score = {"1-0": 2, "1/2-1/2": 1, "0-1": 0}[t["Result"]]
    board = chess.Board()
    played = 0
    for token in COMMENT.sub(" ", movetext).split():   # comments ({ [%clk ...] }) out first
        if token[-1] == "." or token[0] == "$" or token in ("1-0", "0-1", "1/2-1/2", "*"):
            continue
        if played == plies:
            break
        try:
            move = board.parse_san(token.rstrip("?!"))   # analysed games mark moves: f4?!, d5??
        except ValueError:
            bad += 1
            return
        key = chess.polyglot.zobrist_hash(board) << 16 | polyglot_move(board, move)
        score = white_score if board.turn == chess.WHITE else 2 - white_score
        i = index.get(key)
        if i is None:
            i = index[key] = len(wins)
            wins.append(0); draws.append(0); losses.append(0)
        if score == 2: wins[i] += 1
        elif score == 1: draws[i] += 1
        else: losses[i] += 1
        board.push(move)
        played += 1
    kept += 1
    if len(index) > PRUNE_AT:
        prune()


PRUNE_AT = 4_000_000
pruned = 0


def prune():
    """Drop the position-moves seen only once so far (most of the deep ones: games part by move 10 or so),
    keeping memory to a few hundred MB for a whole month. A move that ends up in the book (--min-games,
    10) loses at most the one game it had when it was dropped, and only if it was dropped at all."""
    global index, wins, draws, losses, pruned
    keep = {}
    w2, d2, l2 = array.array("I"), array.array("I"), array.array("I")
    for key, i in index.items():
        if wins[i] + draws[i] + losses[i] >= 2:
            keep[key] = len(w2)
            w2.append(wins[i]); d2.append(draws[i]); l2.append(losses[i])
    pruned += len(index) - len(keep)
    index, wins, draws, losses = keep, w2, d2, l2


source = open(src, "rb")
if max_bytes:
    source = Limited(source, max_bytes)
tags, movetext = [], []
with zstd.open(source, "rb") as packed:
    text = io.TextIOWrapper(packed, encoding="utf-8", newline="\n")
    try:
        for line in text:
            if line.startswith("["):
                if movetext:
                    games += 1
                    finish(tags, " ".join(movetext))
                    tags, movetext = [], []
                    if games % 1_000_000 == 0:
                        print(f"{games:,} games, {kept:,} kept, {len(wins):,} position-moves, {time.time() - start:.0f}s",
                              file=sys.stderr, flush=True)
                name, _, value = line.strip()[1:-1].partition(" ")
                tags.append((name, value.strip('"')))
            elif line.strip() and tags:
                movetext.append(line.strip())
        if movetext:
            games += 1
            finish(tags, " ".join(movetext))
    except (EOFError, zstd.ZstdError) as e:
        print(f"stopped at the cut ({e}); the game it was in dropped", file=sys.stderr)

# Choose the moves and write the book: entries sorted by key, then by weight (largest first).
by_position = defaultdict(list)
for key, i in index.items():
    n = wins[i] + draws[i] + losses[i]
    if n >= min_games and (wins[i] + draws[i] / 2) / n >= min_score:
        by_position[key >> 16].append((key & 0xFFFF, 2 * wins[i] + draws[i]))
entries = 0
with open(dst, "wb") as out:
    for position in sorted(by_position):
        moves = by_position[position]
        top = max(weight for _, weight in moves)
        scale = min(1.0, 65535 / top) if top else 1.0
        for move, weight in sorted(moves, key=lambda m: -m[1]):
            out.write(struct.pack(">QHHI", position, move, max(1, int(weight * scale)), 0))
            entries += 1
print(f"{games:,} games read, {kept:,} kept (both {min_elo}+, rated, blitz or slower), {bad} with an unreadable move; "
      f"{len(wins):,} position-moves counted at the end ({pruned:,} seen once dropped along the way); book: {entries:,} moves in {len(by_position):,} positions; "
      f"{time.time() - start:.0f}s")
