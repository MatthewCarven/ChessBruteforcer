# BT1 (c): the human book's Black lines, vetted (2026-10-23/24)

Matthew's option (a) from [BOOK-TABLES.md](../../../BOOK-TABLES.md): re-search the book moves the bot plays as Black,
tiers 1-2 at depth 16, the ones 50 cp or more behind our best confirmed at depth 18, and drop those. Scripts:
`scripts/book-tables/vet-c-*` (positions, search, desktop driver, Stockfish, book, summary). Numbers from
vet-c-summary.py over the result files here.

## The short answer

1. **57 book moves vetoed, in 48 positions**, out of 1,445 moves checked in 538 positions. Stockfish agrees: **84% of
   the vetoes are 50+ cp behind its own best too** (98% are 25+), against 4% of a random 50 of the moves we kept.
   So these are poor human moves, not our net's misjudgement.
2. **The vetted book**: `games\lichess-2200-vetted-black.bin` in the main checkout (not in git, 12.9 MB): 56 entries
   removed, 1 added (one position lost both its moves and has our best, 6...Nf6, alone). The 57th veto was a move the
   bot played from an older book; today's book doesn't have it.
3. **It fixes little of Black's problem.** The vetoed moves are 15% of all the centipawns the book gives away in the
   network-era Black games. The rest is spread over many moves 10-49 cp behind. Along the lines of the 68 games that
   left book at -0.50 or worse, the book's moves gave away a median **56 cp**; along the other 74, **29 cp**.
4. **Stockfish sees the same bad exits**: Black's median exit is -0.36 by Stockfish (depth 20) against -0.48 by our
   engine in the game, a median paired difference of only -0.03. The French is -0.77 by Stockfish (-0.84 ours), the
   Modern -1.03 (-0.99). So the low exits are the lines, not our net.
5. **The vetted book changes about 3% of Black games** (13 of the last 144 played a now-vetoed move; with today's
   weights I estimate ~3% would). At ~0.7 pawn a veto and +0.145 of a game a pawn (exitworth's slope), that's
   ~0.3% of a point in Black's games, ~2 Elo there and ~1 overall: too little for a match to see, and cheap enough to
   keep if it isn't worse (BOOK-TABLES section 4's rule for (c)).

## What was checked

| | positions | moves checked | what |
|---|---:|---:|---|
| tier 1 | 271 | 741 | on the book lines of the 64 network-era games where the bot was Black and left book at -0.50 or worse |
| tier 2 | 267 | 704 | on the lines of the other network-era Black games |
| (tier 3, not run) | 262 | 739 | older games and the book's likely continuations |

Positions: Black to move, our moves 1-8, where lichess-2200.bin (first in the chain) has a move. Moves checked: every
book move with 5%+ of the position's weight, and any move the bot played there (any era). For each position our
engine's best at depth 16, then each other move scored by searching the position after it to depth 15 and negating
(no MultiPV or searchmoves, as rescore.py). The 81 moves 50+ cp behind were re-searched the same way at depth 18 (17);
57 stayed 50+ behind. h1536-3m, one thread, Hash 64, no tables, a fresh game each search.

Where it ran: a timing on the laptop beside the bot (8 positions at 16, 1 at 18, 2026-10-23 00:44-01:07), then the
desktop's CPU on Matthew's yes (6 engines, bt-a's SSE build, a scheduled task, 2026-10-24 03:14-12:58). The desktop
matched the laptop exactly on 5 re-searched positions (15 rows: move, score, nodes). Engine time: 41.2 h for the
screen and 16.8 h for the confirm, on the desktop's slower cores (~2.5x a laptop core), ~20 laptop core-hours in all.
Stockfish 16 in WSL (depth 20, one thread) on the laptop afterwards, 17 minutes.

## Which defences lose most

Network-era games as Black (3+ games), the exit evaluation (our engine, first move out of book) and the book's loss
summed along each game's line (depth 16, our moves 1-8):

| defence | games | median exit (pawns) | median book loss on the line (cp) | games through a veto | worst move seen (cp at depth 16 / 18) |
|---|---:|---:|---:|---:|---|
| Modern | 5 | -0.99 | 50 | 0 | 2...Nf6 (36) |
| French | 11 | -0.84 | 51 | 0 | 3...b6 (51 / 24) |
| Pirc | 3 | -0.84 | 57 | 1 | 7...dxc5 (79 / 88, vetoed) |
| Scandinavian | 4 | -0.62 | 56 | 0 | 4...Qa5 (63 / 49) |
| King's Indian | 8 | -0.57 | 38 | 0 | 7...c6 (52 / 34) |
| English | 6 | -0.54 | 58 | 1 | 3...g6 (53 / 64, vetoed) |
| Sicilian | 36 | -0.44 | 26 | 4 | 7...Qa5 (76 / 64, vetoed) |
| Caro-Kann | 8 | -0.36 | 44 | 0 | 5...Nc6 (50 / 32) |
| Queen's Gambit Declined | 7 | -0.33 | 19 | 0 | 5...Nc6 (35) |
| Queen's Pawn Game | 4 | -0.14 | 32 | 1 | 2...f6 (119 / 119, vetoed) |
| Ruy Lopez | 9 | -0.12 | 31 | 0 | 6...Bg7 (38) |

The defences with the worst exits lose ~50-58 cp along their lines, the better ones 19-31, and almost none of it in a
single vetoable move: the French's 11 games never cross a veto. **Much of it is the first move.** After 1.e4 our engine's
best is 1...e6 (-0.26 at 16, -0.34 at 18), and the book's 1...d5 is 44 cp behind it, 1...g6 30, 1...c6 20, 1...c5 7.
Summed over the games that played them, the costliest book moves:

| our move | move | cp behind each | games | of them exiting -0.50 or worse | cp in all |
|---:|---|---:|---:|---:|---:|
| 1 | ...c5 (v 1.e4) | 7 | 35 | 16 | 245 |
| 1 | ...d5 (v 1.e4) | 44 | 4 | 2 | 176 |
| 1 | ...g6 (v 1.e4) | 30 | 5 | 5 | 150 |
| 1 | ...c6 (v 1.e4) | 20 | 7 | 3 | 140 |
| 2 | ...f6 (1.d4 g6 2.Nf3; vetoed) | 119 | 1 | 1 | 119 |
| 2 | ...d6 (Sicilian) | 6 | 16 | 8 | 96 |

(All 6,276 cp over these games: the top 15 moves are 27%, the vetoed ones 15%.) The French is the odd one: our engine's
own favourite first move, yet among the worst exits, with no veto on its lines. Stockfish puts its exits at -0.77 too,
so its later book moves (or the bots' answers to them) are what cost.

## Stockfish's word

| | moves | Stockfish 50+ cp behind its best | 25+ | median |
|---|---:|---:|---:|---:|
| our vetoes | 57 | 48 (84%) | 56 (98%) | 71 cp |
| moves we kept (random 50 that were behind our best) | 50 | 2 (4%) | 17 (34%) | 18 cp |
| our replacement (our best at depth 18), in the vetoed positions | 57 | | | 0 cp |

Black's exits, our engine in the game (depth ~14, the net of the day) against Stockfish at depth 20 (pawns, Black's
view; 142 games): median -0.48 against -0.36, paired difference -0.03. By defence the difference is -0.13 to +0.02
everywhere; no defence where our engine alone thinks Black is worse.

## Caveats

- Noise: 108 of the 1,445 moves scored a little better than our "best" (a separate search; down to -32 cp, only 2 at
  -20 or below), so the 50 cp line is ~2-3 noise widths out. 1...b6 after 1.e4 went from 58 at depth 16 to 47 at 18.
- Depth 16 alone isn't enough to veto on: of the 81 moves flagged there, 24 fell under 50 at depth 18, some by ~25 cp
  (the French's 3...b6 went 51 to 24, the King's Indian's 7...c6 52 to 34). It can miss the other way too: a move 40-49
  behind at 16 (59 of them) could be 50+ at 18.
- The game evaluations at the exits came from four nets over time (nnue1 to h1536-3m), at ~depth 14 under the clock.
- Tier 3 (older games, continuations the bot hasn't reached yet) and White's lines weren't checked.

## Decision C: what next (Matthew's pick)

- **(a) Measure the vetted book as it stands** (my pick): it's ready for the scoping session's quick look (exit
  positions with each book) and a BT4 arm. Expect no visible difference; keep it if it isn't worse.
- (b) Draw the line tighter, at 25 cp: 227 more moves in 165 positions (34% of a position's weight in all, 116 of the
  bot's played moves), from the depth-16 numbers already here; a depth-18 confirm of them is ~7-8 h on the desktop's 6
  engines, and needed (the swings above are as big as the line). It cuts the book's width, and more positions end with
  our best alone.
- (c) Leave Black's first two moves to BT1 (b)'s table: the biggest single cost is the first move (1...d5, 1...g6,
  1...c6 against 1.e4), and (b) plays our best there.

## Files

| file | what |
|---|---|
| vet-c-positions.csv | the 800 positions (tiers 1-3), their book moves and weights, the moves vetted, games through them |
| vet-c-screen.csv / vet-c-confirm.csv | every search: depth 16 (1,983 rows), depth 18 (146 rows) |
| vet-c-vetoes.csv | the 57 vetoes: position, move, share, losses at 16 and 18, our best, Stockfish's loss, in today's book |
| vet-c-sf-vetoes.csv / -controls.csv / -exits.csv | Stockfish at depth 20 |
| main checkout games\lichess-2200-vetted-black.bin | the vetted book (sha256 6037702a...ab692), never bot\books |
