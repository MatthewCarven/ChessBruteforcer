"""Every legal position to 5 plies: distinct positions per ply (transpositions merged, by polyglot key), and the
legal moves per position. Also how many of each ply's positions the 2200 book and the bot's games ever reach.
No engine: python-chess only."""
import csv, os, time, statistics
import chess, chess.polyglot

import subprocess


def main_checkout():
    """bot\\games and bot\\books aren't in git: from a worktree, the main checkout's."""
    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    if os.path.isdir(os.path.join(repo, "bot", "games")):
        return repo
    common = subprocess.run(["git", "-C", repo, "rev-parse", "--git-common-dir"], capture_output=True, text=True).stdout.strip()
    return os.path.dirname(os.path.abspath(os.path.join(repo, common)))


MAIN = main_checkout()
t0 = time.time()
frontier = {chess.polyglot.zobrist_hash(chess.Board()): chess.Board().fen()}
levels = [frontier]
for ply in range(5):
    nxt, moves = {}, []
    for fen in frontier.values():
        b = chess.Board(fen)
        legal = list(b.legal_moves)
        moves.append(len(legal))
        for m in legal:
            b.push(m)
            k = chess.polyglot.zobrist_hash(b)
            if k not in nxt:
                nxt[k] = b.fen()
            b.pop()
    print(f"ply {ply}: {len(frontier):,} positions, legal moves mean {statistics.mean(moves):.1f}; "
          f"ply {ply + 1}: {len(nxt):,} positions  ({time.time() - t0:.0f} s)", flush=True)
    frontier = nxt
    levels.append(frontier)

# How many of them the 2200 book holds (any ply), and the bot's network-era games reach.
book = set()
data = open(os.path.join(MAIN, "bot", "books", "lichess-2200.bin"), "rb").read()
for i in range(0, len(data), 16):
    book.add(int.from_bytes(data[i:i + 8], "big"))
games = [set() for _ in range(6)]
for r in csv.DictReader(open(os.path.join(MAIN, "bot", "games", "record.csv"), encoding="utf-8")):
    b = chess.Board()
    for ply, san in enumerate(r["line"].split()[:6]):
        games[ply].add(chess.polyglot.zobrist_hash(b))
        try:
            b.push_san(san)
        except ValueError:
            break
for ply, level in enumerate(levels):
    inbook = sum(1 for k in level if k in book)
    played = len(games[ply]) if ply < 6 else 0
    print(f"ply {ply}: {len(level):,} positions; with a move in the 2200 book {inbook:,} ({inbook / len(level):.2%}); "
          f"reached in the bot's 622 games {played}")
