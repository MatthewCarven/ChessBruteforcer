"""Matthew's rule (2026-10-22 23:3x): "any lost pieces block out the entries for up to 5 deep".

Every legal position to 5 plies, but a position where one side has lost material is blocked: it gets no children
and no book entry. "Lost" = a capture-only search (pieces 1/3/3/5/9, both sides take or stand pat, checks
answered) says the balance is off by T or more. Two thresholds: T = 3 (a piece) and T = 1 (any pawn: gambits go).
Counts per ply, against the full tree, and how many of the 2200 book's own positions each rule would block.
python-chess only, below-normal priority."""
import ctypes, os, sys, time, statistics
import chess, chess.polyglot

ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)
import subprocess


def main_checkout():
    """bot\\games and bot\\books aren't in git: from a worktree, the main checkout's."""
    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    if os.path.isdir(os.path.join(repo, "bot", "games")):
        return repo
    common = subprocess.run(["git", "-C", repo, "rev-parse", "--git-common-dir"], capture_output=True, text=True).stdout.strip()
    return os.path.dirname(os.path.abspath(os.path.join(repo, common)))


MAIN = main_checkout()
VALUE = {chess.PAWN: 1, chess.KNIGHT: 3, chess.BISHOP: 3, chess.ROOK: 5, chess.QUEEN: 9, chess.KING: 0}


def material(b):
    s = 0
    for piece, v in VALUE.items():
        s += v * (len(b.pieces(piece, b.turn)) - len(b.pieces(piece, not b.turn)))
    return s


def qsearch(b, alpha, beta, evasions=2):
    if b.is_check():
        if evasions == 0:
            return material(b)
        moves = list(b.legal_moves)
        if not moves:
            return -100
        best = -1000
        for m in moves:
            b.push(m)
            v = -qsearch(b, -beta, -alpha, evasions - 1)
            b.pop()
            if v > best:
                best = v
            if v > alpha:
                alpha = v
            if alpha >= beta:
                break
        return best
    stand = material(b)
    if stand >= beta:
        return stand
    alpha = max(alpha, stand)
    caps = sorted(b.generate_legal_captures(),
                  key=lambda m: -(VALUE.get(b.piece_type_at(m.to_square), 1) * 10 - VALUE[b.piece_type_at(m.from_square)]))
    for m in caps:
        b.push(m)
        v = -qsearch(b, -beta, -alpha, evasions)
        b.pop()
        if v >= beta:
            return v
        alpha = max(alpha, v)
    return alpha


def blocked(b, t):
    return abs(qsearch(b, -1000, 1000)) >= t


T = float(sys.argv[1]) if len(sys.argv) > 1 else 3
t0 = time.time()
start = chess.Board()
frontier = {chess.polyglot.zobrist_hash(start): start.fen()}
for ply in range(5):
    nxt, cut = {}, 0
    for fen in frontier.values():
        b = chess.Board(fen)
        for m in list(b.legal_moves):
            b.push(m)
            k = chess.polyglot.zobrist_hash(b)
            if k not in nxt:
                nxt[k] = b.fen()
            b.pop()
    kept = {}
    for k, fen in nxt.items():
        if blocked(chess.Board(fen), T):
            cut += 1
        else:
            kept[k] = fen
    print(f"T={T:g} ply {ply + 1}: {len(nxt):,} reached, {cut:,} blocked ({cut / len(nxt):.1%}), {len(kept):,} kept "
          f"({time.time() - t0:.0f} s)", flush=True)
    frontier = kept

# The 2200 book's lines: how often does the rule block a position the book plays into, in the first 10 plies?
import random, chess.polyglot as pg
random.seed(3)
hit = total = 0
with pg.open_reader(os.path.join(MAIN, "bot", "books", "lichess-2200.bin")) as book:
    for _ in range(2000):
        b = chess.Board()
        for ply in range(10):
            entries = list(book.find_all(b))
            if not entries:
                break
            b.push(random.choices([e.move for e in entries], [e.weight for e in entries])[0])
            total += 1
            if blocked(b, T):
                hit += 1
print(f"T={T:g}: 2200-book positions in random walks (10 plies): {total:,}, blocked {hit:,} ({hit / max(1, total):.2%})")
