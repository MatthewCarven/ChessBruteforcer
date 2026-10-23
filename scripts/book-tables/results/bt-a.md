# BT1 (a): the full 5-ply table, depth 8 at the leaves

Written by `table-a.py book` on 2026-10-23 21:37 from C:\Users\Admin\Desktop\-=Programming=-\Chess Bruteforcer\games\book-table-a\d8.

**In short.** Every legal line of 5 half-moves that doesn't lose a piece (prune.py's check at T = 3) was searched
to depth 8 by our engine (h1536-3m, one thread, Hash 64, no endgame tables), on the desktop's CPU from 02:10 to
13:36 on 2026-10-23 (on the 128-bit SSE build from 06:25, with the same answers). Those values were backed up by
minimax to the start position. White's best move is **e4 at +0.26**. The table's main line is a Scotch (e4 e5
Nf3 Nc6 d4 exd4), and its order of White's first moves looks sane: e4, d4 and Nf3 come out on top, and g4 is
worst at -1.01.

Against the human book, the table picks the 2200+ players' most-played move in only 37-51% of the positions both
books have. But the move it doesn't pick is cheap: by the table's own values, the human move is within 20 cp of
its best 72-82% of the time, within 50 cp 92-96% of the time, and costs a median of 0-4 cp. Gaps that small are
probably within a depth-8 search's noise (not measured). So "same move" understates the agreement, and much of the
other 49-63% is near-ties that went the other way.

Most of the table is positions people never reach. The human book has only 3,286 of its 67,984 positions at
ply 4, and 6,329 of its 715,940 leaves. The match with the self-play-book session decides whether the book is
worth playing from. Its file is games\book-table-a\table-a-d8.bin in the main checkout (not in git, and not in
bot\books).

## The tree

| ply | reached | blocked (a piece or more down) | mate or stalemate | kept |
|---|---:|---:|---:|---:|
| 0 | 1 | 0 | 0 | 1 |
| 1 | 20 | 0 | 0 | 20 |
| 2 | 400 | 0 | 0 | 400 |
| 3 | 5,362 | 131 | 0 | 5,231 |
| 4 | 70,612 | 2,624 | 4 | 67,984 |
| 5 | 782,837 | 66,799 | 98 | 715,940 |

Leaves searched: 715,940 at depth 8 (0 more answers in the folder than leaves), 120,578,383,817 nodes, 75.8 engine-hours (381 ms a leaf, 442 knps).

The book: 788,056 positions, one move each, 12,608,896 bytes.

## The values at the root

The start position: **+0.26** for White (pawns, our engine's scale), best move **e4**.

White's 20 first moves, each with Black's best reply in the table:

| White's move | value for White | Black's best reply | White's best then |
|---|---:|---|---|
| e4 | +0.26 | e5 | Nf3 |
| d4 | +0.23 | d5 | c4 |
| Nf3 | +0.23 | d5 | d4 |
| e3 | +0.16 | d6 | d4 |
| c4 | +0.07 | e5 | g3 |
| c3 | +0.06 | Nf6 | d4 |
| g3 | +0.02 | d5 | Nf3 |
| b3 | -0.04 | e5 | Bb2 |
| d3 | -0.04 | d5 | Nf3 |
| Nc3 | -0.07 | d5 | d4 |
| a3 | -0.08 | c5 | c4 |
| h3 | -0.15 | e5 | e3 |
| b4 | -0.20 | d5 | Bb2 |
| a4 | -0.27 | e5 | e4 |
| f4 | -0.27 | d5 | e3 |
| h4 | -0.37 | d5 | c3 |
| Nh3 | -0.59 | e5 | c3 |
| f3 | -0.67 | e5 | Nc3 |
| Na3 | -0.69 | e5 | d4 |
| g4 | -1.01 | d5 | Bg2 |

The table's main line (both sides' best, then the leaf's own move): e4 e5 Nf3 Nc6 d4 exd4

## Against the human book (lichess-2200.bin)

In each kept position the human book has a move for, is the table's move the human book's most-played one (the largest weight)? At plies 0-4 the table also has a value for every move, so the human move's cost is the table's own: its best move's value less the human move's.

| ply | positions | in the human book | same move | human move within 20 cp | within 50 cp | median cost of the human move |
|---|---:|---:|---:|---:|---:|---:|
| 0 | 1 | 1 | 100% | 100% | 100% | 0 cp |
| 1 | 20 | 20 | 45% | 100% | 100% | 0 cp |
| 2 | 400 | 308 | 37% | 72% | 92% | 4 cp |
| 3 | 5,231 | 1,317 | 42% | 82% | 96% | 1 cp |
| 4 | 67,984 | 3,286 | 44% | 81% | 95% | 0 cp |
| 5 | 715,940 | 6,329 | 51% | - | - | - |
