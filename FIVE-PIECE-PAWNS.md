# The 5-piece tables with pawns

All 50 material sets of five pieces with at least one pawn, solved on
Matthew's laptop (Windows, 16 GB): mate distances (`.cbt`) and the 50-move
rule (`.cbz`) for each, stored compressed. Built by
`scripts/build-five-piece.cmd -Pawns`, 2026-09-30 to 2026-10-05, in the order
`game endings` chose (each after the tables its promotions lead to), apart
from KRPvKR, solved in the sessions before (its figures are from those). The
60 without pawns are in [FIVE-PIECE.md](FIVE-PIECE.md); with these, every
table of five pieces or fewer is done.

**Headlines**

- **38.8 billion legal positions** (both sides to move, identical pieces
  counted once). 107 GB as solved, **8.8 GB compressed** (12x; pawn tables
  compress better than the pawnless ones' 9x). **72.6 hours of solving**;
  the run took five days, since the laptop slept for 48 hours of it (the
  log's step times include those: 120.5 h). Peak memory 3.9 GB (K+R+P v K+P).
- **Checked two ways.** Every table, as it was built: every 1009th position
  of both kinds checked against its moves, 38.4 million in all, every one
  consistent. Then against Lichess: each table's longest mate and longest
  DTZ for both sides to move, and three random positions each, 343 in all,
  **every one agreeing** on the mate distance (Gaviota) and on the result and
  distance under the 50-move rule (Syzygy) (`scripts/pawn-tables-check.py`,
  results in `scripts/pawn-tables-check.tsv`).
- **Longest mates:** K+P+P v K+P **127 moves** (253 plies), K+Q+P v K+Q 124,
  K+P+P v K+Q 124, K+Q+P v K+P 122, K+N+N v K+P 115, K+R+P v K+Q 104 (the
  queen side wins), K+B+N v K+P 104, K+R+P v K+P 103 (the lone pawn's side
  wins). The 127-move mate is a cursed win: under the rule it's a draw.
- **The 50-move rule changes results in 14 of the 50**, overwhelmingly in
  K+N+N v K+P: the ending that needs more than 50 moves to win (the
  Troitsky line), one of the endings FIDE once gave extra moves to.

  | Table | Cursed wins | share of wins | Blessed losses | share of losses |
  |---|---|---|---|---|
  | K+N+N v K+P | 21,391,690 | 18% | 19,023,998 | 41% |
  | K+B+B v K+P | 2,889,224 | 2.0% | 239,826 | 0.22% |
  | K+R+P v K+Q | 62,630 | 0.01% | 145,606 | 0.06% |
  | K+Q+P v K+Q | 56,936 | 0.01% | 44,822 | 0.08% |
  | K+R+P v K+P | 24,274 | <0.01% | 1,838 | <0.01% |
  | K+P+P v K+P | 3,668 | <0.01% | 4,108 | <0.01% |
  | K+P+P v K+Q | 3,282 | <0.01% | 6 | <0.01% |
  | 7 more | 1,114 | <0.01% | 8,108 | <0.01% |

  Of the 98 longest-mate positions, 21 are cursed or blessed: the longest
  mates mostly can't be forced inside the rule.
- **12 tables are won from every white-to-move position**: 10 of the 15
  "three against a bare king" (not K+B+B+P, K+N+N+P, K+B+P+P, K+N+P+P or
  K+P+P+P, which each keep a few draws, 0.02-1.7%), and K+Q+Q v K+P and K+Q+R
  v K+P. A longest DTZ of 100, the rule's limit, comes in four: K+Q+P v K+Q,
  K+R+P v K+Q, K+R+P v K+B, K+N+N v K+P.
- **Pawns on both sides take longest to solve**: K+P+P v K+P 7.5 hours,
  K+R+P v K+P and K+Q+P v K+P 3.6-3.9 hours, K+N+P v K+P and K+B+P v K+P
  2.4-2.8 hours (and K+R+B v K+P 2.2); the rest about an hour each, both
  kinds together. More slices (pawn placements of both colours), and en
  passant.
- **Two failures, both from memory, both recovered**: a compress of K+Q+P+P v
  K (low virtual memory that second; done by hand after) and K+Q+P v K+P's
  first solve (low memory earlier in it, another program holding 7.3 GB); the
  script's retry finished it.

"Time" below is solving with the laptop awake. "Longest mate" is the longest
forced mate in the table, for whichever side has it ("black wins": the side
with fewer pieces). "Longest DTZ" is the longest stretch, in plies, before
the next capture, pawn move or mate under best play with the rule; 100 is the
rule's limit. "Compressed" is both files together.

## Three pieces against a bare king

| Table | Legal positions | White to move: won / drawn / lost | Longest mate | Longest DTZ (plies) | Cursed wins | Blessed losses | Time, both kinds (min) | Peak memory (GB) | Compressed (MB) |
|---|---|---|---|---|---|---|---|---|---|
| KQQPvK | 436 M | 100.0 / 0.0 / 0.0% | 10 moves | 6 | 0 | 0 | 78 | 1.81 | 50 |
| KQRPvK | 927 M | 100.0 / 0.0 / 0.0% | 16 moves | 6 | 0 | 0 | 72 | 2.74 | 97 |
| KQBPvK | 959 M | 100.0 / 0.0 / 0.0% | 31 moves | 6 | 0 | 0 | 76 | 2.68 | 122 |
| KQNPvK | 982 M | 100.0 / 0.0 / 0.0% | 27 moves | 6 | 0 | 0 | 71 | 2.70 | 122 |
| KQPPvK | 374 M | 100.0 / 0.0 / 0.0% | 32 moves | 6 | 0 | 0 | 64 | 1.67 | 42 |
| KRRPvK | 494 M | 100.0 / 0.0 / 0.0% | 16 moves | 6 | 0 | 0 | 68 | 1.89 | 51 |
| KRBPvK | 1,031 M | 100.0 / 0.0 / 0.0% | 31 moves | 8 | 0 | 0 | 70 | 2.77 | 137 |
| KRNPvK | 1,053 M | 100.0 / 0.0 / 0.0% | 27 moves | 8 | 0 | 0 | 67 | 2.77 | 141 |
| KRPPvK | 403 M | 100.0 / 0.0 / 0.0% | 32 moves | 6 | 0 | 0 | 99 | 1.49 | 53 |
| KBBPvK | 532 M | 98.3 / 1.7 / 0.0% | 31 moves | 24 | 0 | 0 | 67 | 1.92 | 95 |
| KBNPvK | 1,093 M | 100.0 / 0.0 / 0.0% | 33 moves | 10 | 0 | 0 | 65 | 2.75 | 210 |
| KBPPvK | 418 M | 99.8 / 0.2 / 0.0% | 32 moves | 18 | 0 | 0 | 50 | 1.43 | 72 |
| KNNPvK | 559 M | 98.4 / 1.6 / 0.0% | 28 moves | 16 | 0 | 0 | 65 | 1.89 | 106 |
| KNPPvK | 429 M | 100.0 / 0.0 / 0.0% | 32 moves | 12 | 0 | 0 | 46 | 1.17 | 69 |
| KPPPvK | 109 M | 99.9 / 0.1 / 0.0% | 33 moves | 15 | 0 | 0 | 68 | 0.58 | 12 |

## Two pieces against one

| Table | Legal positions | White to move: won / drawn / lost | Longest mate | Longest DTZ (plies) | Cursed wins | Blessed losses | Time, both kinds (min) | Peak memory (GB) | Compressed (MB) |
|---|---|---|---|---|---|---|---|---|---|
| KQQvKP | 431 M | 100.0 / 0.0 / 0.0% | 30 moves | 6 | 0 | 0 | 78 | 1.84 | 80 |
| KQRvKP | 918 M | 100.0 / 0.0 / 0.0% | 67 moves | 6 | 0 | 158 | 92 | 2.85 | 148 |
| KQBvKP | 953 M | 100.0 / 0.0 / 0.0% | 33 moves | 23 | 0 | 0 | 78 | 2.80 | 191 |
| KQNvKP | 976 M | 99.9 / 0.1 / 0.0% | 41 moves | 34 | 0 | 0 | 76 | 2.83 | 213 |
| KQPvKQ | 809 M | 68.4 / 31.2 / 0.4% | 124 moves | 100 | 56,936 | 44,822 | 70 | 2.56 | 267 |
| KQPvKR | 889 M | 99.6 / 0.3 / 0.1% | 43 moves | 34 | 0 | 0 | 87 | 2.78 | 266 |
| KQPvKB | 934 M | 99.9 / 0.1 / 0.0% | 29 moves | 10 | 0 | 0 | 81 | 2.80 | 222 |
| KQPvKN | 963 M | 99.7 / 0.3 / 0.0% | 30 moves | 12 | 0 | 0 | 81 | 2.77 | 216 |
| KQPvKP | 745 M | 100.0 / 0.0 / 0.0% | 122 moves | 10 | 38 | 6,532 | 218 | 3.55 | 150 |
| KRRvKP | 490 M | 100.0 / 0.0 / 0.0% | 50 moves (black wins) | 18 | 0 | 0 | 74 | 1.87 | 88 |
| KRBvKP | 1,026 M | 99.1 / 0.9 / 0.0% | 70 moves (black wins) | 22 | 0 | 0 | 130 | 2.88 | 234 |
| KRNvKP | 1,048 M | 98.5 / 1.5 / 0.0% | 68 moves (black wins) | 29 | 0 | 0 | 90 | 2.90 | 269 |
| KRPvKQ | 887 M | 37.7 / 11.8 / 50.5% | 104 moves (black wins) | 100 | 62,630 | 145,606 | 86 | 2.77 | 442 |
| KRPvKR | 967 M | 66.6 / 33.0 / 0.4% | 74 moves | 70 | 0 | 0 | 69 | 3.72 | 248 |
| KRPvKB | 1,012 M | 96.4 / 3.6 / 0.0% | 73 moves | 100 | 70 | 166 | 71 | 2.79 | 368 |
| KRPvKN | 1,041 M | 97.5 / 2.5 / 0.0% | 54 moves | 62 | 0 | 0 | 72 | 2.76 | 352 |
| KRPvKP | 802 M | 99.4 / 0.4 / 0.3% | 103 moves (black wins) | 20 | 24,274 | 1,838 | 232 | 3.87 | 193 |
| KBBvKP | 530 M | 48.0 / 50.2 / 1.8% | 83 moves (black wins) | 42 | 2,889,224 | 239,826 | 66 | 1.66 | 117 |
| KBNvKP | 1,090 M | 91.4 / 5.5 / 3.2% | 104 moves | 40 | 370 | 550 | 76 | 2.79 | 453 |
| KBPvKQ | 930 M | 21.3 / 11.5 / 67.2% | 50 moves (black wins) | 84 | 0 | 0 | 85 | 2.83 | 385 |
| KBPvKR | 1,010 M | 30.9 / 67.3 / 1.8% | 45 moves | 37 | 0 | 0 | 57 | 2.50 | 182 |
| KBPvKB | 1,055 M | 41.3 / 58.7 / 0.0% | 51 moves | 50 | 0 | 0 | 54 | 2.49 | 198 |
| KBPvKN | 1,084 M | 56.6 / 43.4 / 0.0% | 100 moves | 60 | 514 | 528 | 80 | 2.53 | 265 |
| KBPvKP | 835 M | 86.4 / 9.6 / 4.1% | 67 moves | 74 | 0 | 0 | 143 | 3.46 | 261 |
| KNNvKP | 558 M | 31.3 / 66.4 / 2.3% | 115 moves | 100 | 21,391,690 | 19,023,998 | 54 | 1.61 | 150 |
| KNPvKQ | 959 M | 17.9 / 11.9 / 70.2% | 62 moves (black wins) | 86 | 0 | 2 | 79 | 2.84 | 367 |
| KNPvKR | 1,039 M | 26.7 / 69.3 / 4.0% | 67 moves (black wins) | 79 | 0 | 0 | 56 | 2.52 | 188 |
| KNPvKB | 1,084 M | 38.8 / 61.2 / 0.0% | 43 moves | 48 | 0 | 0 | 55 | 2.50 | 186 |
| KNPvKN | 1,113 M | 49.2 / 50.8 / 0.0% | 97 moves | 59 | 122 | 172 | 52 | 2.54 | 225 |
| KNPvKP | 856 M | 78.2 / 13.7 / 8.1% | 58 moves (black wins) | 46 | 0 | 0 | 170 | 3.12 | 270 |
| KPPvKQ | 370 M | 16.0 / 12.6 / 71.4% | 124 moves | 59 | 3,282 | 6 | 44 | 1.19 | 98 |
| KPPvKR | 400 M | 35.4 / 20.1 / 44.5% | 54 moves | 30 | 0 | 0 | 54 | 1.16 | 102 |
| KPPvKB | 417 M | 54.4 / 45.6 / 0.0% | 43 moves | 24 | 0 | 0 | 33 | 1.13 | 70 |
| KPPvKN | 428 M | 64.7 / 35.3 / 0.0% | 50 moves | 27 | 0 | 0 | 33 | 1.15 | 83 |
| KPPvKP | 327 M | 76.8 / 10.4 / 12.8% | 127 moves | 42 | 3,668 | 4,108 | 452 | 1.62 | 84 |

