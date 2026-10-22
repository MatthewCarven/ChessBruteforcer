"""The "their every move, our best" table to 5 plies: how many of our positions need a search? Our move is stood in
by the 2200 book's most-played move (else the first legal move); theirs is every legal move, less those blocked by
Matthew's lost-material rule (prune.py's capture check, T = 3 or 1). Counts our positions by ply, both colours."""
import os, sys, runpy, io, contextlib
import chess, chess.polyglot as pg

HERE = os.path.dirname(os.path.abspath(__file__))
import subprocess


def main_checkout():
    """bot\\games and bot\\books aren't in git: from a worktree, the main checkout's."""
    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    if os.path.isdir(os.path.join(repo, "bot", "games")):
        return repo
    common = subprocess.run(["git", "-C", repo, "rev-parse", "--git-common-dir"], capture_output=True, text=True).stdout.strip()
    return os.path.dirname(os.path.abspath(os.path.join(repo, common)))


MAIN = main_checkout()
src = open(os.path.join(HERE, "prune.py"), encoding="utf-8").read().split("T = float")[0]
ns = {"__file__": os.path.join(HERE, "prune.py")}
exec(compile(src, "prune_head", "exec"), ns)
blocked = ns["blocked"]
book = pg.open_reader(os.path.join(MAIN, "bot", "books", "lichess-2200.bin"))


def ours(b):
    entries = sorted(book.find_all(b), key=lambda e: -e.weight)
    return entries[0].move if entries else next(iter(b.legal_moves))


for T in (3, 1):
    for colour in ("white", "black"):
        level = {pg.zobrist_hash(chess.Board()): chess.Board().fen()}
        counts, cut = [], 0
        for ply in range(6):
            our_turn = (ply % 2 == 0) == (colour == "white")
            nxt = {}
            if our_turn:
                counts.append((ply, len(level)))
            for fen in level.values():
                b = chess.Board(fen)
                moves = [ours(b)] if our_turn else list(b.legal_moves)
                for m in moves:
                    b.push(m)
                    if not our_turn and blocked(b, T):
                        cut += 1
                    else:
                        nxt.setdefault(pg.zobrist_hash(b), b.fen())
                    b.pop()
            level = nxt
        total = sum(c for _, c in counts)
        print(f"T={T} {colour}: our positions by ply {counts}, total {total:,}; their moves blocked {cut:,}", flush=True)
