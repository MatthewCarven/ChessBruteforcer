# Book tables: an opening book brute-forced like the endgame tables (scoped 2026-10-22)

Matthew's idea (2026-10-22), in his words: "Brute force entry tables, kind of like the matchbook but done in the style
of the end game tables, like we pit the bot against itself, and run through all the possibilities in up to 50 games
deep like? What do you reckon? We could throw away games that played badly and remember the games that took the
minimum time to win like?" ("Entry tables" are opening tables; "matchbook" is the opening book.)

Later in the same evening, while this was being scoped (23:00-23:20):
- "this is just a test for now"
- "It may be that the opening book is better its up to us to compare"
- "well lets run all the possibilities to 5 deep only the moves we can make legally"
- "i think that will be a few gb of disk?"
- "any lost pieces block out the entries for up to 5 deep"

Scoped only: nothing in src or bot changed, and no engine was run. The numbers come from the bot's 622 games
(bot\games: record.csv and the PGNs, whose `[%eval]` comments hold our engine's evaluation after each of its moves),
the books, a count of every legal position to 5 plies (python-chess), and Lichess's public cloud evaluations of 153
positions the bot reached (Stockfish at depth ~40, 5 lines each). Scripts in
[scripts/book-tables/](scripts/book-tables/) (stdlib, or python-chess from the bot's venv; they read the main
checkout's bot\games and bot\books): bookexit.py, exitworth.py and exitfamily.py (where the books leave us),
reach.py (how far they reach), tree.py, prune.py and ourbest.py (the 5-ply counts), margins.py and
margins_summary.py (the cloud), costs.py. Outputs in results/. The decisions are at the end.

## The short answer

1. **Every legal position to 5 plies is small: 822,518 positions after the first 5 half-moves** (counted;
   transpositions merged). As a book that's a few MB, not GB. **To 5 moves each (10 plies) it's 85 billion**, about
   as many as the 5-piece tables' 90.7 billion. That's where the likeness ends. The tables took 93.5 hours because a
   position's value follows from its successors' in ~4 µs, starting from positions whose values are known (mates,
   stalemates, captures into smaller tables). Openings have no such positions: every leaf needs a search,
   0.5 s at depth 10 and 11 s at depth 14. 85 billion half-second searches is ~1,300 core-years (and 1.4 TB as book
   entries).
2. **Blocking lines that lose material (Matthew's rule) is sound, but cuts little this early.** At a whole piece it
   removes 13% of the 5-ply tree (822,518 → 715,940) and never touches a real opening: 4 of 19,607 positions on the
   human book's lines. At a single pawn it removes 33% (to 554,877), but it also blocks 7.8% of the human book's
   positions: the gambits and other pawn sacrifices.
3. **The tables' real trick does carry over: all of their moves, but only our best.** A table stores the best move
   in every position the opponent can bring about. A book needs the same thing: every legal reply of theirs, and at
   our turns just our engine's best move, found by one deep search. That grows ~18x per pair of moves (theirs and
   ours), where the full tree grows ~150x. **Every possibility in the first 5 half-moves is then 754 of our positions to
   search** (364 as White, 390 as Black), not 715,940 leaves. That's ~1,000x fewer, so each search can go to depth
   18 (the bot reaches ~14 over the board) for less CPU than the full table spends at depth 10.
4. **On Matthew's comparison, the human book has a measurable weak spot: Black.** On the bot's first move out of
   book (242 network-era games), our engine's own evaluation has a median of **+0.20 as White and -0.48 as Black**,
   and 48% of Black's exits are already -0.50 or worse (8% of White's). It's concentrated in a few defences:
   the French as Black, 11 games, median -0.84 (+0.23 when we're White against it); the Modern, 5 games, -0.99.
   With the ratings allowed for, each pawn at the exit goes with +0.145 of a game's score [0.098, 0.196]. That's
   correlation, not proof. Bringing Black's exits up 0.3 pawn would be worth **at most ~+15 Elo**, a seventh of a
   ply of depth (~+105).
5. **Telling two books apart takes thousands of games.** ±20 Elo needs ~1,600 games an arm, ±10 ~6,000. The current
   book's own worth has never been measured either (TODO, "The book's worth, measured"), so the comparison should
   measure it too.

My picks: **BT1 (b)**, Matthew's 5 half-moves in the "all of theirs, our best" form, at depth 18 (~85 core-hours, one
training window); **BT2 (a)**, searches; **BT3 (a)**, first in the chain, keeping to the human book's moves where
they're within 20 cp; **BT4 (a)**, a three-arm match (current books, the table, no book). Details below.

## 1. What's feasible

### Every legal position to 5 plies (counted)

| ply | positions | legal moves (mean) | blocked at a piece (kept) | blocked at a pawn (kept) | with a move in the 2200 book | reached in the bot's 622 games |
|---|---:|---:|---:|---:|---:|---:|
| 0 | 1 | 20.0 | | | 1 | 1 |
| 1 | 20 | 20.0 | 0 (20) | 0 (20) | 20 | 13 |
| 2 | 400 | 22.3 | 0 (400) | 19 (381) | 308 | 59 |
| 3 | 5,362 | 22.1 | 131 (5,231) | 454 (4,758) | 1,323 | 117 |
| 4 | 72,078 | 24.9 | 2,628 (67,984) | 9,584 (56,925) | 3,300 | 187 |
| 5 | 822,518 | | 66,897 (715,940) | 144,949 (554,877) | 6,351 | 253 |
| human book's lines, plies 1-10 (2,000 random walks) | 19,607 | | 4 (0.02%) | 1,536 (7.8%) | | |

(The position counts match OEIS A083276. "Blocked": a capture-only search, pieces valued 1/3/3/5/9 with checks
answered, finds one side down a piece (or a pawn) or more. A blocked position gets no children, so the blocked
columns count only positions reached from kept ones.) 99.2% of the positions at ply 5 have no move in the human book,
and the bot has reached 253 of them. A full table spends almost all its work on positions nobody plays. That's true
of the 5-piece tables too, but there a position costs microseconds.

### Matthew's 5 half-moves, two ways

| | (a) the full table, minimax (the tables' way) | (b) all of theirs, our best |
|---|---|---|
| what's searched | every position after 5 half-moves: 715,940 (a piece blocks) | our positions in the first 5 half-moves: 754 (364 as White: moves 1-3; 390 as Black: moves 1-2) |
| how our move is chosen | minimax over 5 full-width plies, then a search at each leaf | one deep search in each of our positions |
| CPU (one-thread searches) | depth 10: 97 core-h; 12: 468; 14: 2,267 | depth 16: 12 core-h; 18: 56; 20: 271 |
| on the laptop's 8 cores | depth 10: ~12 h; 12: ~2.4 days | depth 18: ~7 h (~11 h with the check in BT3 (a)) |
| our move's depth | 11-15 plies (full width to ply 5, then 10) | 18 plies |
| what it holds | a value and a best move for both sides in all 784,000 positions | our best move in every position they can reach |
| disk | ~13 MB as a book (~60 MB with values) | ~12 KB |

Both give a book of our engine's own moves for the first 5 half-moves. (b)'s moves come from searches 3-7 plies
deeper, for less CPU. (a) also holds the opponent's best replies, which our book doesn't use. Carried on to Black's third move
(6 half-moves), (b) needs 6,620 more positions as Black: ~490 core-h at depth 18, ~100 at depth 16.

Search times: depth 14 took 10.4 s a position (median) on one thread in the learner's re-score (book-exit positions,
h1024-3m); h1536-3m takes 1.10x as long to a depth (`bench 9`). Each ply costs ~2.2x (p2.1, WORKLOG 2026-10-21).
So depth 10 is ~0.5 s, 14 ~11 s, 16 ~55 s, 18 ~4.5 min, 20 ~22 min. Treat these as ±50% until measured.

### Further than 5 half-moves: a margin book

The bounded version strong engines' books use: expand only moves within a margin of the best. The number of moves
within the margin, measured on positions the bot reached (Lichess cloud, Stockfish depth ~40, 5 lines, 129
positions):

| plies | positions | within 20 cp | within 30 cp | within 50 cp (5 means 5+) | at the cap of 5 (50 cp) |
|---|---:|---:|---:|---:|---:|
| 0-3 | 25 | 3.8 | 4.8 | 4.9 | 92% |
| 4-7 | 31 | 2.6 | 3.1 | 3.8 | 71% |
| 8-11 | 32 | 2.8 | 3.7 | 4.1 | 78% |
| 12-15 | 26 | 2.0 | 2.6 | 3.4 | 54% |
| 16-19 | 15 | 2.3 | 3.1 | 4.0 | 73% |
| all | 129 | **2.65** | **3.41** | **4.0 or more** | 74% |

(Geometric means, which is what a tree grows by. 17 of the 153 positions asked for weren't in the cloud, mostly at
plies 16-19.) Tree sizes from those:

| margin | plies | both sides within the margin: positions at the last ply, one colour | our best, theirs within the margin: our positions, both colours | core-hours at depth 14 / 16 / 18 (the second) |
|---|---:|---:|---:|---|
| 20 cp | 10 | 17,000 | 287 | 1 / 4 / 21 |
| 20 cp | 14 | 840,000 | 2,028 | 6 / 31 / 150 |
| 20 cp | 20 | 290 million | 37,778 | 120 / 579 / 2,802 |
| 30 cp | 10 | 210,000 | 842 | 3 / 13 / 62 |
| 30 cp | 14 | 29 million | 9,809 | 31 / 150 / 728 |
| 30 cp | 20 | 45 billion | 389,012 | 1,232 / 5,962 / 28,857 |
| 50 cp | 10 | 1 million+ | 1,705+ | 5 / 26 / 126 |
| 50 cp | 14 | 270 million+ | 27,305+ | 86 / 418 / 2,026 |
| 50 cp | 20 | 1.1 trillion+ | 1.7 million+ | 5,534 / 26,785 / 129,641 |

Up to ~14 plies at 20-30 cp is a few nights of the laptop. Two cautions:
- **A margin on their side misses the moves that end our books today.** Bots leave the human book with rare moves,
  mostly by our move 6-8 (WORKLOG 2026-10-13: "what's missing is width, not depth"). Rare moves are mostly not
  within 30 cp of the best. "All of theirs" catches them but grows ~18x a move, so it stops at ~6 half-moves.
- **The human book already reaches our 9th move** (median, network era; 16% of games use all 12 book moves). A
  margin book to 14 plies covers our first 7. It earns its place only where its moves beat the human ones.

### Games instead of searches

One self-play game at 10+0.1 costs ~1 core-minute (the matches: ~600 games an hour on ~10 cores), about a depth-16
search. Scoring a position to within ±0.05 of a point (±35 Elo, 95%) takes 200-300 games, i.e. 200-300x the
search's CPU, for a noisier answer. Most
self-play games between good moves are draws, and the wins mostly come from the loser's mistakes. So games are for
checking the finished book (BT4), not for scoring its positions.

### Where it runs

- **The laptop, as 8 one-thread searches at once.** One search on 8 threads only reaches a depth ~2x sooner
  (bench-threads, WORKLOG 2026-10-15). 8 separate searches do ~4x the work. A fixed-depth search isn't timed, so it
  can run beside the ~13-hour GPU training (the learner's re-score did: 3 h instead of 2.3). Check FreeVirtualMemory
  first and keep 2 GB free; 8 one-thread engines with Hash 64 and no tables need ~1-2 GB.
- **Not the desktop.** Its i7-3770 has 4 cores and no AVX2, so the engine falls back to its slow scalar network code
  there, and its CPU is busy with NNUE records.

## 2. Where Matthew's other ideas fit

- **"Throw away games that played badly."** In a table built by searches there are no games. The same idea becomes
  two filters: his blocking rule on their moves (a reply that loses a piece gets no entry; the engine answers such
  blunders easily in play), and the learner's check on ours (a move ≥ 50 cp below our best is never stored). If
  games are ever used, drop a game where one of its moves loses ≥ 75 cp to a deeper re-search (learn.py's --worse).
- **"Remember the games that took the minimum time to win."** In the tables, the fastest win is exact (depth to
  mate). In openings there's no win to count. In self-play, wins are rare and come from mistakes, so the fastest
  ones mostly record the worst blunders. It fits only as a tie-break between moves within the margin, e.g. the one
  whose score climbs soonest. I'd leave it out of the first test.
- **"Pit the bot against itself."** It's the right way to check a finished book (BT4), and the wrong way to score
  its positions (above).

## 3. How it plugs in

- **The file.** A polyglot .bin: 16 bytes an entry (position key, move, weight). make-book.py and learn.py already
  write them. (b)'s table is ~754 entries, ~12 KB.
- **The chain** (bot/make-config.py; lichess-bot tries the books in order every move, and plays a weighted-random
  move from the first one that has the position). Today: lichess-2200.bin, ours.bin, lichess-2200-wide.bin. The
  table goes **first**. Placed after the human book, it would only answer where that book is silent: the learner's
  job, and it would hardly ever play. Used instead of the human book, our book would end at move 3 instead of
  move 9: the engine would think from move 4 and spend clock it saves today.
- **Keeping the human book going.** If the table plays a move the human book doesn't have, our book ends right
  after the table's last move. BT3 (a) avoids that: where one of the human book's moves is within 20 cp of our
  best, the table stores that one. Our engine has no MultiPV or searchmoves, so checking a human move costs one more
  search after it (as rescore.py does), in roughly half of the positions (the learner's re-score: h1024-3m's best
  move differed from the learnt one in 51%). That's ~1.5x the searches.
- **The learner** treats the main, wide and ours books as "a book has a move"; the table needs adding to that list,
  or it re-studies positions the table already answers. One line.
- **book-match.py** reads the books in make-config.py's order. A book list per arm is ~30 lines.
- **Variety.** Today the bot changes its move from a position it has met before 60% of the time, and bots 30%
  (OPPONENTS.md). A one-move table fixes our first 3 moves as White and first 2 as Black. OPPONENTS.md found no bot
  weak or strong against us beyond chance, and most bots meet us 1-4 times, so being predictable has no measured
  cost (and O3 (a)'s replay trial wants repetition anyway). If variety is wanted: up to 3 moves within 10 cp,
  weighted.

## 4. How it would be measured

**The match** ([scripts/book-match.py](scripts/book-match.py), given a book list per arm): our engine against
Stockfish 16 (WSL), arms alternating, colours alternating, 10+0.1. Two fixes to its old plan (TODO): weakened Stockfish
plays odd moves, so our book lasted ~4 moves against it. So **Stockfish gets the human wide book for its own first
moves**, and the games follow lines like the bots'. And **three arms**: the current books, the table first and then
the current books, and no book. The third answers the open "book's worth" item at no extra cost.

| games an arm | 95% interval on an arm-to-arm difference | ~time for three arms (bot off, ~10 cores) |
|---:|---:|---:|
| 1,000 | ±26 Elo | 5 h |
| 3,000 | ±15 Elo | 15 h |
| 6,000 | ±10 Elo | 30 h |

(At ~50% and ~30% draws; two arms take two thirds of the time.) An SPRT [0, 10] between the table and the current
books stops early if the difference is large.

**A quick look without games**: walk 2,000 lines, with each book playing our moves and the human wide book playing
theirs. Score each exit position with Stockfish at depth 20 (WSL, ~1 s each), so the judge isn't the engine that
built the table, and with our engine at depth 14. At most ~13 core-hours, ~2 h on 8 cores, untimed. A table that doesn't move Black's exits
toward White's isn't worth a match.

**What keeps it.** The table costs a rebuild with each new net, so it stays only if it measurably wins: the SPRT
accepts +10, or after 3,000 games an arm the interval clears -5 and the estimate is +5 or more. If 3,000 games can't
tell the books apart, they're within ~±15 Elo of each other, and Matthew's "the opening book may be better" is
answered as "no better, no worse": keep whichever is cheaper. A cheap change such as BT1 (c) stays if it isn't worse
(the interval clears -10) and Black's exits improve.

## 5. Effort

| option | sessions | CPU | laptop time | when |
|---|---|---|---|---|
| BT1 (a) the full 5-ply table | 1 (driver, minimax, the book) | ~100 core-h at depth 10 | ~12 h on 8 cores | a training window |
| **BT1 (b) all of theirs, our best, 5 half-moves** | **1** | **~85 core-h at depth 18, with BT3 (a)'s check** | **~11 h on 8 cores** | **a training window** |
| BT1 (b) carried to Black's move 3 | +0 | +~150 core-h at depth 16 | +~19 h | a second window |
| BT1 (c) vet the human book as Black | 1 | ~10-30 core-h | 1-4 h | any free window |
| BT1 (d) a 30 cp margin book to 14 plies | 2 | ~150-230 core-h at depth 16 | ~1 day | after (b) or (c) shows something |
| BT4 (a) the match, three arms | 0.5 (book-match.py) | ~150-250 core-h of games | ~15-25 h, bot off | a night or two |
| the quick look | (with BT4) | ~13 core-h | ~2 h on 8 cores | a training window |

Nothing timed during a training window: the table's searches can run then, the match can't.

## Decision BT: what to do

**BT1. What to build first**
- (a) Matthew's words exactly: every legal position after 5 half-moves, lines losing a piece blocked, each leaf
  searched to depth 10, values backed up by minimax as the tables do. ~100 core-h.
- **(b) The same reach, all of theirs and our best** (my pick): 754 of our positions, each searched to depth 18,
  their piece-losing replies blocked. ~85 core-h with BT3 (a)'s check, one training window. The same kind of book as
  (a), ~3 plies deeper for less CPU. Then, if it's kept, Black's third move (6,620 positions).
- (c) Aim at the measured weak spot: re-search our book moves as Black in the positions our games reach (the
  defences, and the lines out to move ~8), and drop those ≥ 50 cp below our best. ~10-30 core-h. Cheaper and closer
  to where the measured gap is, but not Matthew's table.
- (d) The margin book to 14 plies at 30 cp, ~10,000 positions. Later, if (b) or (c) shows anything.

**BT2. How a position is scored**
- **(a) A deep search of our engine** (my pick), as the learner does: deterministic and cheap.
- (b) Self-play games: 200-300x the CPU a position, and noisier.
- (c) Stockfish's searches instead of ours: a stronger judge, but its best move isn't necessarily the one our
  engine plays best from.

**BT3. Where it goes, and variety**
- **(a) First in the chain, keeping to the human book's moves within 20 cp** (my pick), one move a position.
- (b) First, our best always: purer, but our book often ends at move 3.
- (c) First, up to 3 moves within 10 cp, weighted: keeps some variety, costs one search a move kept.

**BT4. How it's judged**
- **(a) The three-arm match against Stockfish with the wide book** (my pick), after the quick look.
- (b) The quick look only (exit positions, no games): cheap, but it doesn't measure results.
- (c) The bot itself, a few days on the new chain: ±50 Elo in 600 games, a sanity check at most.

**BT5. Rebuilding**
- **(a) With each new net, only if the first table is kept** (my pick): ~11 h of a training window each time.
- (b) Once, and leave it.
- (c) Bit by bit, like the learner: re-search a position when the bot reaches it on a new net.
