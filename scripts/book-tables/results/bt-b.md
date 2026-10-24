# BT1 (b): the 5-ply "all of theirs, our best" book

Built 2026-10-24 (12:52-18:01 on the laptop, 8 engines) by `scripts/book-tables/table-b.py`, decision BT in
[BOOK-TABLES.md](../../../BOOK-TABLES.md). The book is [table-b.bin](table-b.bin) (Polyglot, 811 entries, one move
each); every search is in [table-b.jsonl](table-b.jsonl); the figures below are
[table-b-summary.txt](table-b-summary.txt) (`table-b.py <log> --summary`) plus two looks at the log. It is a test
book for the measurement (BT4), **not** a book for the bot: it isn't in `bot\books`.

## What it is

For the first 5 half-moves (White's moves 1-3, Black's 1-2), every move the opponent can legally make, and at each of
our turns one move: our engine's best by a depth-18 search.

- Their replies that lose a piece (prune.py's capture check, T = 3) aren't followed: 16 positions.
- Where the human book (`lichess-2200.bin`) has a move within 20 cp of our best, the human move is stored instead
  (BT3 (a)), so the bot leaves the table on familiar ground. Up to its 2 most-played moves are tried, each scored by a
  depth-17 search of the position after it.
- The engine is the Release build of cb30183 with h1536-3m.nnue, 1 thread and Hash 64 a search, no endgame tables, a
  fresh game for every search (so the same search gives the same answer).

## The run

| | |
|---|---:|
| our positions | **811** (White 400, Black 411) |
| by ply | White's move 1: 1, Black's 1: 20, White's 2: 20, Black's 2: 391, White's 3: 379 |
| searches | 860 (811 best moves at depth 18, 49 human moves at 17) |
| a search (wall) | median 167 s; 10% under 90 s, 10% over 292 s; longest 33 min |
| CPU | **39.3 core-h** (38.4 best moves, 0.9 human moves), against ~85 estimated |
| wall time | 5 h 09 min on 8 engines for the last 841 searches (19 ran in the earlier slowdown tests) |
| the check (`table-b-check.py`) | 811 of 811 positions reached, **0 holes**, no search twice, all 32 human moves within 20 cp |

It took half the estimate. A depth-18 search took ~2.8 min against the ~4.5 min guessed, and the human-move check
cost 2% instead of ~50%: in most positions our best already is a human move, or the human book has no move there.

## Our move

| where the stored move comes from | positions | share |
|---|---:|---:|
| our best, and it's a human-book move too | 379 | 47% |
| the human book's most-played move, within 20 cp of our best | 31 | 4% |
| the human book's second move, within 20 cp | 1 | 0% |
| our best: the human book's moves tried (1 or 2), all more than 20 cp worse | 14 | 2% |
| our best: the position isn't in the human book | 386 | 48% |

- Of the 425 positions the human book knows, the table's move is one of its moves in **411 (97%)**.
- The 49 human moves tried scored a median 11 cp below our best (half within +3 to +30), range -15 to +131.
- Of the 386 positions outside the human book, 333 (86%) come straight after a move of theirs the human book never
  plays, and 16 more after an earlier one. Only 37 follow a move it does play, where it has no answer for us.

## The values

In centipawns from our side, for the move the table stores, by our own engine at depth 18 (17 for human moves).

**Against every legal move of theirs**:

| | positions | median | middle half | range |
|---|---:|---:|---|---|
| White, move 1 | 1 | +29 | | |
| White, move 2 | 20 | +66 | +40 to +110 | +22 to +127 |
| White, move 3 | 379 | +125 | +81 to +163 | +20 to +492 |
| Black, move 1 | 20 | +10 | -13 to +32 | -34 to +102 |
| Black, move 2 | 391 | +61 | +27 to +108 | -33 to a mate |

**Against the moves a strong human plays** (each position weighted by how often the 2200 book's side plays into it;
every such position is in the table):

| | positions reached | median | middle half | below 0 |
|---|---:|---:|---|---:|
| White, move 2 | 20 | +24 | +22 to +33 | 0% |
| White, move 3 | 152 | +33 | +26 to +46 | 0% |
| Black, move 1 | 20 | -26 | -34 to -26 | 96% |
| Black, move 2 | 238 | **-29** | -31 to -18 | 89% |

The first table is high because most of their legal moves are bad ones. The second is the one that matters: after 5
half-moves the table keeps White at about +0.3 and Black at about -0.3, the usual size of White's first-move edge.
That isn't comparable with the scoping's exit figures (-0.48 as Black after ~9 book moves); BT4 compares like with
like.

## The first moves

As White the table always plays **1.Nf3** (+29); the human book plays 1.e4 48%, 1.d4 32%, 1.Nf3 9%.

As Black, against each of White's first moves (the table's answer is a human-book move every time):

| White | the table | value | the human book's top 3 |
|---|---|---:|---|
| 1.e4 | **e6** (the French) | -34 | c5 36%, e5 21%, e6 13% |
| 1.d4 | d5 | -26 | Nf6 47%, d5 28%, e6 6% |
| 1.Nf3 | Nf6 | -23 | Nf6 34%, d5 32%, c5 11% |
| 1.c4 | e5 | -15 | Nf6 26%, e5 22%, e6 16% |
| 1.e3 | Nf6 | -13 | d5 27%, e5 18%, Nf6 16% |
| 1.g3 | d5 | -12 | d5 38%, Nf6 18%, g6 10% |
| 1.c3 | Nf6 | -1 | d5 27%, e5 20%, Nf6 20% |
| 1.b3 | e5 | +5 | e5 27%, d5 24%, Nf6 20% |
| 1.d3 | d5 | +6 | d5 39%, Nf6 18%, e5 11% |
| 1.a3 | c5 | +8 | d5 27%, e5 21%, Nf6 15% |
| 1.h3 | e5 | +12 | d5 36%, e5 24%, Nf6 8% |
| 1.a4 | Nf6 | +13 | e5 33%, d5 23%, Nf6 21% |
| 1.Nc3 | d5 | +16 | d5 39%, Nf6 19%, c5 12% |
| 1.b4 | e5 | +20 | e5 34%, Nf6 17%, d5 16% |
| 1.f4 | d5 | +25 | d5 38%, e5 14%, Nf6 11% |
| 1.h4 | d5 | +34 | d5 38%, e5 27%, Nf6 6% |
| 1.Nh3 | d5 | +60 | d5 55%, e5 19%, Nf6 9% |
| 1.Na3 | e5 | +62 | e5 49%, d5 24%, Nf6 10% |
| 1.f3 | e5 | +66 | d5 38%, e5 27%, c5 7% |
| 1.g4 | d5 | +102 | d5 55%, e5 12%, c5 6% |

## Worth knowing before the measurement

- **Against 1.e4 the table always plays the French** (1...e6, then 2.d4 d5: -31, 35% of games against the human book's
  White). That is the defence the scoping found worst for us out of book (11 games, median -0.84 at the exit). The table
  carries it 4 half-moves further, but whether that helps is exactly what BT4 has to show. The human book plays it in
  only 13% of games.
- **It always plays the same move.** One move a position, so opponents who meet us often can prepare. The scoping
  found most bots meet us 1-4 times.
- It found the quick mates: 1.g4 e5 2.f3 Qh4#, and 1.h3 e5 2.f3 (or 1.f4 e5 2.h3) Qh4+ 3.g3 Qxg3#.
- The table was built with h1536-3m; a new net means a rebuild (~40 core-h, ~5 h on 8 cores).

## Next

- BT4: the quick look (2,000 lines, exits scored by Stockfish) and then the match, with an arm that plays the table
  first and the current books after it.
- Matthew's option: carry the table to Black's third move (`table-b.py ... --plies 6`, ~6,620 more positions). At
  this run's 171 s of CPU a search, that's ~314 core-h at depth 18 (~40 h on 8 cores), or roughly 65-100 at depth 16
  (no depth-16 timing yet).
