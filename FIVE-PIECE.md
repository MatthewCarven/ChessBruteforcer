# The 5-piece tables without pawns

All 60 material sets of five pieces without pawns, solved on Matthew's
laptop (Windows, 16 GB): mate distances (`.cbt`) and the 50-move rule
(`.cbz`) for each. Built by `scripts/build-five-piece.cmd`, 2026-09-28/29,
apart from four solved in the sessions before (KQRvKR, KRRvKR, KRNvKR,
KRBvKR: their times and peaks are from those sessions).

**Headlines**

- **51.9 billion legal positions** (both sides to move, identical pieces
  counted once), 30.8 GB on disk for both kinds, 20.9 hours of solving. Peak
  memory 2.25 GB (K+Q+R+N v K); tables with identical pieces 0.4-1.2 GB.
- **The 50-move rule changes results in 6 of the 60.** 35.9 M cursed wins
  (won with best play, drawn under the rule) and 78.6 M blessed losses,
  almost all in K+B+B v K+N, where it takes away a fifth of the wins:

  | Table | Cursed wins | share of wins | Blessed losses | share of losses |
  |---|---|---|---|---|
  | K+B+B v K+N | 31,000,264 | 20.9% | 61,322,704 | 48.1% |
  | K+B+B v K+Q | 3,808,264 | 1.2% | 16,652,104 | 8.3% |
  | K+B+N v K+N | 1,068,600 | 0.5% | 517,376 | 1.8% |
  | K+N+N v K+Q | 28,760 | 0.01% | 94,800 | 0.05% |
  | K+R+B v K+R | 17,440 | 0.01% | 5,400 | 0.02% |
  | K+Q+R v K+Q | 1,840 | <0.01% | 8,848 | <0.01% |

  Bishops and knights against a piece, manoeuvring for a long time with
  nothing to capture: two bishops against a knight was one of the endings
  FIDE once gave extra moves to (until 1992).
- **Longest mates:** K+B+N v K+N 107 moves (213 plies), K+B+B v K+Q 81 (the
  queen side wins), K+B+B v K+N 78, K+N+N v K+Q 72 (queen side), K+R+B v K+Q
  70 (queen side), K+R+N v K+Q 69 (queen side), K+Q+R v K+Q 67, K+R+B v K+R
  65. Every one checked against Lichess (Gaviota mate distances): all agree,
  and Lichess marks the cursed and blessed ones the same way.
- **18 tables are won from every white-to-move position** (all 3-v-0 but
  the minor-piece trios, plus K+Q+Q v K+B and K+Q+Q v K+N). At the other
  end, K+N+N v K+B is 99.98% drawn with white to move: its 72,816 wins are
  all quick mates (4 moves at most) with the defender caught in a corner.
- A longest DTZ of 100 plies is the rule's limit: the six tables with cursed
  wins are exactly the six that reach it.

## Three pieces against a bare king

| Table | Legal positions | White to move: won / drawn / lost | Longest mate | Longest DTZ (plies) | Cursed wins | Blessed losses | Time, both kinds (min) | Peak memory (GB) |
|---|---|---|---|---|---|---|---|---|
| KQQQvK | 173 M | 100.0 / 0.0 / 0.0% | 4 moves | 6 | 0 | 0 | 6 | 0.38 |
| KQQRvK | 543 M | 100.0 / 0.0 / 0.0% | 6 moves | 7 | 0 | 0 | 18 | 1.06 |
| KQQBvK | 560 M | 100.0 / 0.0 / 0.0% | 8 moves | 7 | 0 | 0 | 22 | 1.19 |
| KQQNvK | 572 M | 100.0 / 0.0 / 0.0% | 9 moves | 8 | 0 | 0 | 19 | 1.18 |
| KQRRvK | 572 M | 100.0 / 0.0 / 0.0% | 7 moves | 8 | 0 | 0 | 10 | 1.16 |
| KQRBvK | 1,188 M | 100.0 / 0.0 / 0.0% | 16 moves | 9 | 0 | 0 | 27 | 2.17 |
| KQRNvK | 1,212 M | 100.0 / 0.0 / 0.0% | 16 moves | 9 | 0 | 0 | 26 | 2.25 |
| KQBBvK | 611 M | 100.0 / 0.0 / 0.0% | 19 moves | 12 | 0 | 0 | 20 | 1.10 |
| KQBNvK | 1,254 M | 100.0 / 0.0 / 0.0% | 33 moves | 9 | 0 | 0 | 27 | 2.18 |
| KQNNvK | 641 M | 100.0 / 0.0 / 0.0% | 9 moves | 14 | 0 | 0 | 20 | 1.05 |
| KRRRvK | 201 M | 100.0 / 0.0 / 0.0% | 7 moves | 9 | 0 | 0 | 7 | 0.43 |
| KRRBvK | 632 M | 100.0 / 0.0 / 0.0% | 16 moves | 11 | 0 | 0 | 19 | 1.08 |
| KRRNvK | 644 M | 100.0 / 0.0 / 0.0% | 16 moves | 11 | 0 | 0 | 19 | 1.11 |
| KRBBvK | 656 M | 100.0 / 0.0 / 0.0% | 19 moves | 21 | 0 | 0 | 20 | 1.09 |
| KRBNvK | 1,343 M | 100.0 / 0.0 / 0.0% | 33 moves | 15 | 0 | 0 | 28 | 2.10 |
| KRNNvK | 685 M | 100.0 / 0.0 / 0.0% | 16 moves | 21 | 0 | 0 | 19 | 1.11 |
| KBBBvK | 224 M | 73.9 / 26.1 / 0.0% | 19 moves | 21 | 0 | 0 | 6 | 0.39 |
| KBBNvK | 693 M | 100.0 / 0.0 / 0.0% | 33 moves | 27 | 0 | 0 | 19 | 1.17 |
| KBNNvK | 711 M | 100.0 / 0.0 / 0.0% | 34 moves | 27 | 0 | 0 | 19 | 1.07 |
| KNNNvK | 242 M | 98.7 / 1.3 / 0.0% | 21 moves | 42 | 0 | 0 | 6 | 0.45 |

## Two pieces against one

| Table | Legal positions | White to move: won / drawn / lost | Longest mate | Longest DTZ (plies) | Cursed wins | Blessed losses | Time, both kinds (min) | Peak memory (GB) |
|---|---|---|---|---|---|---|---|---|
| KQQvKQ | 448 M | 99.1 / 0.8 / 0.1% | 30 moves | 50 | 0 | 0 | 17 | 0.91 |
| KQQvKR | 502 M | 100.0 / 0.0 / 0.0% | 35 moves | 28 | 0 | 0 | 19 | 1.04 |
| KQQvKB | 532 M | 100.0 / 0.0 / 0.0% | 17 moves | 8 | 0 | 0 | 20 | 1.07 |
| KQQvKN | 551 M | 100.0 / 0.0 / 0.0% | 21 moves | 9 | 0 | 0 | 20 | 1.09 |
| KQRvKQ | 970 M | 97.0 / 2.8 / 0.2% | 67 moves | 100 | 1,840 | 8,848 | 28 | 1.79 |
| KQRvKR | 1,078 M | 99.8 / 0.1 / 0.0% | 35 moves | 31 | 0 | 0 | 27 | 1.95 |
| KQRvKB | 1,138 M | 100.0 / 0.0 / 0.0% | 29 moves | 10 | 0 | 0 | 30 | 1.96 |
| KQRvKN | 1,177 M | 99.9 / 0.1 / 0.0% | 40 moves | 10 | 0 | 0 | 28 | 2.08 |
| KQBvKQ | 1,016 M | 55.7 / 44.0 / 0.3% | 33 moves | 60 | 0 | 0 | 22 | 1.60 |
| KQBvKR | 1,123 M | 99.3 / 0.6 / 0.0% | 40 moves | 38 | 0 | 0 | 27 | 1.93 |
| KQBvKB | 1,183 M | 99.7 / 0.3 / 0.0% | 17 moves | 16 | 0 | 0 | 27 | 1.97 |
| KQBvKN | 1,223 M | 99.5 / 0.5 / 0.0% | 21 moves | 14 | 0 | 0 | 27 | 1.94 |
| KQNvKQ | 1,047 M | 50.1 / 49.6 / 0.3% | 41 moves | 70 | 0 | 0 | 22 | 1.55 |
| KQNvKR | 1,154 M | 99.2 / 0.7 / 0.0% | 41 moves (black wins) | 44 | 0 | 0 | 28 | 1.88 |
| KQNvKB | 1,214 M | 99.8 / 0.2 / 0.0% | 17 moves | 18 | 0 | 0 | 27 | 1.97 |
| KQNvKN | 1,254 M | 99.4 / 0.6 / 0.0% | 21 moves | 18 | 0 | 0 | 26 | 1.96 |
| KRRvKQ | 527 M | 58.1 / 36.8 / 5.1% | 49 moves (black wins) | 40 | 0 | 0 | 17 | 0.85 |
| KRRvKR | 581 M | 99.2 / 0.7 / 0.0% | 31 moves | 50 | 0 | 0 | 22 | 1.88 |
| KRRvKB | 611 M | 99.3 / 0.7 / 0.0% | 29 moves | 20 | 0 | 0 | 18 | 1.07 |
| KRRvKN | 631 M | 99.7 / 0.3 / 0.0% | 40 moves | 15 | 0 | 0 | 19 | 1.05 |
| KRBvKQ | 1,114 M | 38.7 / 47.9 / 13.4% | 70 moves (black wins) | 83 | 0 | 0 | 28 | 1.68 |
| KRBvKR | 1,221 M | 41.2 / 58.7 / 0.0% | 65 moves | 100 | 17,440 | 5,400 | 19 | 1.51 |
| KRBvKB | 1,281 M | 98.2 / 1.8 / 0.0% | 30 moves | 50 | 0 | 0 | 39 | 1.90 |
| KRBvKN | 1,321 M | 98.9 / 1.1 / 0.0% | 40 moves | 42 | 0 | 0 | 49 | 1.93 |
| KRNvKQ | 1,144 M | 35.4 / 41.1 / 23.4% | 69 moves (black wins) | 92 | 0 | 0 | 29 | 1.75 |
| KRNvKR | 1,252 M | 36.6 / 63.3 / 0.1% | 41 moves (black wins) | 65 | 0 | 0 | 18 | 1.49 |
| KRNvKB | 1,312 M | 97.7 / 2.3 / 0.0% | 31 moves | 50 | 0 | 0 | 26 | 1.92 |
| KRNvKN | 1,351 M | 99.0 / 1.0 / 0.0% | 40 moves | 48 | 0 | 0 | 29 | 1.93 |
| KBBvKQ | 580 M | 15.3 / 20.2 / 64.6% | 81 moves (black wins) | 100 | 3,808,264 | 16,652,104 | 21 | 0.98 |
| KBBvKR | 633 M | 16.5 / 83.4 / 0.1% | 31 moves (black wins) | 17 | 0 | 0 | 12 | 0.69 |
| KBBvKB | 663 M | 15.6 / 84.4 / 0.0% | 22 moves | 12 | 0 | 0 | 12 | 0.68 |
| KBBvKN | 683 M | 48.2 / 51.8 / 0.0% | 78 moves | 100 | 31,000,264 | 61,322,704 | 14 | 0.89 |
| KBNvKQ | 1,200 M | 25.0 / 6.4 / 68.6% | 53 moves (black wins) | 84 | 0 | 0 | 29 | 1.96 |
| KBNvKR | 1,307 M | 26.0 / 73.8 / 0.2% | 41 moves (black wins) | 25 | 0 | 0 | 18 | 1.42 |
| KBNvKB | 1,367 M | 25.5 / 74.5 / 0.0% | 39 moves | 25 | 0 | 0 | 16 | 1.40 |
| KBNvKN | 1,407 M | 32.1 / 67.9 / 0.0% | 107 moves | 100 | 1,068,600 | 517,376 | 16 | 1.48 |
| KNNvKQ | 618 M | 0.0 / 42.8 / 57.2% | 72 moves (black wins) | 100 | 28,760 | 94,800 | 18 | 0.95 |
| KNNvKR | 671 M | 0.0 / 99.6 / 0.4% | 41 moves (black wins) | 21 | 0 | 0 | 12 | 0.66 |
| KNNvKB | 701 M | 0.0 / 100.0 / 0.0% | 4 moves | 7 | 0 | 0 | 11 | 0.64 |
| KNNvKN | 721 M | 0.1 / 99.9 / 0.0% | 7 moves | 13 | 0 | 0 | 10 | 0.65 |

"Longest mate" is the longest forced mate in the table, for whichever side
has it ("black wins": the lone piece's side). "Longest DTZ" is the longest
stretch, in plies, before the next capture, pawn move or mate under best play
with the rule; 100 is the rule's limit.
