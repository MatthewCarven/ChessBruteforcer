r"""BT1 (a) (BOOK-TABLES.md): Matthew's table exactly as he put it. "Run all the possibilities to 5 deep only the moves
we can make legally", and "any lost pieces block out the entries for up to 5 deep". The laptop's half, python-chess
from the bot's venv (bot\lichess-bot\.venv\Scripts\python.exe -I); the leaves are searched on the desktop by
table-a-search.py (table-a-desktop.ps1 drives it).

    table-a.py leaves <leaves .tsv>                          the leaves, shuffled: `<polyglot key hex>\t<fen>` a line
    table-a.py book <results dir> <depth> <out .bin> <summary .md>
                                                             the back-up, the book and its summary
    table-a.py compare <results dir> <results dir> <depth>   do two runs give the same answers? (move, score, nodes)

The tree is prune.py's at T = 3: every legal position to 5 half-moves (transpositions merged), and a position where a
capture-only search (pieces 1/3/3/5/9, checks answered) finds one side 3 pawns or more down is blocked: no children,
no book entry, no search. Two refinements, so that the back-up can't be fooled by the rule:
- A blocked position still counts for its parent, at the capture search's material score (+-300 cp or more). Dropped
  outright, a move that wins a piece (a knight fork with check reads as blocked: the forked side is a piece down)
  would vanish with the ones that lose one.
- Checkmate and stalemate are exact results (mate, 0), not blocked. prune.py's check calls a mated side "down", which
  would block the mating move itself (Fool's mate is at ply 4).
The leaves are the kept positions at ply 5 (715,940); each is searched to a fixed depth, one thread, Hash 64, no
endgame tables, h1536-3m. Their scores are backed up by minimax to ply 0, as the endgame tables back up theirs.

The book: our engine's best move in every kept position of plies 0-4, both colours (the back-up's), and in every
leaf (the leaf's own search), as a polyglot .bin of 16-byte entries (key, move, weight 1, learn 0), sorted by key, as
bot/learn.py writes ours.bin. A position reached at two plies (1.Nf3 Nf6 2.Ng1 Ng8 is the start again) keeps the move
from the shallower, which saw further.
"""
import collections
import ctypes
import glob
import os
import random
import statistics
import struct
import subprocess
import sys
import time

import chess
import chess.polyglot

ctypes.windll.kernel32.GetCurrentProcess.restype = ctypes.c_void_p
ctypes.windll.kernel32.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)

PLIES = 5
T = 3
MATE = 100000
SEED = 1023


def main_checkout():
    """bot\\books isn't in git: from a worktree, the main checkout's."""
    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    if os.path.isdir(os.path.join(repo, "bot", "books")):
        return repo
    common = subprocess.run(["git", "-C", repo, "rev-parse", "--git-common-dir"], capture_output=True, text=True).stdout.strip()
    return os.path.dirname(os.path.abspath(os.path.join(repo, common)))


# prune.py's capture check, as it is there.
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


def build():
    """Per ply: kept {key: fen}, blocked {key: cp for the side to move}, terminal {key: cp}, reached count."""
    start = chess.Board()
    kept = [{chess.polyglot.zobrist_hash(start): start.fen()}]
    blocked, terminal, reached = [{}], [{}], [1]
    t0 = time.time()
    for ply in range(1, PLIES + 1):
        nxt = {}
        for fen in kept[-1].values():
            b = chess.Board(fen)
            for m in list(b.legal_moves):
                b.push(m)
                nxt.setdefault(chess.polyglot.zobrist_hash(b), b.fen())
                b.pop()
        k_, b_, t_ = {}, {}, {}
        for key, fen in nxt.items():
            b = chess.Board(fen)
            if b.is_checkmate():
                t_[key] = -MATE
            elif b.is_stalemate():
                t_[key] = 0
            else:
                q = qsearch(b, -1000, 1000)
                if abs(q) >= T:
                    b_[key] = q * 100
                else:
                    k_[key] = fen
        kept.append(k_)
        blocked.append(b_)
        terminal.append(t_)
        reached.append(len(nxt))
        print(f"ply {ply}: {len(nxt):,} reached, {len(b_):,} blocked, {len(t_):,} mate or stalemate, {len(k_):,} kept "
              f"({time.time() - t0:.0f} s)", flush=True)
    return kept, blocked, terminal, reached


def leaves(args):
    out = args[0]
    kept, _, _, _ = build()
    keys = sorted(kept[PLIES])
    random.Random(SEED).shuffle(keys)
    with open(out + ".tmp", "w", encoding="utf-8", newline="\n") as f:
        for key in keys:
            f.write(f"{key:016x}\t{kept[PLIES][key]}\n")
    os.replace(out + ".tmp", out)
    print(f"{len(keys):,} leaves to {out} ({os.path.getsize(out):,} bytes)")


def read_results(folder, depth):
    """{key: (move, score text, nodes, ms)} at <depth> (a key answered twice: the answers must agree)."""
    found = {}
    for path in sorted(glob.glob(os.path.join(folder, "results-*.tsv"))):
        with open(path, encoding="utf-8") as f:
            for line in f:
                w = line.rstrip("\n").split("\t")
                if len(w) != 6 or w[1] != str(depth) or w[3].split()[0] not in ("cp", "mate"):
                    continue
                key, answer = int(w[0], 16), (w[2], w[3], int(w[4]), int(w[5]))
                if key in found and found[key][:3] != answer[:3]:
                    raise SystemExit(f"{w[0]} answered twice, differently: {found[key]} and {answer}")
                found[key] = answer
    return found


def cp(score):
    """An engine's score text as cp for the side to move; a mate in n as MATE less its plies."""
    kind, n = score.split()
    n = int(n)
    if kind == "cp":
        return n
    return MATE - (2 * n - 1) if n > 0 else -(MATE - 2 * -n)


def polyglot_move(board, move):
    """A move in Polyglot's 16 bits: castling as the king taking its own rook (make-book.py's)."""
    to = move.to_square
    if board.is_castling(move):
        to = chess.square(7 if chess.square_file(move.to_square) == 6 else 0, chess.square_rank(move.from_square))
    promotion = {None: 0, chess.KNIGHT: 1, chess.BISHOP: 2, chess.ROOK: 3, chess.QUEEN: 4}[move.promotion]
    return (chess.square_file(to) | chess.square_rank(to) << 3 | chess.square_file(move.from_square) << 6
            | chess.square_rank(move.from_square) << 9 | promotion << 12)


def pawns(v):
    if abs(v) > MATE // 2:
        return f"mate in {(MATE - abs(v) + 1) // 2}" if v > 0 else f"mated in {(MATE - abs(v)) // 2}"
    return f"{v / 100:+.2f}"


def book(args):
    folder, depth, out, summary = args[0], int(args[1]), args[2], args[3]
    t0 = time.time()
    results = read_results(folder, depth)
    kept, blocked, terminal, reached = build()
    missing = [k for k in kept[PLIES] if k not in results]
    if missing:
        raise SystemExit(f"{len(missing):,} of {len(kept[PLIES]):,} leaves have no answer at depth {depth} "
                         f"(e.g. {missing[0]:016x})")
    extra = len(results) - len(kept[PLIES])
    # The back-up: a position's value is the best of its moves' values, each the child's value negated.
    value = [dict() for _ in range(PLIES + 1)]
    best = [dict() for _ in range(PLIES + 1)]
    children = [dict() for _ in range(PLIES)]   # ply < 5: {key: [(move, value for the mover, kind)]}
    for key in kept[PLIES]:
        move, score, _, _ = results[key]
        value[PLIES][key] = cp(score)
        best[PLIES][key] = chess.Move.from_uci(move)
    for ply in range(PLIES - 1, -1, -1):
        for key, fen in kept[ply].items():
            b = chess.Board(fen)
            moves = []
            for m in sorted(b.legal_moves, key=lambda m: m.uci()):
                b.push(m)
                child = chess.polyglot.zobrist_hash(b)
                b.pop()
                if child in terminal[ply + 1]:
                    v, kind = terminal[ply + 1][child], "mate" if terminal[ply + 1][child] else "stalemate"
                elif child in blocked[ply + 1]:
                    v, kind = blocked[ply + 1][child], "blocked"
                else:
                    v, kind = value[ply + 1][child], "kept"
                v = -v
                if v > MATE // 2:
                    v -= 1
                elif v < -MATE // 2:
                    v += 1
                moves.append((m, v, kind))
            top = max(moves, key=lambda x: x[1])   # (the first of equals, in UCI order)
            value[ply][key], best[ply][key] = top[1], top[0]
            children[ply][key] = moves
    print(f"backed up ({time.time() - t0:.0f} s)", flush=True)

    # The book, shallowest ply first.
    entries = {}
    for ply in range(PLIES + 1):
        for key, move in best[ply].items():
            if key not in entries:
                entries[key] = polyglot_move(chess.Board(kept[ply][key]), move)
    with open(out + ".tmp", "wb") as f:
        for key in sorted(entries):
            f.write(struct.pack(">QHHI", key, entries[key], 1, 0))
    os.replace(out + ".tmp", out)
    print(f"{len(entries):,} entries to {out} ({os.path.getsize(out):,} bytes)", flush=True)

    # The summary.
    root = chess.Board()
    rk = chess.polyglot.zobrist_hash(root)
    nodes = sum(results[k][2] for k in kept[PLIES])
    ms = sum(results[k][3] for k in kept[PLIES])
    lines = [f"# BT1 (a): the full {PLIES}-ply table, depth {depth} at the leaves", "",
             f"Written by `table-a.py book` on {time.strftime('%Y-%m-%d %H:%M')} from {folder}.", "",
             "## The tree", "",
             "| ply | reached | blocked (a piece or more down) | mate or stalemate | kept |",
             "|---|---:|---:|---:|---:|"]
    for ply in range(PLIES + 1):
        lines.append(f"| {ply} | {reached[ply]:,} | {len(blocked[ply]):,} | {len(terminal[ply]):,} | {len(kept[ply]):,} |")
    lines += ["", f"Leaves searched: {len(kept[PLIES]):,} at depth {depth} ({extra:,} more answers in the folder than "
              f"leaves), {nodes:,} nodes, {ms / 3.6e6:,.1f} engine-hours ({ms / max(1, len(kept[PLIES])):,.0f} ms a "
              f"leaf, {nodes / max(1, ms):,.0f} knps).", "",
              f"The book: {len(entries):,} positions, one move each, {os.path.getsize(out):,} bytes.", ""]

    lines += ["## The values at the root", "",
              f"The start position: **{pawns(value[0][rk])}** for White (pawns, our engine's scale), best move "
              f"**{root.san(best[0][rk])}**.", "",
              "White's 20 first moves, each with Black's best reply in the table:", "",
              "| White's move | value for White | Black's best reply | White's best then |", "|---|---:|---|---|"]
    for m, v, kind in sorted(children[0][rk], key=lambda x: -x[1]):
        b = root.copy()
        san = b.san(m)
        b.push(m)
        k1 = chess.polyglot.zobrist_hash(b)
        reply = b.san(best[1][k1])
        b.push(best[1][k1])
        k2 = chess.polyglot.zobrist_hash(b)
        third = b.san(best[2][k2]) if k2 in best[2] else "-"
        lines.append(f"| {san} | {pawns(v)} | {reply} | {third} |")
    b = root.copy()
    pv = []
    for ply in range(PLIES + 1):
        k = chess.polyglot.zobrist_hash(b)
        if k not in best[ply]:
            break
        pv.append(b.san(best[ply][k]))
        b.push(best[ply][k])
    lines += ["", f"The table's main line (both sides' best, then the leaf's own move): {' '.join(pv)}", ""]

    # Against the human book: how often is the table's move the human book's most-played one?
    lines += ["## Against the human book (lichess-2200.bin)", "",
              "In each kept position the human book has a move for, is the table's move the human book's most-played "
              f"one (the largest weight)? At plies 0-{PLIES - 1} the table also has a value for every move, so the human move's "
              "cost is the table's own: its best move's value less the human move's.", "",
              "| ply | positions | in the human book | same move | human move within 20 cp | within 50 cp | median cost of the human move |",
              "|---|---:|---:|---:|---:|---:|---:|"]
    with chess.polyglot.open_reader(os.path.join(main_checkout(), "bot", "books", "lichess-2200.bin")) as human:
        for ply in range(PLIES + 1):
            n = same = w20 = w50 = 0
            costs = []
            for key, fen in kept[ply].items():
                b = chess.Board(fen)
                found = list(human.find_all(b))
                if not found:
                    continue
                top = max(found, key=lambda e: e.weight).move
                n += 1
                same += top == best[ply][key]
                if ply < PLIES:
                    byvalue = {m: v for m, v, _ in children[ply][key]}
                    cost = value[ply][key] - byvalue[top]
                    costs.append(cost)
                    w20 += cost <= 20
                    w50 += cost <= 50
            share = (lambda x: f"{x / n:.0%}") if n else (lambda x: "-")
            lines.append(f"| {ply} | {len(kept[ply]):,} | {n:,} | {share(same)} | "
                         + (f"{share(w20)} | {share(w50)} | {statistics.median(costs):.0f} cp |" if costs else "- | - | - |"))
    lines.append("")
    with open(summary, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))
    print("\n".join(lines))
    print(f"({time.time() - t0:.0f} s)")


def compare(args):
    a, b, depth = read_results(args[0], int(args[2])), read_results(args[1], int(args[2])), int(args[2])
    common = sorted(set(a) & set(b))
    differ = [k for k in common if a[k][:3] != b[k][:3]]
    print(f"{len(common):,} leaves in both at depth {depth}: {len(common) - len(differ):,} the same (move, score, nodes), "
          f"{len(differ):,} different")
    for k in differ[:10]:
        print(f"  {k:016x}: {a[k]} against {b[k]}")
    if common:
        ta, tb = sum(a[k][3] for k in common), sum(b[k][3] for k in common)
        print(f"time on those: {ta / 1000:,.1f} s against {tb / 1000:,.1f} s ({tb / max(1, ta):.2f}x)")
    return 1 if differ else 0


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else ""
    if command not in ("leaves", "book", "compare"):
        raise SystemExit(__doc__)
    sys.exit({"leaves": leaves, "book": book, "compare": compare}[command](sys.argv[2:]) or 0)
