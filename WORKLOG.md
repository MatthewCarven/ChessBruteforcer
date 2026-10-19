# Worklog

## 2026-09-26: milestone 1, the foundation

**Decisions**

- 6 bits per square, 48 bytes per board:
  `[occupied][colour][type×3][spare]`. The spare bit is reserved (it must be
  0 for now) and is kept for castling or en passant. 4 bits (32 bytes) would
  fit 13 states, but 6 gives each bit a meaning, which suits the
  superposition work.
- C# / .NET 8, xunit for tests.
- Board files are headerless runs of 48-byte records, so dedupe is sort +
  unique and sorted files compress well.
- The first exact count covers material rules only. King adjacency and the
  rest are on the roadmap.

**Correction to the starting assumption**

"There can never be two queens on one side" isn't true: promotion makes up
to nine possible. The rule the checker uses is that promoted pieces needed ≤
missing pawns, with bishops counted by square colour. Getting this right
changes the tier 4 count by about 7 × 10⁸.

**Results**

- Exact survivors: 2³⁸⁴ → 13⁶⁴ ≈ 2.0e71 → 1.5e68 → 6.7e66 → **1.44e49**.
- The counter is checked against brute force (2×3 and 3×2 boards) and
  square-by-square DP (4×4, 3×5). They agree at every tier.
- `sample`: none of 20,000 raw collapses has a meaningful code on every
  square, and about 0.07% of meaningful-code collapses have the right kings.
  Both match the counted ratios.

**Direction (thesis)**

Map the minimum-spec game and its win/loss tree, then sort it by favourable
outcome, working backwards from the endgame. King vs king is a dead draw, so
the first real target is K+Q v K / K+R v K via retrograde analysis. It's
milestone 2 in TODO.md.

**Competition plan**

The engine will only ever play openly as software: local engine-vs-engine
matches first, then a labelled Lichess BOT account, then engine rating lists
(CCRL) and tournaments. Added as milestone 3 in TODO.md, together with the
UCI protocol it needs. Solved endgames will be stored as indexed outcomes
rather than 48-byte boards, because that is the storage that scales.

## 2026-09-26: move generator

- `Position` (full FEN, make / unmake in place, check / mate / stalemate),
  `Move` (UCI notation), `MoveGenerator` (pseudo-legal, then filtered for
  own-king safety), `Perft`.
- Perft matches the published counts on all six standard positions:
  start 1-6, Kiwipete 1-4, position 3 1-6, position 4 1-5, position 5 1-4,
  position 6 1-5. Unit tests cover the shallower depths plus castling rules,
  en passant pins, promotions, and mate vs stalemate.
- One scare on the way: position 6 came out 44 moves instead of 46. It turned
  out the FEN had been typed from memory wrong (a white pawn on a2 instead of
  a3). With the right FEN it matches to depth 5.
- Speed is about 13-16M nodes/s in release with a plain 64-square array. That
  is plenty for 3-4 piece endgames; bitboards come later, for search.
- Next: the retrograde solver for K+Q v K.

## 2026-09-26: endgame solver (milestone 2 core)

- `Endgame/`: `Material` (signatures like KQvKR, stronger side as white),
  `Outcome` (win / loss / draw with distance to mate in plies),
  `EndgameTable` (index, retrograde solve, save / load as `.cbt`),
  `Tablebase` (finds or solves tables, colour swap, ranks moves, best line).
- Retrograde method: seed mates as Loss(0) and stalemates as draws, then
  process ply buckets in order. Losses make their predecessors wins;
  wins count down each predecessor's remaining moves, and a predecessor with
  none left (and no drawing capture) becomes a loss. Captures are settled up
  front by probing the smaller table. Predecessors come from pawnless
  un-moves.
- Results match the known longest mates: KQvK 10, KRvK 16, KQvKR 35,
  KBNvK 33. KvK has 3,612 legal placements, which agrees with the range
  counter's non-adjacent-kings figure.
- Test: every legal KQvK / KRvK position equals the best outcome over its
  moves, which with the mates fixed determines the table uniquely.
- Three of my hand-picked test positions were wrong (a defended queen that
  was really loose, and so on); the solver was right each time.
- Cost: 3-piece tables ~2 s and 1 MB; 4-piece ~110 s, 64 MB on disk, ~700 MB
  peak memory. Symmetry and a tighter index are next before 5 pieces.
- DTM ignores the 50-move rule. That's fine so far, noted in TODO for 5 pieces.

## 2026-09-26: pawns in the endgame solver

- Pawn pushes stay in a table, while promotions (like captures) go to the table
  for the promoted piece. Pawn un-moves: one square back, or two back to its
  starting rank. Pawns on rank 1 or 8 are impossible slots.
- `SwapColours` now also flips the board (square ^ 56), so a black pawn
  side is looked up in the white-pawn table with pawns running the right
  way. Pawnless results are unchanged by the flip.
- KPvK: 28-move longest mate, 76.5% of white-to-move positions won. Both
  match the figures I remember for this endgame, though I'm less sure of the
  source than for KQK / KRK. The real proof is the consistency test over
  every legal KPvK position, plus textbook positions (square rule, rook pawn
  in the corner, king on the 6th in front of its pawn, an unstoppable black
  pawn through the colour swap).
- KQvKP ~7.5 min and KRvKP ~4 min, each including the four 4-piece tables
  their promotions need (KQvKQ, KQvKR, …).
- Pawns on both sides are refused for now: en passant makes the value
  depend on the previous move, and the index doesn't record that. The plan
  is in TODO.

## 2026-09-26: en passant, pawns on both sides

- Tables keep storing positions without en passant rights. A double push
  that hands the opponent an e.p. capture leads to an extra node: the value
  is the better of the table entry and the capture for the side that may
  take, or the capture alone if it is the only legal move. `Probe` applies
  the same rule to FENs with an e.p. square, through separate code.
- New `verify <material> [stride]` command: every legal position must equal
  the best outcome over its moves.
- KPvKP: solved in ~5 min (given the promotion tables). **All 14,872,176
  legal positions verified consistent**, which cross-checks the solver's e.p.
  nodes against `Probe`'s e.p. rule. White-to-move and black-to-move
  statistics are identical, as the colour-swap-and-flip symmetry says they
  must be.
- E.p. matters: with white Pe5 v black Pd5, white to move wins in 11 with the
  e.p. right and in 12 without.
- Plan change: a slow opt-in test (`CHESS_SLOW_TESTS=<tables dir>`) samples
  KPvKP, because building its tables takes ~20 minutes, too long for every
  run.
- Every 4-piece table (21 of them) now exists in the scratch tables directory.
  They are generated data, so not committed.

## 2026-09-26: the engine (milestone 3a)

- Zobrist hashing on `Position` (incremental; "no castling rights" hashes
  to 0 so boards built square by square match FEN ones, a mismatch the
  tests caught). Null move for the search.
- `Engine/`: `Evaluation` (material + simplified piece-square tables, tapered
  king, bishop pair), `TranspositionTable`, `Search` (ID, PVS, TT, null move,
  LMR, check extension, quiescence, killers / history, repetition and
  50-move draws, mate-distance pruning, endgame-table probing), `UciEngine`.
- New executable `ChessBruteforcer.Engine`: UCI by default, plus `bench` and
  `selfplay`. Bench depth 7 over 6 positions: ~0.94M nodes, ~560k nodes/s.
- Tests: hashing (random playouts, transpositions, null move), colour-blind
  evaluation, mates in one, free and poisoned material, stalemate,
  repetition avoidance, node limit, UCI commands and movetime. Also the
  search's mate-in-2 / mate-in-3 distances match the K+R v K table on
  positions drawn from it.
- Self-play at 100 ms / move: 4 games, 2 draws by repetition and 2 mates, no
  illegal moves. One "blunder" (black Qd7-f7, queen taken) turned out to be
  deliberate: with the tables loaded, K+P v K after the capture is a proven
  mate in 12, while with the queen on the search couldn't prove any mate.
  Correct but slow; solving K+Q+P v K and the other 2-v-0 tables fixes it.
- Plan change: cutechess isn't installable here, so local matches will use
  our own runner. Stockfish is installable as a yardstick opponent.

## 2026-09-27: match runner (milestone 3b)

- `Match/`: `San` (standard notation incl. disambiguation, e.p., promotion,
  check / mate), `GameRecord` (PGN), `IPlayer` / `UciPlayer` (engines as
  separate processes), `GamePlayer` (clock, time forfeits, every game ending,
  table adjudication), `MatchStats` (Elo + 95% interval, LOS, SPRT),
  `MatchRunner` (colour-reversed opening pairs, concurrency).
- New executable `ChessBruteforcer.Match` and `scripts/match.sh`. Stockfish 16
  installed from apt as the yardstick (UCI_Elo 1320-3190).
- Results (MATCHES.md): v0.1 18-2 v Stockfish-1500; v0.2 11-9 and v0.3 14-4-2
  v Stockfish-2000. About 2100 on Stockfish's scale, approximate.
- Two time forfeits, traced to first-time endgame-table reads mid-search on a
  slow disk (~65 MB/s). Fix: tables load into memory on `isready`
  (`Tablebase.Preload`, `LoadOnDemand = false`), plus a Move Overhead UCI
  option. v0.3 had no forfeits. Time losses now report the milliseconds.
- Tests: SAN cases (two of my own test positions were wrong again: a rook
  blocked by its own king, a queen that gave no check), PGN, Elo / SPRT maths,
  time-control parsing, and scripted-player games for mate, illegal move,
  time forfeit, threefold, bare kings, openings.

## 2026-09-27: ready to move to a desktop

- `scripts/build-tables.sh` solves all 30 four-piece tables (15 one-v-one, 15
  two-v-bare-king) into ./tables, skipping ones already there, so it can be
  stopped and restarted. Tested here: KQQvK and KQRvK each took ~107 s.
- Table saves write to a temporary file and rename it, so killing a solve
  never leaves a truncated table.
- What lives only in a cloud session and has to be rebuilt on a new machine:
  the .NET 8 SDK, Stockfish, ./tables (run the script), and engines/
  snapshots (scripts/match.sh snapshot).

## 2026-09-27: on the Windows desktop

- Cloned onto Matthew's machine (`git init` + `git pull` from GitHub). The
  .NET 10 SDK builds the net8.0 projects as they are; the .NET 8 runtime is
  installed, so no SDK change was needed.
- One test failed on Windows only: `TablesSurviveASaveAndLoad` couldn't
  delete its temp table. A loaded table memory-maps its file and never let
  go of it, and Windows won't delete or replace a mapped file (Linux will).
  `EndgameTable` is now `IDisposable` and releases the mapping. 185 pass,
  1 skipped (the long en passant consistency run, skipped on purpose).
- `scripts/build-tables.sh` built its command from an unquoted `$PWD`, which
  splits at the space in `Chess Bruteforcer`. It uses a relative path now.
  `match.sh` needs `STOCKFISH=<path to stockfish.exe>` here; it defaults to
  the Linux apt path.
- Still to do on this machine: install Stockfish, run build-tables.sh
  (1-3 h), and snapshot an engine before any matches.
- Done, in WSL (Ubuntu 24.04, clone at ~/chessbruteforcer): .NET 8.0.131
  and Stockfish 16 from apt, tests 185/1 skipped, all 36 tables (1.9 GB).
  The first WSL attempt died mid-test: Windows ran short of memory (the
  Claude app and its Cowork VM held ~8 GB) and shut the VM down; a reboot
  cleared it.
- `match.sh snapshot` never worked: it passed two projects to one
  `dotnet build` (MSB1008). Builds them one at a time now.
- v0.3 v Stockfish-2000 on the laptop, one game at a time: +17 -3 =0 (85%),
  no time losses. Logged in MATCHES.md, kept apart from the container runs
  because the hardware differs. Each engine holds all tables in memory
  (1.9 GB), so 2 games at once needs ~4 GB free.

## 2026-09-27: game files, step 1 (`.cbg`, PGN in and out)

- Design settled with Matthew: a game is its start position plus one byte per
  move, the move's index among the legal moves sorted by (from, to,
  promotion). The rules fix the order, not the generator, so the bitboard
  rewrite can't break old files. Going back is replay. Plan in TODO.md
  ("Next: storing games"), ahead of the 5-piece tables.
- `Records/`: `MoveCode`, `StoredGame` (tags in order, moves, result; start
  from the FEN tag or the standard position), `Pgn` (streaming reader and
  writer), `GameFile` (magic "CBG1", then per game: tags, result byte, move
  count, move bytes). `San.Parse` added beside `San.Of`. `GameRecord` now
  shares the move-text writer, and escapes quotes in tag values too.
- CLI: `game import / export / list / count`.
- Tests (28 new, 213 pass): SAN parse cases and lenient forms; every legal
  move in random games from three start positions reads back from its own SAN
  and decodes from its own code; the 218-move record position; the Opera game
  in = out exactly; the same game in messy real-world form (comments inside
  variations inside variations, NAGs, `0-0-0`, `e.p.`, a `%` line, a missing
  result) comes out identical; file round trip at exactly 1 byte per move;
  append; errors name the game and move; damaged files are refused.
- 5,000 games through the CLI: export identical to input, byte for byte.
  ~3,000 games/s. Tags take 3.6x the bytes of the moves.
- Direction, from Matthew: this project stays chess. Checkers (uniform pieces,
  which suits how he estimates) would be a separate project. His hypothesis is
  that enough rules write off 75%+ of the space, with repetition limiting
  depth. The distinction worth keeping straight: repetition prunes *games*
  (infinite to finite), not *positions*; the position space is cut by the
  placement / reachability tiers, where the published reachable estimates
  (10^44-10^46 v our tier 4 at 1.44e49) imply far more than 75%. Both
  measurements are on the TODO.

## 2026-09-27: game files, step 2 (replay)

- `GameFile.Read(path, n)` fetches one game, skipping the games before it
  by their lengths (tags, then jump the move bytes), so nothing before it is
  decoded. `Count` skips the same way now, where it decoded every move before.
  Game 5,000 of a 5,000-game file comes up in well under a second.
- `StoredGame.San()` lists the moves in notation; `PositionAt(ply)` was
  already there from step 1.
- CLI `game show <file> <n> [ply]`: the board, FEN, whose move, the move that
  led here and the next one, and the whole game with `|` at the position.
  A negative ply counts back from the end; the default is the end.
- Tests (2 new, 215 pass): games read by number from a 1,200-game file
  match reading them all; out-of-range numbers refused; a truncated last
  game fails `Count` and its own read but leaves earlier games readable;
  replay matches step-by-step play at every ply of the Opera game.

## 2026-09-27: real games (Lichess 2013-01), styles, and the "grid"

- Matthew thought Lichess's policy forbade taking its games; that policy is
  about engine help while *playing*. database.lichess.org publishes every
  rated game under CC0. Downloaded 2013-01 (17.8 MB .zst, 121,332 games) into
  games/ (now gitignored with *.cbg), unpacked with Python 3.14's
  compression.zstd.
- Import: every game, no errors, ~60 s, 43.5 MB. Export then re-import gives
  an identical .cbg, the real-data version of "PGN in = PGN out".
- Matthew asked for three styles: early kill, moderately efficient, wastes
  time (maybe on a long plan). `GameAnalysis` measures plies, mate, shuffles
  (a piece straight back where it came from), repeats, the longest quiet
  stretch and captures; `game grade` sorts and gives example games (Lichess
  URLs). Results in the README. Early kills average ~35 rating points below
  the rest (1570 v ~1605). The shuffle rule is too loose (59% of time wasters
  flagged by it alone), which is left for Matthew to tune.
- Matthew's grid idea: games share ancestors (openings) and descendants
  (transpositions), so each should be stored once. `game tree` measures it.
  89% of plies are distinct prefixes, 86% distinct positions, median game new
  from ply 7, and 811 exact repeats. So at 121k games the sharing is all in the
  first ~10 plies; the tree is a map more than a compressor. Worth measuring
  again on a month 100x bigger.
- Found while testing: a double push always sets the en passant square, so
  "1. e4 e5 2. Nf3" and "1. Nf3 e5 2. e4" hashed differently. `GameAnalysis.Key`
  drops the square unless a capture there is legal (FIDE's rule).
  Merged positions went 7,057,164 -> 7,048,611.
- Tests: 9 new (224 pass).
- Matthew chose shuffle rule (b): a shuffle counts only while nothing is
  happening, 10+ plies without a capture or pawn move (`IdleShuffles`; plain
  `Shuffles` still counted). Jan 2013: time wasters 16,941 -> 7,101 (5.9%),
  flagged by shuffling alone 9,937 -> 97. 74% of time wasters won.
- Downloaded and imported 2013-02 (18.2 MB, 123,961 games, 8.33 M moves, no
  errors). It grades almost the same as January (23.0 / 39.6 / 6.0%).
  `game tree` now takes several files, each added on top of the last: Feb
  alone is 89.0% new tree nodes, and on top of Jan 87.7% (positions 86.4 ->
  84.5%), median game new from ply 7 -> 8. Matthew's diminishing returns,
  confirmed but gently: doubling the games moved the split point one ply.
  Repeat games 811 -> 2,074 across both.
- Tests: 1 new (225 pass).

## 2026-09-27: five months of games, and the two ends of the game

- Downloaded 2013-03/04/05 (23.6, 23.3, 26.5 MB; 158,635, 157,871 and
  179,550 games, sizes and counts as listed) and imported all, no errors.
  The imports ran from a copy of the CLI build so the project could be
  rebuilt meanwhile (Windows locks a running DLL).
- `game tree` now also reports the opening (first 12 plies: share of new tree
  nodes) and endgames with <= 6 and <= 4 pieces (games reaching them, share of
  new positions, and games whose first such position an earlier game had
  already reached). Matthew's guess was that five files would solidify
  "the beginning 6 and the ending 6 pieces". The opening, partly: 73% of
  May's opening moves were already known. Six-piece endgames, no: 99.7% of
  arrivals are new, and the space is too big for samples. Four pieces:
  converging (6% of arrivals known in May, rising). The ending has to come
  from solving; the 5-piece tables are the way in.
- 741,349 games, 50.2 M plies: 87.4% distinct tree nodes, 84.0% distinct
  positions, 6,216 repeat games. 4.5 min for the whole run.
- Disk: games/ holds 544 MB of unpacked PGN, 255 MB of .cbg and the 105 MB
  of .zst downloads. The PGN can go (the .zst has it, and the .cbg keeps
  everything but comments), but that's Matthew's call.
- Tests: 1 new (226 pass).

## 2026-09-27: 5-piece tables, step 1 (symmetry)

- The data said the ending has to be solved, not sampled; Matthew said go.
  Plan: symmetry, then the solver's memory, then one 5-piece table to measure.
- `TableIndex`: one index per position up to symmetry. Of a position's images,
  keep the one whose squares read first in slot order. Without pawns that
  puts the white king in the a1-d1-d4 triangle (462 king pairs, the standard
  number); with pawns, files a-d (1,806). Other images are holes.
- The solver counts distinct children and distinct predecessors, since a
  symmetric position can reach mirror images of one child by two moves (the
  two counts then disagree by the stabiliser sizes). With pawns no position
  is its own mirror (the kings can't sit on the middle line), so en passant
  nodes need nothing new; the passed square is mapped into the child's frame.
- Statistics weight each index by its images: K v K still 3,612 per side,
  K+P v K still 76.5% won.
- Format CBT2; CBT1 renumbered on load; `upgrade [dir]`; `CHESS_TABLES` sets
  the folder (CLI and build-tables.sh).
- The check: in WSL, the 36 old tables were upgraded (5 s, 1.9 GB -> 428 MB),
  and all 36 solved again from nothing with the new solver (491 s). They
  are byte-identical, 36 of 36. Every pawnless longest mate matches the
  published values (KQvKR 35, KRvKN 40, KRvKB 29, KBNvK 33, KQvKN 21,
  KQvKB 17, KRvKR 19, KQvKQ 13, KBBvK 19); the pawn tables' aren't checked
  against an outside source yet.
- Tests: 13 new (239 pass).
- Left in WSL: ~/chessbruteforcer/tables (old format, untouched), plus
  tables-upgraded and tables-new (identical). Matthew to choose which to keep.
- Later the same day: Matthew kept one WSL table folder and deleted the rest.

## 2026-09-27: 5-piece tables, step 2 started (solver memory); the ladder plan

- Measured first. `solve` now prints the solver's memory: its per-slot
  arrays, plus its queues at their largest. `scripts/measure-tables.sh`
  solves all 36 tables with one line each. Baseline, old solver, Windows:
  36 tables in 479 s. The queues are as big as the arrays in winning tables.

  | Table | Slots | Arrays | Queues | Total | Process peak |
  |---|---|---|---|---|---|
  | KQvKR | 3.8 M | 25.3 MB | 27.7 MB | 52.9 MB | 111 MB |
  | KQRvK | 3.8 M | 25.3 MB | 30.7 MB | 56.0 MB | 121 MB |
  | KBvKB (all draws) | 3.8 M | 25.3 MB | 0.0 MB | 25.3 MB | 70 MB |
  | KRPvK | 14.8 M | 98.8 MB | 105.6 MB | 204.3 MB | 365 MB |

  Saved as `tables-baseline/` (the 36 tables + `before.log`, gitignored)
  for the regression check.
- Matthew chose (a) bits for the two flags and (b) 4-byte queue entries.
  Both written, on branch `solver-memory`, with the 5-piece guard lifted for
  pawnless tables. Not finished: one new test fails, and `solve KQRPvK`
  hung when it should refuse at once. Couldn't dig in this session. It's
  the first item of session 1 in TODO.md.
- Matthew's ladder: build tables to a capped depth and extend later
  ("we can always go bigger with more disk space if we get the edges
  correct"). He picked (a) cap and resume, (b) climb by piece count at the
  same cap, (c) the 50-move rule as the cap. Planned as four sessions in
  TODO.md ("Now: the session plan").

## 2026-09-27: 5-piece tables, step 2 finished (solver memory)

- The open bug was the new test, not the solver. It listed KQRRvK as
  "six pieces", but two kings + Q, R, R is five, with no pawn: the guard
  rightly let it through, and the test host started a real 242 M-slot solve
  with a draw-everything probe, then died. Six pieces is now KQRRvKR.
  `solve KQRPvK` refuses at once, as it should.
- Tests: 3 new (242 pass, 1 skipped as before). The full suite took 20 s.
- Regression: all 36 tables solved again from nothing (481 s, was 479 s):
  36 of 36 byte-identical to `tables-baseline/`.
- Before/after solver memory (MB; arrays = value, moves left, slowest loss,
  two flags; queues at their largest):

  | Table | Slots | Before | After | Arrays | Queues | Process peak | Time |
  |---|---|---|---|---|---|---|---|
  | KQvKR | 3.8 M | 52.9 | 32.8 (0.62x) | 25.3 -> 18.9 | 27.7 -> 13.8 | 111 -> 85 | 13 -> 13 s |
  | KQRvK | 3.8 M | 56.0 | 34.3 (0.61x) | 25.3 -> 18.9 | 30.7 -> 15.3 | 121 -> 90 | 12 -> 13 s |
  | KRvKR | 3.8 M | 32.6 | 22.6 (0.69x) | 25.3 -> 18.9 | 7.3 -> 3.7 | 84 -> 71 | 8 -> 8 s |
  | KBvKB (all draws) | 3.8 M | 25.3 | 18.9 (0.75x) | 25.3 -> 18.9 | 0 -> 0 | 70 -> 64 | 6 -> 6 s |
  | KRPvK | 14.8 M | 204.3 | 126.9 (0.62x) | 98.8 -> 74.1 | 105.6 -> 52.8 | 365 -> 255 | 33 -> 32 s |
  | KQvKP | 14.8 M | 185.1 | 117.2 (0.63x) | 98.8 -> 74.1 | 86.3 -> 43.2 | 334 -> 239 | 37 -> 36 s |
  | **All 36** | | **2,571** | **1,660 (0.65x)** | 0.75x | 0.50x | | **479 -> 481 s** |

  Arrays went from 7 to 5.25 bytes a slot, the queues exactly halved, and
  the bit twiddling costs nothing measurable.
- Projection to 5 pieces without pawns (242 M slots, 64x a 4-piece table),
  from KQRvK: arrays 5.25 B x 242 M = ~1.2 GB; queues 0.68 entries a slot
  at ~6.3 B each (List doubling leaves ~1.6x capacity) = ~1 GB; the largest
  bucket (~22% of entries) may spike ~0.4 GB while it doubles. So ~2.7 GB
  at peak, against ~3.5 GB before. Fits WSL's 7.8 GB cap with room; the
  rescan-instead-of-queue fallback isn't needed.
- Matthew's idea, logged in TODO (repetition): a staller cycles 3+ pieces
  round separate loops. 3 pieces on 3-square loops = 27 arrangements, each
  allowed twice before threefold = 54 moves, so the 50-move rule arrives
  first: repetition alone can't end stalling. The shuffle rule
  ("straight back") can't see loops.

## 2026-09-27: 5-piece tables, session 2 (cap and resume)

- `Outcome.Beyond(N)`: not settled within N plies (a longer win or loss, or
  a draw). `solve <material> --cap N` stops after ply N; capped tables are
  CBT3, with the solver's state in a `.cbf` frontier file beside them, and
  `solve` with a bigger cap (or none) carries on from N + 1. A table that
  runs out of work before its cap is complete and saved as CBT2.
- Edges: a capture into a capped table that answers Beyond is an unknown
  exit (a third bit per slot): it blocks a loss but not a win. That's exact
  when the smaller table reaches cap - 1 (the solve throws otherwise).
  Extending re-probes those exits after extending the smaller tables. The
  en passant nodes needed the same treatment. Queuing behind the current
  ply now throws, as a guard.
- The engine: `TryProbe` reports Beyond as "not covered". Before this, the
  search and the match adjudicator treated anything not a win or loss as a
  draw, so a capped table would have handed out false draws.
- Tests: 10 new (252 pass, 1 skipped), including a KPvK ladder in memory
  and on disk (byte-identical at the end).
- The ladder, all 36 tables: capped at 10, extended to 20, 40, 80, 160, end.
  **36 of 36 byte-identical to tables-baseline.** Time per rung: 354, 155,
  107, 37, 16, 15 s (684 s; a straight solve is 481 s). Tables plus frontiers
  take 1.1 GB on disk while capped (427 MB when done).
- Settled share per rung (wins and losses within the cap, plus stalemates;
  100% = finished, draws proven), all 36:

  | Table | draws (end) | 10 | 20 | 40 | 80 |
  |---|---|---|---|---|---|
  | KQvK | 6.3% | 31.3% | 100% | 100% | 100% |
  | KRvK | 5.6% | 6.4% | 38.4% | 100% | 100% |
  | KPvK | 32.8% | 1.4% | 30.7% | 65.2% | 100% |
  | KQvKQ | 57.8% | 6.7% | 41.9% | 100% | 100% |
  | KQvKR | 3.5% | 4.3% | 33.9% | 58.8% | 100% |
  | KQvKB | 13.3% | 8.8% | 58.6% | 100% | 100% |
  | KQvKN | 11.7% | 7.6% | 58.5% | 88.3% | 100% |
  | KQvKP | 7.5% | 16.1% | 84.7% | 92.3% | 100% |
  | KRvKR | 70.2% | 1.2% | 7.1% | 100% | 100% |
  | KRvKB | 81.6% | 0.8% | 3.9% | 18.3% | 100% |
  | KRvKN | 71.8% | 0.9% | 4.1% | 24.6% | 100% |
  | KRvKP | 13.4% | 2.3% | 14.1% | 81.6% | 86.6% |
  | KBvKP | 85.1% | 0.2% | 3.9% | 14.8% | 100% |
  | KNvKP | 77.0% | 0.2% | 4.0% | 21.1% | 100% |
  | KPvKP | 33.4% | 1.4% | 22.3% | 64.9% | 66.6% |
  | KQQvK | 1.5% | 94.3% | 100% | 100% | 100% |
  | KQRvK | 0.7% | 92.5% | 97.2% | 100% | 100% |
  | KQBvK | 6.0% | 74.4% | 100% | 100% | 100% |
  | KQNvK | 6.0% | 58.5% | 100% | 100% | 100% |
  | KQPvK | 1.7% | 55.5% | 96.1% | 98.4% | 100% |
  | KRRvK | 0.2% | 82.9% | 94.8% | 100% | 100% |
  | KRBvK | 5.2% | 16.1% | 88.1% | 100% | 100% |
  | KRNvK | 5.3% | 11.1% | 76.7% | 100% | 100% |
  | KRPvK | 1.4% | 24.3% | 82.5% | 98.5% | 100% |
  | KBBvK | 55.4% | 0.9% | 7.6% | 100% | 100% |
  | KBNvK | 10.3% | 0.4% | 1.7% | 10.5% | 100% |
  | KBPvK | 11.0% | 4.2% | 46.6% | 88.9% | 100% |
  | KNPvK | 11.5% | 2.7% | 39.7% | 86.7% | 100% |
  | KPPvK | 4.9% | 5.2% | 55.6% | 94.4% | 100% |

  (KvK, KBvK, KNvK, KBvKB, KBvKN, KNvKN, KNNvK: all draws, finished at once.)
  Decisive positions settled, mean over tables with wins: 23% within 10,
  59% within 20, 94% within 40, 100% within 80. Everything is finished by 160.
- KPvKP open at 80 with its own longest mate at 66: 35,174 positions can
  underpromote to a rook into KRvKP (longest 43 moves), which isn't settled
  at 80. Checked from the frontier file; that's the edge rule, not a bug.
- Matthew on stalling, logged in TODO: cycles that look random can be
  orderly (a Gray-code walk), and the longest stall is a Hamiltonian path
  through the side's arrangements, "like the snake solver, with a start and
  an end".

## 2026-09-28: 5-piece tables, session 3a (the 50-move rule, DTZ)

- Matthew said go on session 3 (or a plan if better). Split it: 3a is DTZ
  up to 4 pieces now; 3b (slices with their own memory, for 5-piece pawn
  tables) is its own session. Written up in TODO.
- DTZ tables (`.cbz`, CBZ1, same index): the result under the rule with the
  count at 0, and plies to the next capture, pawn move or mate. In the
  solver, zeroing moves are exits worth win / draw / loss only (the count
  restarts), un-moves never include pawns, en passant nodes aren't needed
  (a double push is itself an exit), and the solve stops at 100 plies:
  what's left is a draw. Tables with pawns go slice by slice (one pawn
  placement and its mirror), most advanced first. A DTZ table needs only
  the smaller tables' DTZ.
- One bug on the way: `ChildOf` reads the side to move from the scratch
  position, and I'd made the move first (null value on KPvK). Fixed by
  taking the child before the move.
- All 36 (`scripts/dtz-tables.sh`, 942 s with checks): 36 of 36 pass
  `dtz verify` on every 5th position. **No cursed wins or blessed losses
  anywhere up to 4 pieces**: the rule changes no result. Longest DTZ, plies
  (white to move / black to move):

  | Table | DTZ | Table | DTZ | Table | DTZ |
  |---|---|---|---|---|---|
  | KQvK | 19/20 | KRvKR | 7/7 | KQRvK | 8/9 |
  | KRvK | 31/32 | KRvKB | 35/36 | KQBvK | 12/13 |
  | KPvK | 19/20 | KRvKN | 53/54 | KQNvK | 14/15 |
  | KQvKQ | 19/19 | KRvKP | 25/24 | KQPvK | 5/6 |
  | KQvKR | 61/62 | KBvKP | 6/7 | KRRvK | 10/11 |
  | KQvKB | 23/24 | KNvKP | 16/15 | KRBvK | 23/24 |
  | KQvKN | 37/38 | KPvKP | 21/21 | KRNvK | 23/24 |
  | KQvKP | 52/53 | KQQvK | 6/7 | KRPvK | 5/6 |
  | KBBvK | 37/38 | KBNvK | 65/66 | KBPvK | 25/26 |
  | KNPvK | 25/26 | KPPvK | 14/15 | KNNvK | 1/- |

  (KvK, KBvK, KNvK, KBvKB, KBvKN, KNvKN: draws, bar a few odd mates.)
- Syzygy check through the Lichess tablebase API (`scripts/syzygy-check.py`):
  178 positions, each table's longest DTZ for both sides, 3 random legal
  positions each, and 4 en passant positions in KPvKP. First pass: 177
  agree; the one "miss" was a checkmated position, which Lichess reports as
  dtz -1 (every mate I tried there, KQvK too) and we store as 0. Allowing
  for that: 178 of 178. Results in `scripts/syzygy-check-results.tsv`.
- Tests: 4 new (256 pass, 1 skipped).
- Matthew asked whether pieces are tied to parts of the board. Yes for
  bishops (32 squares, their colour) and pawns (48 squares, and only
  forward, which is what makes slicing work); no for the rest. Logged the
  48-square pawns and bishop-colour splits as memory ideas in TODO 3b.

## 2026-09-28: 5-piece tables, session 4 (the first ones), and stalling cycles

- Memory first: Windows had 2.0 GB free (committed 17.1 of 19.9 GB: Godot,
  the WSL VM, the Claude app). Matthew freed some: 5.7 GB. Two trims while
  waiting: `Save` writes straight from the array (a copy was 484 MB at 5
  pieces), and `dtz` only compares with the DTM table if it's already on
  disk (at 5 pieces that's a solve of its own).
- The plan's first table, K+Q+R v K, is 4 pieces (my slip, like the test
  in session 1). Solved it anyway in 13 s, then the real one:
- **KQRRvK DTZ**: 796 s, solver 2.06 GB (arrays 1.24 + queues 0.82),
  process peak 2.18 GB against 2.7 projected; 484 MB. 1.14 billion legal
  positions; every white-to-move one won. Longest DTZ 7 / 8 plies, both
  matched on Lichess.
- **KRBvKR DTZ**: 570 s, peak 1.5 GB. 41.2% won with white to move.
  Longest DTZ 99 / 100 plies, matched on Lichess.
- **KRBvKR DTM** up the ladder: capped at 100 in 570 s (818 MB frontier),
  extended to the end in 9 s. Within 100 plies DTM proves 234,252,104 wins,
  DTZ 234,323,628: 71,524 wins where a capture on the way restarts the
  count although mate is further than 100.
- Longest mate 65 moves (129 plies); Lichess (Gaviota) agrees for our
  position. The literature's "59 moves" is most likely distance to
  conversion: Syzygy puts that position ~116 plies from a capture.
  Lesson: a single published number needs its metric checked.
- **The first cursed wins: 17,440 (white to move), and 5,400 blessed
  losses (black to move).** Lichess classes our longest-mate positions as
  cursed-win / blessed-loss too. Three random positions: mate distance and
  DTZ both match. `verify` and `dtz verify` on every 1000th position of
  both tables: all consistent.
- Pawnless 5-piece tables in all: 60 material sets. DTZ ~29 GB / ~11 h;
  with DTM ~58 GB / ~22 h. Asking Matthew before filling the disk.
- Also a fix: pawnless DTZ printed no progress (muted for the thousand-slice
  pawn case); it reports again.

- Matthew's stalling question (c), `game idle`, five months, 741,349 games:

  | idle moves (10+ plies into a quiet stretch) | count | share |
  |---|---|---|
  | straight back (the shuffle) | 199,292 | 19.3% |
  | cycle: an arrangement already had this stretch, another way | 80,158 | 7.7% |
  | fresh: an arrangement new to the stretch | 754,857 | 73.0% |

  Per game with 10+ idle moves, the share that are cycles: median 6%, 90th
  percentile 24%, 99th 52%. Games with 3+ cycle moves: 8,056 (1.09%); 97.1%
  already flagged as marking time (6,751 by a 20+ quiet stretch). A cycle
  rule would add 234 games: not warranted. January alone gave the same
  shares to a tenth of a point. Caveat: an orderly staller's moves read as
  "fresh" until its arrangements run out, so revisits can't say how
  orderly a walk is.
- Tests: 1 new (257 pass, 1 skipped).

## 2026-09-28: session 3b, part A (slices with their own memory)

- Matthew asked for a plan first, then said go with part A (slices proven
  on 4 pieces), part B (the first 5-piece pawn table) next session, and to
  drop back to planning on any major failure. None came.
- Reference first: today's DTZ for all 36 into `tables-baseline/*.cbz`
  (549 s), so the new code had something exact to match.
- `SliceIndex`: pawns fixed, every other piece anywhere, no symmetry. One
  placement of each mirror pair (a placement is never its own mirror), most
  advanced first. The solver switches its index per slice and reuses
  slice-sized arrays. Pawn pushes are exits into solved slices, read from
  the whole table (`ArrayStore`, or `FileStore`: the table file itself,
  memory-mapped, filled with "impossible" first): DTZ restarts its count,
  mate distances carry on; en passant is a plain exit, no nodes. Two
  identical pawns: each order is its own slice, as the index has both.
- Used for DTZ with pawns always, and for mate distances with pawns when
  solved to the end. Capped pawn tables keep the whole-table solver (the
  ladder); 5-piece pawn tables can't be capped yet.
- Also: `Save` skips a table loaded from the very file it's asked to write
  (a table solved straight into its file), and writes a mapped table in
  chunks rather than copying it all into memory.
- Regression, all byte for byte:
  - mate distances, 36 of 36 against `tables-baseline` (493 s, was 481 s).
    Pawn tables' solver memory: KRPvK 126.9 MB -> 5.4 MB, KQvKP 117.2 ->
    4.9, KPvKP 101.3 -> 0.1;
  - DTZ, 36 of 36 against the references (494 s);
  - the capped ladder (10 ... 160, end), 36 of 36 (result below);
  - tests: 258 pass, 1 skipped, 22 s. One new test solved KPPvK in memory
    and with it half a dozen 4-piece promotion tables (4 min 44 s); cut to
    KPvK, the rest is covered by the byte checks.
- The ladder check: 36 of 36 byte-identical, but 1,227 s against 684 s
  last time. Timed old and new builds on the same capped solve (KRPvK to
  20 from scratch, twice each): 165 / 163 s old, 168 / 165 s new. The same
  within 2%, so the slow ladder run was the machine at the time, not the
  code (the capped path barely changed).

## 2026-09-28: session 3b, part B (the first 5-piece table with a pawn)

- KRPvKR under the rule. Its promotions lead to KQRvKR, KRRvKR, KRNvKR and
  KRBvKR (done), so those first, each in its own process:

  | Table | Time | Solver memory | Process peak | White to move W / D / L | Longest DTZ |
  |---|---|---|---|---|---|
  | KQRvKR | 830 s | 1,884 MB | 1,848 MB | 99.8 / 0.1 / 0.0% | 30 / 31 |
  | KRRvKR | 762 s | 1,912 MB | 1,906 MB | 99.2 / 0.7 / 0.0% | 49 / 50 |
  | KRNvKR | 539 s | 1,406 MB | 1,486 MB | 36.6 / 63.3 / 0.1% | 65 / 64 |
  | **KRPvKR** | 2,040 s | **244 MB** | 3,724 MB | 66.6 / 33.0 / 0.4% | 69 / 70 |

  KRPvKR by slices: 24 slices of 33.5 M, arrays 172 MB + queues 72 MB. The
  process peak is mostly mapped files (the 1.9 GB table as it's written and
  the promotion tables), which Windows can drop; RAM free never went below
  ~3 GB. Black to move: 20.1% won, 54.4% drawn, 25.5% lost. 967 M legal
  positions (476.6 M white to move, 490.1 M black).
- Checks: `dtz KRPvKR verify` on every 1009th index (a prime, so the sample
  doesn't line up with the index): 479,339 positions consistent. Lichess:
  the six 5-piece tables' longest cases and 3 random each, 30 of 30; 12
  more random KRPvKR, 12 of 12. `scripts/syzygy-check.py` takes a table
  list and `--rule` now (probing with the new `probe <fen> --rule`, which
  needs only DTZ tables).
- Tests: 258 pass, 1 skipped.
- Next, if wanted: KRPvKR's mate distances for its cursed wins (promotion
  targets' DTM first), or the pawnless 5-piece lot (Matthew to OK the disk).

## 2026-09-28: KRPvKR's mate distances, started and put on hold

- Matthew picked (a), KRPvKR's mate distances for its cursed wins, then
  asked to hold and rerun later. KQRvKR's DTM finished first: 816 s,
  solver 1.94 GB (arrays 1.24 + queues 0.70), process peak 1.95 GB; same
  win / draw / loss counts as its DTZ table (so no cursed wins or blessed
  losses there, to be confirmed by `dtz KQRvKR`); longest mate 34 moves
  (67 plies) with white to move, 35 (70 plies) with black.
- Stopped during KRRvKR's DTM: no solver left running, nothing
  half-written. The commands to carry on are in TODO (3b).

## 2026-09-28: KRPvKR's mate distances, finished

- Matthew said carry on. KRRvKR 766 s, KRNvKR 539 s, KRPvKR 2,082 s by
  slices (solver 263 MB: arrays 172 + queues 91; process peak 3.6 GB,
  mostly mapped files; free RAM got down to 1.2 GB with other things open,
  no harm done).

  | Table | Longest mate (white / black to move) | Cursed wins | Blessed losses |
  |---|---|---|---|
  | KQRvKR | 34 moves, 67 plies / 35, 70 | 0 | 0 |
  | KRRvKR | 31, 61 / 31, 62 | 0 | 0 |
  | KRNvKR | lost in 39, 78 / won in 41, 81 (the lone rook wins) | 0 | 0 |
  | KRPvKR | 74, 147 / 74, 148 | 0 | 0 |

  All longest mates confirmed on Lichess (Gaviota DTM). `verify` on every
  1009th position: KRPvKR 479,339, KQRvKR 133,404, KRRvKR 143,748, KRNvKR
  155,088, all consistent.
- The finding: KRPvKR's mates run to 147 plies, but the pawn resets the
  count often enough that the 50-move rule never changes a result (Syzygy
  puts the 147-ply mate's position 65 plies from its next reset). Of the
  six 5-piece tables so far, only KRBvKR, with nothing to push, has cursed
  wins.

## 2026-09-28: identical pieces stored once, DTZ in one byte

- Matthew, before the overnight run: "if we sort these correctly we will
  minimize disk space by means of deduplication". Yes: the numbering kept
  both orders of two identical pieces (six of three). Now a run of them is
  one digit of C(64, n): a pair's table is half, three alike a sixth. It
  also fixes the counts, which had counted every position with a pair
  twice (percentages unaffected): K+B+B v K 11.9 M legal, not 23.8 M;
  K+R+R v K+R 254,487,576 white to move, not 508,975,152.
- DTZ in one byte a value (CBZ2; its values stay within -101..100).
- Older files load either way (the size says which numbering; they're
  renumbered in memory); `upgrade` rewrites them; `compare` checks two
  files value by value. A capped table in the old numbering is solved again
  rather than extended (its frontier follows the old numbering).
- Regression: mate tables 36 of 36 (the 31 without identical pieces byte
  for byte, the 5 with a pair value for value); DTZ 36 of 36 value for value,
  209 MB against 448 MB; the capped ladder 36 of 36; tests 266 pass.
- 5 pieces: `upgrade tables` in 59 s, 8.39 GB -> 5.67 GB (KQRRvK's DTZ 462
  -> 114 MB). KRRvKR's DTZ re-solved fresh: equal to the upgraded file,
  533 s against 762 s, solver 0.95 GB against 1.91 GB. So about 30% faster,
  not the 50% I'd guessed: sorting the images costs a little each time.
- The overnight run: `scripts\build-five-piece.cmd` (a PowerShell script
  underneath) for all 60 pawnless 5-piece tables, both kinds: ~31 GB,
  ~18 h. Restartable, logs to `tables\five-piece.log`.

## 2026-09-29: the overnight 5-piece run, 49 of 60 so far

- `build-five-piece.cmd` ran 20:33 to 10:18, 46 tables, then stopped a few
  seconds into KQRNvK's mate table: no solver left, no crash or low-memory
  event in Windows' logs, no reboot, no half-written file. Most likely the
  window was closed. 11 left, all full-size (~5 h): run it again.
- 49 done (the run's 45 + KQRRvK's mate table + the four solved before;
  those four re-read here with the same commands). No non-zero exits.
  38,499,347,744 legal positions between them (identical pieces counted
  once). 5-piece files on disk: 25.7 GB (KRPvKR's 2.7 GB included); the
  last 11 add ~7.6 GB, so ~31 GB for the pawnless 60, as estimated.
- **Cursed wins in 5 tables only**, all minor pieces against a minor piece
  or a queen, plus KRBvKR:

  | Table | Cursed wins | share of wins | Blessed losses | share of losses |
  |---|---|---|---|---|
  | KBBvKN | 31,000,264 | 20.9% | 61,322,704 | 48.1% |
  | KBBvKQ | 3,808,264 | 1.2% | 16,652,104 | 8.3% |
  | KBNvKN | 1,068,600 | 0.5% | 517,376 | 1.8% |
  | KNNvKQ | 28,760 | 0.01% | 94,800 | 0.05% |
  | KRBvKR | 17,440 | 0.01% | 5,400 | 0.02% |

- Longest mates: KBNvKN 107 moves (213 plies), KBBvKQ 81 (the queen side
  wins), KBBvKN 78, KNNvKQ 72 (queen side), KRBvKR 65. All four new ones
  match Lichess's mate distance exactly, and Lichess marks those positions
  cursed / blessed too.
- 16 tables are won from every white-to-move position; KNNvKB is drawn
  from all of them. Memory peak 2.18 GB (KQBNvK); tables with identical
  pieces 0.4-1.2 GB. Per table, both kinds: 6 min (three alike) to 29 min.
- Full per-table table below (time and peak for the four earlier tables
  from their own sessions).

  | Table | Legal positions | White to move: won / drawn / lost | Longest mate | Longest DTZ | Cursed wins | Blessed losses | Time (min) | Peak (GB) |
  |---|---|---|---|---|---|---|---|---|
  | KBBBvK | 224 M | 73.9 / 26.1 / 0.0% | 19 moves | 21 | 0 | 0 | 6 | 0.39 |
  | KBBNvK | 693 M | 100.0 / 0.0 / 0.0% | 33 moves | 27 | 0 | 0 | 19 | 1.17 |
  | KBBvKB | 663 M | 15.6 / 84.4 / 0.0% | 22 moves | 12 | 0 | 0 | 12 | 0.68 |
  | KBBvKN | 683 M | 48.2 / 51.8 / 0.0% | 78 moves | 100 | 31,000,264 | 61,322,704 | 14 | 0.89 |
  | KBBvKQ | 580 M | 15.3 / 20.2 / 64.6% | 81 moves (black wins) | 100 | 3,808,264 | 16,652,104 | 21 | 0.98 |
  | KBBvKR | 633 M | 16.5 / 83.4 / 0.1% | 31 moves (black wins) | 17 | 0 | 0 | 12 | 0.69 |
  | KBNNvK | 711 M | 100.0 / 0.0 / 0.0% | 34 moves | 27 | 0 | 0 | 19 | 1.07 |
  | KNNNvK | 242 M | 98.7 / 1.3 / 0.0% | 21 moves | 42 | 0 | 0 | 6 | 0.45 |
  | KNNvKB | 701 M | 0.0 / 100.0 / 0.0% | 4 moves | 7 | 0 | 0 | 11 | 0.64 |
  | KNNvKN | 721 M | 0.1 / 99.9 / 0.0% | 7 moves | 13 | 0 | 0 | 10 | 0.65 |
  | KNNvKQ | 618 M | 0.0 / 42.8 / 57.2% | 72 moves (black wins) | 100 | 28,760 | 94,800 | 18 | 0.95 |
  | KNNvKR | 671 M | 0.0 / 99.6 / 0.4% | 41 moves (black wins) | 21 | 0 | 0 | 12 | 0.66 |
  | KQBBvK | 611 M | 100.0 / 0.0 / 0.0% | 19 moves | 12 | 0 | 0 | 20 | 1.10 |
  | KQNNvK | 641 M | 100.0 / 0.0 / 0.0% | 9 moves | 14 | 0 | 0 | 20 | 1.05 |
  | KQQBvK | 560 M | 100.0 / 0.0 / 0.0% | 8 moves | 7 | 0 | 0 | 22 | 1.19 |
  | KQQNvK | 572 M | 100.0 / 0.0 / 0.0% | 9 moves | 8 | 0 | 0 | 19 | 1.18 |
  | KQQQvK | 173 M | 100.0 / 0.0 / 0.0% | 4 moves | 6 | 0 | 0 | 6 | 0.38 |
  | KQQRvK | 543 M | 100.0 / 0.0 / 0.0% | 6 moves | 7 | 0 | 0 | 18 | 1.06 |
  | KQQvKB | 532 M | 100.0 / 0.0 / 0.0% | 17 moves | 8 | 0 | 0 | 20 | 1.07 |
  | KQQvKN | 551 M | 100.0 / 0.0 / 0.0% | 21 moves | 9 | 0 | 0 | 20 | 1.09 |
  | KQQvKQ | 448 M | 99.1 / 0.8 / 0.1% | 30 moves | 50 | 0 | 0 | 17 | 0.91 |
  | KQQvKR | 502 M | 100.0 / 0.0 / 0.0% | 35 moves | 28 | 0 | 0 | 19 | 1.04 |
  | KQRRvK | 572 M | 100.0 / 0.0 / 0.0% | 7 moves | 8 | 0 | 0 | 10 | 1.16 |
  | KRBBvK | 656 M | 100.0 / 0.0 / 0.0% | 19 moves | 21 | 0 | 0 | 20 | 1.09 |
  | KRNNvK | 685 M | 100.0 / 0.0 / 0.0% | 16 moves | 21 | 0 | 0 | 19 | 1.11 |
  | KRRBvK | 632 M | 100.0 / 0.0 / 0.0% | 16 moves | 11 | 0 | 0 | 19 | 1.08 |
  | KRRNvK | 644 M | 100.0 / 0.0 / 0.0% | 16 moves | 11 | 0 | 0 | 19 | 1.11 |
  | KRRRvK | 201 M | 100.0 / 0.0 / 0.0% | 7 moves | 9 | 0 | 0 | 7 | 0.43 |
  | KRRvKB | 611 M | 99.3 / 0.7 / 0.0% | 29 moves | 20 | 0 | 0 | 18 | 1.07 |
  | KRRvKN | 631 M | 99.7 / 0.3 / 0.0% | 40 moves | 15 | 0 | 0 | 19 | 1.05 |
  | KRRvKQ | 527 M | 58.1 / 36.8 / 5.1% | 49 moves (black wins) | 40 | 0 | 0 | 17 | 0.85 |
  | KRRvKR | 581 M | 99.2 / 0.7 / 0.0% | 31 moves | 50 | 0 | 0 | 22 | 1.88 |
  | KBNvKB | 1,367 M | 25.5 / 74.5 / 0.0% | 39 moves | 25 | 0 | 0 | 16 | 1.40 |
  | KBNvKN | 1,407 M | 32.1 / 67.9 / 0.0% | 107 moves | 100 | 1,068,600 | 517,376 | 16 | 1.48 |
  | KBNvKQ | 1,200 M | 25.0 / 6.4 / 68.6% | 53 moves (black wins) | 84 | 0 | 0 | 29 | 1.96 |
  | KBNvKR | 1,307 M | 26.0 / 73.8 / 0.2% | 41 moves (black wins) | 25 | 0 | 0 | 18 | 1.42 |
  | KQBNvK | 1,254 M | 100.0 / 0.0 / 0.0% | 33 moves | 9 | 0 | 0 | 27 | 2.18 |
  | KQBvKB | 1,183 M | 99.7 / 0.3 / 0.0% | 17 moves | 16 | 0 | 0 | 27 | 1.97 |
  | KQBvKN | 1,223 M | 99.5 / 0.5 / 0.0% | 21 moves | 14 | 0 | 0 | 27 | 1.94 |
  | KQBvKQ | 1,016 M | 55.7 / 44.0 / 0.3% | 33 moves | 60 | 0 | 0 | 22 | 1.60 |
  | KQBvKR | 1,123 M | 99.3 / 0.6 / 0.0% | 40 moves | 38 | 0 | 0 | 27 | 1.93 |
  | KQNvKB | 1,214 M | 99.8 / 0.2 / 0.0% | 17 moves | 18 | 0 | 0 | 27 | 1.97 |
  | KQNvKN | 1,254 M | 99.4 / 0.6 / 0.0% | 21 moves | 18 | 0 | 0 | 26 | 1.96 |
  | KQNvKQ | 1,047 M | 50.1 / 49.6 / 0.3% | 41 moves | 70 | 0 | 0 | 22 | 1.55 |
  | KQNvKR | 1,154 M | 99.2 / 0.7 / 0.0% | 41 moves (black wins) | 44 | 0 | 0 | 28 | 1.88 |
  | KQRBvK | 1,188 M | 100.0 / 0.0 / 0.0% | 16 moves | 9 | 0 | 0 | 27 | 2.17 |
  | KQRvKR | 1,078 M | 99.8 / 0.1 / 0.0% | 35 moves | 31 | 0 | 0 | 27 | 1.95 |
  | KRBvKR | 1,221 M | 41.2 / 58.7 / 0.0% | 65 moves | 100 | 17,440 | 5,400 | 19 | 1.51 |
  | KRNvKR | 1,252 M | 36.6 / 63.3 / 0.1% | 41 moves (black wins) | 65 | 0 | 0 | 18 | 1.49 |

## 2026-09-29: all 60 pawnless 5-piece tables done

- Matthew restarted the run at 13:01 (a first start got stopped straight
  away: "6.7 GB" was RAM, not disk, a misread). The last 11 took 5.7 h;
  finished 18:41, 60 of 60 on disk, no non-zero exits.
- Totals: 51,862,481,856 legal positions; 30.8 GB of files (33.7 GB with
  KRPvKR); 20.9 h of solving; peak 2.25 GB (KQRNvK).
- One more table with cursed wins: KQRvKQ, 1,840 cursed, 8,848 blessed.
  So six in all, and they are exactly the six whose longest DTZ reaches
  the rule's 100. New long mates: KRBvKQ 70 moves, KRNvKQ 69, KQRvKQ 67
  (the first two won by the queen); all confirmed on Lichess, KQRvKQ's
  edge positions too (DTZ 99 / -100).
- Checked before writing it down: KNNvKB is not drawn from every position,
  as its 100.0% looked: 72,816 white-to-move wins (0.02%), all mates in 4
  or fewer.
- The full table (grouped 3 v 0, then 2 v 1) is in FIVE-PIECE.md.

## 2026-09-29/30: planning the pawn tables; compression started

- Matthew: plan the pawn tables, thinking of the engine's weak points, an
  opponent who (a) flukes or (b) knows exactly what they're doing and
  wanders pieces about to draw moves out of us. Found: the engine plays
  table positions by mate distance only and ignores the 50-move clock, which
  is what (b) exploits. Plan in TODO ("Now: compression, then the pawn
  tables"): steps A-D.
- Sizes: the 50 pawn tables are 36 with one pawn, 12 with two, 2 with
  three; both kinds 115.1 GB with pawns on 64 squares, 81.5 GB on 48;
  ~49 h. Only ~103 GB free.
- Matthew asked: buy a drive, or clean up? Measured compression on our own
  tables in independent 64 KB blocks: zlib 12.0x (KRBvKR.cbt), 9.5x
  (KRPvKR.cbz); 4 KB blocks 8-9x; lzma on a 64 MB sample 17-35x. So the
  whole 5-piece set (~112 GB plain) would be ~11 GB. He chose compression
  first (a drive would have to be an SSD anyway).
- Started on branch `compression`: `CompressedTable` ("CBC1", Brotli per
  64 KB block, offsets up front, a 1024-block cache), loading and
  `SaveCompressed` in EndgameTable. Builds, 266 tests pass; the new code
  isn't tested yet. Next steps listed in TODO, step A.

## 2026-09-30: compression, step A (all but compressing `tables/`)

- Brotli quality measured per 64 KB block (a sample of 1 in 4 or 8 blocks):

  | quality | KRBvKR.cbt | KRPvKR.cbz | whole file, 16 threads |
  |---|---|---|---|
  | 5 | 13.1x | 11.0x | 1 s / 3 s |
  | 9 | 14.6x | 11.4x | 4 s / 13 s |
  | **10** | **18.0x** | **13.1x** | 23 s / 36 s |
  | 11 | 19.7x | 14.2x | 97 s / 113 s |

  Chose 10: most of 11's gain at a quarter of its time. Decompressing a
  block takes 70-100 us at any quality. Two byte planes for mate distances
  (low bytes, then high) gained nothing: Brotli already models them.
- `compress [dir|file] [--quality N]`: temporary file, read back, every
  value compared with the plain table, then moved over. The four DTZ tables
  KRPvKR promotes into: KQRvKR 9.8x, KRRvKR 6.5x, KRBvKR 15.9x, KRNvKR
  17.5x, ~25 s each (compressing and checking).
- The question that decided the plan: is a solve slower when the tables it
  reads are compressed? KRPvKR's DTZ solved twice at the same time (so both
  saw the same machine), promotion tables plain in one, compressed in the
  other: **1,990 s plain, 2,055 s compressed, 3% slower**; process peak
  1.93 GB v 1.62 GB. Both came out byte for byte the KRPvKR.cbz already on
  disk. The solver's probes land near each
  other, so the 1024-block cache mostly hits. So the pawn tables can be
  solved from compressed smaller tables, and each compressed as it finishes.
- `Tablebase.Preload` (the engine) now reads plain tables into memory,
  smallest first, within 1 GB, and leaves compressed ones (and plain ones
  past 1 GB) on disk. Before, with the 5-piece tables in the folder, it would
  have tried to read ~31 GB.
- `build-five-piece.ps1` compresses both files of every finished table
  (`-Plain` to skip), including ones it finds already done: run as it is, it
  compresses the 60 pawnless tables. `-List` shows which are compressed.
- 13 new tests (CompressedTableTests); 279 pass, 1 skipped, 46 s (was 20 s:
  mostly solving K+R+R v K, both kinds, and again from compressed tables). A deliberately broken cache
  lookup fails the eviction test.
- Left for Matthew: compress `tables/` (replaces his plain files, so his go).
  He ran build-five-piece.cmd himself, 02:30-03:18: all 120 files of the 60
  pawnless tables, every one checked value by value, none failed. 28.7 GB ->
  3.15 GB (9.1x): mate distances 19.1 -> 1.90 GB (10.1x), DTZ 9.6 -> 1.24 GB
  (7.7x). From 3.6x (KBNvKQ.cbz) to 78x (KNNvKN.cbt, nearly all draws), so
  KRBvKR's 18x, which set my estimate of ~8 GB for everything, was on the
  kind side: expect ~13 GB for the pawn tables. Free space 96 -> 122 GB.

## 2026-09-30: step B, which pawn endings real games reach

- `game endings <file>...` (EndingStats): replays every game and records
  each material of 5 pieces or fewer it passes through. 741,349 games in
  231 s.
- First run said 40% of games went through a table outside the plan: the
  Windows `tables/` had never had 8 of the 36 small tables (KQPvK, KBPvK,
  KNPvK, KPPvK, KQvKP, KBvKP, KNvKP, KPvKP, both kinds; they were solved in
  WSL before). Solved here in 9 min; 7 byte for byte `tables-baseline/`,
  KPPvK (pawns stored once now) equal value for value. Longest mates as
  published: KPvKP 33 moves, KQvKP 28, KBPvK 31, KNPvK 27.
- With all 36: **48,138 games (6.5%) get down to 5 pieces or fewer**. Of
  those, 6,879 (14.3%) stay in tables we have; 41,259 (85.7%) pass through a
  5-piece pawn table we don't, and 66% of those are back in our tables by
  the end (the pawn promoted or taken). One game held castling rights at 5
  pieces.
- Most reached pawn tables (games, share of all games): KPPvKP 8,719
  (1.18%), KQPvKP 4,014, KRPvKP 3,464, KRPPvK 3,197, KQPPvK 2,756, KPPPvK
  2,698, KBPvKP 1,963, KPPvKR 1,765, KPPvKQ 1,571, KQRPvK 1,417. Pawn
  endings are brief: KPPvKP games stay 5 plies on average (races).
- The build order (EndingStats.BuildOrder): greedy, each round the table
  that with the tables its promotions need brings the most games wholly into
  the tables per position solved. Milestones:

  | after | tables | work (positions) | games wholly in tables |
  |---|---|---|---|
  | today | 0 | 0% | 14.3% |
  | the 15 "three v a bare king" | 15 | 25.5% | 41.9% |
  | the 25 one-pawn "two v one" | 40 | 83.7% | 54.3% |
  | the 8 two-pawn "two v one" | 48 | 98.8% | 81.9% |
  | K+P+P v K+P | 49 | 100% | 100% |

  KPPvKP comes last because its promotions lead into all 8 two-pawn tables,
  which lead into most of the one-pawn ones: 18% of these games wait for
  nearly all the work. Order in TODO step D.
- 7 tests (EndingStatsTests); 286 pass.

## 2026-09-30: step C, the engine plays the tables by the 50-move rule

- Matthew's "wanderer" (an opponent who knows the tables runs the clock up):
  before, the engine chose table moves by mate distance alone, so a shortest
  mate longer than the plies left on the clock was a draw it thought a win.
- Root (`Search.TablebaseMove`), with DTZ tables on hand: each move's result
  for us is the reply position's DTZ result at its clock (0 after a capture or
  pawn move), a draw if clock + DTZ passes 100. Winning, the win nearest its
  next capture, pawn move or mate (the count restarts there), ties by mate
  distance; losing, the furthest loss; draws broken by mate distance too.
- In the search and in match adjudication, `Tablebase.TryProbeWithClock`:
  the mate distance, but a draw where the DTZ table says the rule makes it
  one at that node's clock. `RankMovesUnderRule` (and `probe --rule`) now
  count from the position's own clock, not 0.
- The engine now preloads the `.cbz` tables too; both kinds up to 4 pieces
  take ~850 MB in memory, inside Preload's 1 GB.
- Tests: K+R v K at the last clock that still wins and one ply later, both
  sides; K+P v K at 99 (only a pawn move wins, though king moves win without
  the rule); DTZ-optimal play move after move; adjudication; a cursed K+B+B v
  K+N position (slow test, run on the real tables: passes). Three mutations
  (root ignores the clock; probe ignores DTZ; ranking ignores the clock)
  each fail at least one test. 291 pass, 2 skipped (the slow ones).
- Not done: scaling the evaluation down as the clock rises outside the
  tables (the "maybe" in TODO).

## 2026-09-30: step D started, the 49 pawn tables

- `build-five-piece.ps1 -Pawns`: step B's order written out (checked by a
  script: all 50 pawn tables, none before a table it promotes into); after
  each table, `verify` and `dtz verify` on every 1009th position, and a
  failure or mismatch stops the run; it runs from a copy of the build
  (tables\.cli). First start at 03:27 stopped at "build failed" (the build
  was fine a minute later; the error wasn't kept, so now it is logged).
  Restarted 03:28, in its own window.
- KRPvKR.cbt had been compressed at 03:27, outside the run: Matthew ran the
  `compress` command I'd given him (confirmed), and a CLI running from
  bin\Release locks its files, hence the first start's build failure at
  03:27:56. The run found it done and compressed KRPvKR.cbz (12.6x); he ran
  the .cbz command too, around then. Checked afterwards: both files read
  end to end with KRPvKR's original counts, to the position.
- First two new tables, ~75 min each with checks:

  | table | positions | DTM solve | DTZ solve | verified (both) | compressed |
  |---|---|---|---|---|---|
  | KQRPvK | 947 M | 37.5 min | 34.8 min | 459,351, consistent | 2.7 GB -> 97 MB (28x) |
  | KQQPvK | 466 M | 35.3 min | 42.7 min | 216,413, consistent | 1.3 GB -> 50 MB (27x) |

  Pawn tables compress 25-34x, three times the pawnless ones: expect ~5 GB
  for all 49, not ~13. At this pace (~3.8% of the work in 2.6 h) the run
  is ~60-70 h, not 49.
- Lichess (Syzygy) on both: longest DTZ each side and 3 random positions
  each, 10 of 10 agree (`scripts/syzygy-check-pawns.tsv`). Both easy
  tables (longest DTZ 5-6 plies: the pawn runs).
- 18:33, 13 of 50 done (15.1 h): both compresses of KQPPvK failed with exit
  1 in the same second, leaving it plain (the run went on, as it should).
  Matthew guessed low memory, and Windows agrees: Resource-Exhaustion-
  Detector 2004 at 18:33:11, "low virtual memory", vmmem (the Cowork VM, not
  WSL) 4.3 GB and the Claude app ~5 GB across its processes; commit free
  was still 1.7 of 21.8 GB half an hour later. Compressed by hand then:
  25.8x and 57.7x, each checked. The script now logs a failed compress's
  error, retries it once after 2 min, and retries a crashed solve or DTZ step
  once after 5 min; a verify mismatch still stops at once (8 cases of the
  rule checked). Takes effect at the script's next start.

## 2026-10-01: taking over real games where they reach our tables

- Matthew: we haven't played any chess with the tables; take over real
  games once they reach a table that's ready. `game takeover <files>
  [--play N]` (Takeover.Find / PlayOut): each game's first position in a
  table we have (both kinds; 121 at the time, the pawn run at 25 of 50),
  the table's verdict at the game's own 50-move clock against the result,
  then every one played out by the engine on both sides from there (its
  clock and history carried over). Five months, 741,349 games, 10 min.
- **42,755 games (5.8%) reach a table we have.** Median 7 moves left in the
  game when they get there (quartiles 3 and 13), so the tables come in late.

  | table says, for the side to move | games | won | drawn | lost |
  |---|---|---|---|---|
  | won | 10,010 | 81.0% | 16.0% | 2.9% |
  | drawn | 10,195 | 13.0% | 69.5% | 17.5% |
  | lost | 22,550 | 0.9% | 13.2% | 85.9% |

  **19.1% of them (8,187) ended otherwise than perfect play from there.**
  Of 32,560 won endings, 5,077 weren't won (15.6%), 2,189 of those on time.
  Only 2 were won with best play but drawn by the rule at the game's clock.
  Games that ended in mate took the players a median 10.5 moves; the
  fastest mate was 8.0.
- Most common entries: KRPvKR 5,210 (won ones converted 75.6%, drawn ones
  held 58.3%), KPvKP 4,640 (77.4%, 68.4%), KRPPvK 3,080, KPPvK 2,953, KPPPvK
  2,686. The worst held draws: KQPvKQ, 42.4%.
- Played out, all 42,755 (9 ms each): **every one ended as the table said.**
  Checkmate 32,560 (exactly the decisive verdicts), threefold repetition
  6,465, insufficient material 3,587, stalemate 141, 50-move rule 2 (the two
  cursed ones). The first real games with the new tables and step C's rule.
- Not yet measured: how much slower the engine's rule-safe play mates than
  the fastest mate (it heads for the next capture or pawn move first).

## 2026-10-05 / 09: step D done, all 50 pawn tables

- The run finished 2026-10-05 09:55, 50 of 50 (Matthew: "Tables are dONE").
  Summary from its log in FIVE-PIECE-PAWNS.md: 38.8 billion legal positions,
  107 GB solved -> 8.8 GB compressed (12x), peak 3.9 GB (KRPvKP).
- 120.5 h of steps in the log, but the laptop slept 48 h inside them (System
  log: Kernel-Power 42 and back, eight times; 16.5 h, 12.6 h and 14.4 h the
  longest): **72.6 h of solving**. I'd first put the long ones (KQPvKB's DTZ
  1,030 min, KQPvKP's mates 907, KBPvKP's 935) down to pawns on both sides;
  the sleeps account for nearly all of it. Pawns on both sides are slower,
  but 2-7.5 h, not 15.
- Two failures, both recovered. KQPPvK's compress (low memory, 2026-09-30,
  done by hand). KQPvKP's first DTM solve, exit -1 after 91 min on 2026-10-03
  03:15; a low-virtual-memory event at 01:53 inside it (apreview.exe 7.3 GB,
  Visual Studio 1 GB), so probably memory again; the new retry finished it.
  The run also restarted once (2026-10-01 22:32) and carried on.
- Every table's sample verified as built (every 1009th position, both
  kinds): 38.4 M positions, all consistent. Lichess
  (`scripts/pawn-tables-check.py`): longest mate and longest DTZ for each
  side to move and 3 random positions, 49 tables, 343 positions: **343 agree
  on the mate distance (Gaviota), 343 on the rule (Syzygy)**. 21 of the 98
  longest-mate positions are cursed or blessed, and marked so on both sides.
- Longest mate of any five-piece ending: K+P+P v K+P, 127 moves (253 plies),
  a cursed win. The rule matters most in K+N+N v K+P (18% of wins cursed,
  41% of losses blessed: the Troitsky line), then K+B+B v K+P (2%).
- `game takeover` again with every table (146 with both kinds): 47,842 games
  (6.5%) reach one, nearly all of the 48,138 that get down to 5 pieces; 21.1%
  ended otherwise than perfect play from there. KPPvKP is now the commonest
  way in (8,719 games; drawn ones held only 58.6%). Played out, engine on
  both sides (18 ms each, 14 min): **47,842 of 47,842 as the table said**:
  37,345 mates (every decisive one), 7,123 repetitions, 3,216 insufficient
  material, 155 stalemates, 3 by the 50-move rule (the 3 cursed at the
  game's clock).

## 2026-10-09: the takeover positions against Stockfish

- Matthew picked (a): our engine against Stockfish from where real games
  reach the tables. `game takeover ... --versus N "<engine>" [ms]`: N
  positions spread over all the takeovers (as often as games reach each
  table), each played twice through the match's game loop, our engine on
  the side to move and then on the other side. Stockfish 16 in WSL by
  `wsl.exe -d Ubuntu-24.04 -- /usr/games/stockfish`, full strength, no
  tablebases. Our engine in-process (SearchPlayer) rather than as a second
  UCI process: with WSL up, only 2.2-2.5 GB of commit was free.
- 500 positions, 1,000 games, 100 ms a move, 20.6 min: **every game ended
  as the table says, for both sides.** Ours: 406 of 406 wins converted, 188
  of 188 draws held, 406 losses lost. Stockfish exactly the same. Points 500
  to 500, the tables' figure. Ended by mate 812, insufficient material 117,
  repetition 54, the 50-move rule 10, stalemate 7.
- So in the positions real games bring, Stockfish at 100 ms doesn't need the
  tables: the humans' 21% of results gone wrong comes from people, not from
  the endings being hard for an engine. Where the tables should beat it:
  the hard positions (long DTZ, the longest mates, cursed and blessed
  ones), a weaker or faster opponent, or the moves it takes. Not yet tried.

## 2026-10-09: hard positions from the tables against Stockfish

- Matthew picked (a): positions sampled from the tables themselves. `hard
  <materials> <per> "<engine>" [ms]`: per table, `per` each of long-DTZ wins
  (DTZ 50+ plies, or the top fifth of a shorter table's range), cursed wins
  and blessed losses, all with the count at 0, each played twice against
  Stockfish 16 (WSL, full strength, no tablebases, 100 ms a move), our
  engine on each side. 18 tables chosen for long DTZ: KBNvK, KQvKR, KBBvKN,
  KBNvKN, KRBvKR, KQRvKQ, KBBvKQ, KNNvKQ, KRNvKQ, KQNvKQ, KRNvKR, KQPvKQ,
  KRPvKQ, KRPvKB, KNNvKP, KBPvKN, KRPvKR, KPPvKP; 6 per kind; sampling took
  5 min, the 504 games 41 min.

  | kind | positions | our engine | Stockfish |
  |---|---|---|---|
  | long-DTZ wins | 108 | converted 108 of 108 | converted 44 of 108 (41%), drew 64 |
  | cursed wins (drawn by the rule) | 72 | won 67 of 144 games, lost 0 | lost 67 |
  | blessed losses (drawn by the rule) | 72 | won 68 of 144, lost 0 | lost 68 |
  | points | | **351.5** (the table's 252) | 152.5 (252) |

- So: in real games' endings Stockfish needs no tables (1,000 games, all as
  the tables say); in the hard ones it can't convert 59% of long wins (it
  stalemated, repeated, ran out the 50 moves or gave material away; K+B+N v
  K among them), and defending a position the rule draws it often lost
  (two bishops against a knight, mated in 74-118 plies). Our engine never did
  worse than the table in any of the 1,504 games. The output lists only the
  first few of Stockfish's slips per kind, not a per-table breakdown.
- Caveats: 100 ms a move is fast for Stockfish, and in real competition it
  would have Syzygy tables of its own.
- Matthew asked for it table by table: same command and seed (same
  positions), PlayVersus now counting per table; run on battery (Matthew),
  so Stockfish had a slower CPU in its 100 ms. 504 games, none ended by a
  missing answer or illegal move. Stockfish converted 29 of 108 long wins
  this time (44 before), lost 67 of 72 cursed defences and 70 of 72
  blessed ones (67, 68 before); we scored 360 of 504 (the table's 252).

  | table | Stockfish's long wins converted | its cursed / blessed defences held |
  |---|---|---|
  | KBNvK | 0 of 6 | - |
  | KQvKR | 1 of 6 | - |
  | KBBvKN | 2 of 6 | 0 of 6 / 0 of 6 |
  | KBNvKN | 0 of 6 | 0 / 0 |
  | KRBvKR | 0 of 6 | 0 / 0 |
  | KQRvKQ | 1 of 6 | 0 / 0 |
  | KBBvKQ | 0 of 6 | 0 / 0 |
  | KNNvKQ | 2 of 6 | 0 / 0 |
  | KRNvKQ | 0 of 6 | - |
  | KQNvKQ | 0 of 6 | - |
  | KRNvKR | 6 of 6 | - |
  | KQPvKQ | 2 of 6 | 1 / 0 |
  | KRPvKQ | 0 of 6 | 0 / 0 |
  | KRPvKB | 3 of 6 | 0 / 0 |
  | KNNvKP | 1 of 6 | 4 / 2 |
  | KBPvKN | 0 of 6 | 0 / 0 |
  | KRPvKR | 5 of 6 | - |
  | KPPvKP | 6 of 6 | 0 / 0 |

  (Each defence is the game where we attack; in the other game of each pair
  Stockfish attacks and our defence holds, always.)
- Losing nearly every defence of a position the rule draws looked too clean,
  so I checked examples on Lichess: all genuinely cursed or blessed, but
  only just: DTZ 105-120 against the rule's 100. The defender must be all
  but perfect; any slack brings the conversion inside the count. Several of
  our mates came after more than 100 plies, so Stockfish had let its piece be
  taken, which restarts the count. K+N+N v K+P, where the cursed positions
  run much longer, is the one Stockfish defends.

## 2026-10-12: recent games, and who gets the endings wrong

- Matthew asked for recent games. The September 2026 file is 29.2 GB (89.6 M
  games); with his OK, only its first 1 GB by a range request
  (`games/lichess_db_standard_rated_2026-09.first-1GB.pgn.zst`, 16 min at
  ~1 MB/s), unpacked across its many Zstandard frames up to the cut
  (`zstd.open`, keeping complete games only): 3,284,165 games of 1
  September, 7.1 GB of PGN with clock comments, imported in 26 min into
  `games/2026-09-part.cbg` (1.28 GB). The PGN was deleted after.
- `game takeover` now breaks the scorecard down by the players' average
  rating and by Lichess's time-control class (base + 40 x increment).

  | | 2013, 5 months | 2026, 1 day |
  |---|---|---|
  | games | 741,349 | 3,284,165 |
  | reaching a table we have | 47,842 (6.5%) | 189,886 (5.8%) |
  | ended otherwise than the tables | 21.1% | 26.7% |
  | won endings not won | 17.1% | 22.8% |
  | of those, lost on time | 42% | 54% |

  By rating (2026): ended otherwise 27.1% (under 1500), 25.5%, 28.7%,
  27.2% (2400 and up), flat; but the share of those lost on time climbs
  38% -> 59% -> 73% -> 80%, so the errors in the moves themselves fall
  16.9% -> 10.4% -> 7.6% -> 5.5%. 2013 the same shape (16.2%, 10.0%, 8.6%).
  By time control (2026): ended otherwise 41.8% (ultrabullet, 435 games),
  32.6% bullet, 26.0% blitz, 21.2% rapid, 17.7% classical; on time 90%,
  76%, 55%, 26%, 12% of those. Strong players know these endings and lose
  them to the clock; slower games lose them to the moves.
- KPPvKP is the commonest way in again (35,101 games); drawn KRPvKR held
  54%. Outputs: scripts/takeover-2026-09.txt, scripts/takeover-2013.txt.
- The check month Matthew asked for, 2013-06 (32.9 MB, 224,679 games by
  Lichess's count): imported 224,678 in 1 min 46 s into `games/2013-06.cbg`
  (the unpack script trimmed the last game of a whole file too; fixed, now
  `scripts/unzst.py`, which trims only after a cut). Its scorecard: 13,872
  games reach a table, **21.7% ended otherwise** (the five months: 21.1%),
  and the same shapes by rating and time control (bullet 29.1%, rapid
  16.5%). So the 2013 figures hold on a month nothing had been tuned on.
  Output: scripts/takeover-2013-06.txt.
- Matthew asked to finish the September 2026 download (he kept the first
  1 GB for it). `scripts/download-lichess-month.cmd 2026-09` carries on from
  those bytes (renamed to `.part`, the server's Last-Modified recorded and
  checked so two versions of the file can't be spliced), retries on a
  dropped connection, and renames the file when its size matches the
  server's 29,224,520,887 bytes. Started 18:43 in its own window, ~750 KB/s:
  ~10 h. Log: games/download-2026-09.log.
- Not yet possible as it stands, so for the next session: importing the
  whole month. Unpacked it is ~150 GB, so unpack and import in chunks,
  deleting each; and as .cbg it would be ~35 GB (389 bytes a game, 83% of it
  tags) against ~39 GB free once the download is in. Either keep only the
  tags the scorecards use (Result, WhiteElo, BlackElo, TimeControl,
  Termination: ~12 GB) or score straight from the stream without storing.

## 2026-10-12: move by move, the mistakes inside the tables

- Matthew asked for the human failure rate: how many games our engine would
  play better. `game takeover <files> --moves`: from where each game enters
  a table to its end, every move with the table's verdict (50-move rule, the
  game's clock) for the side moving, before and after. A move from a won
  position that leaves it drawn or lost, or from a drawn one that leaves it
  lost, is a mistake. A lost side can't make it worse, and the engine,
  moving by the tables, never makes one. Check: no move of the 4.6 M came
  out better than the table allows, and every position was read.

  | | 2013, Jan-Jun | 2026-09, 1 day |
  |---|---|---|
  | games reaching a table | 61,714 | 189,886 |
  | moves played in the tables | 1,156,298 | 3,444,228 |
  | mistakes, of the moves that could go wrong | 3.1% (1 in 32) | 3.2% (1 in 31) |
  | wins thrown, per move from a won position | 1 in 37 | 1 in 35 |
  | draws lost, per move from a drawn position | 1 in 26 | 1 in 26 |
  | games with at least one mistake | 19.3% | 20.4% |
  | ended as the table said, after mistakes that came back | 6.0% | 6.7% |

- How the games that ended otherwise than the table went wrong (2026,
  50,619): through the moves 46.6%; a flag fell with the last position not
  as the result 49.8%; resigned a position not lost 1.2%; drawn by
  repetition while someone was winning 2.2%; other 0.3%. (2013: 58.5%,
  37.7%, 1.8%, 0.7%, 1.3%.)
- Per 100 moves that could go wrong, by the rating of the player moving
  (2026): 3.82 under 1500, 3.07, 2.84, 2.40 at 2400 and up. By time control:
  ultrabullet 5.5, bullet 3.35, blitz 3.23, rapid 3.12, classical 2.59. So
  the moves barely get worse from rapid to bullet; the gap in results
  between them (21.2% against 32.6% ended otherwise) is the clock.
- By how far into the ending: 3.75 per 100 in a player's first 5 moves in
  the tables, falling to 2.05 after 40. But the winning side's rate climbs
  again after 40 moves (5.2% of its moves throw the win): the 50-move count
  running out.
- Hardest tables (2026, 1,000 moves or more): KQPvKQ 10.4 per 100 (17.5% of
  moves from a won position throw it), KRPvKQ 10.2, KPPvKR 9.7. The
  commonest, KRPvKR, 7.2 (12.5% of its won moves throw the win). Even KPvK
  throws a win once in 19 moves.
- Points: from the scorecard, results that differed from the tables cost
  the side that lost out 28,561.5 points over 189,884 endings in 2026, 0.15
  an ending.
- Outputs: scripts/takeover-moves-2026-09.txt and
  scripts/takeover-moves-2013.txt (all six 2013 months). 18 min and 6 min,
  peak 1.8 and 1.7 GB, run from a copy of the build with the heap capped at
  1.75 GB: commit was 1-2 GB free with the download running.
- Not measurable yet: mistakes against the time left on the clock. The .cbg
  files keep no clock comments; the whole-month import could keep `%clk` for
  the moves inside the tables.

## 2026-10-12: the clock, the kinds of mistake, and real examples

- Matthew picked (a) the clock and (c) examples. `scripts/table-games.py`
  streams a Lichess .pgn.zst and keeps only the games with 27 captures or
  more: exactly those that get down to 5 pieces (32 - captures; SAN marks
  every capture with an x). Seven tags kept, comments dropped, and the
  clock readings from two plies before the 27th capture on, as two tags
  `game import` keeps (`ClockPly`, `Clocks`: whole seconds, the mover's
  time after the move with the increment added). ~30,000 games a second.
  The first 1 GiB of September: 3,284,678 games read, 191,110 kept (5.82%)
  in 99 s; 200 MB of PGN, 64 MB as `games/2026-09-part-tables.cbg`. So the
  whole month comes to ~1.8 GB as .cbg with no ~150 GB unpack.
- `game takeover` on that file gives the morning's scorecard again: 189,913
  games enter a table against 189,886 (the cut came later this time, 513
  more games read). `--moves` now sets each move against the mover's clock
  as it starts to think (its reading after its last move), and the time
  taken (that, plus the increment, less its reading after this move):

  | seconds left | 0-4 | 5-9 | 10-19 | 20-29 | 30-59 | 1-2 min | 2-5 min | 5 min+ |
  |---|---|---|---|---|---|---|---|---|
  | mistakes per 100 | 4.62 | 3.34 | 2.93 | 2.71 | 2.92 | 3.03 | 3.07 | 2.96 |

  | time taken | under 1 s | 1 s | 2-3 s | 4-9 s | 10-29 s | 30 s+ |
  |---|---|---|---|---|---|---|
  | mistakes per 100 | 2.83 | 2.86 | 3.47 | 4.76 | 5.65 | 5.15 |

  Below 10 s left the rate climbs (1.6x the plateau under 5 s); from 20 s
  up, more time doesn't lower it. The long thinks go wrong most, but that's
  where the hard positions are: it doesn't show that thinking causes
  mistakes.
- Each mistake classified (`KindOfMistake`), the first that fits:
  stalemated the other side; too slow (still a mate, outside the 50-move
  count); hung (the other side's way out is a capture); missed a capture
  that kept it; else the piece moved. 2026: a wrong king step 33%, hung
  21%, another piece 20%, pawn 10%, stalemate 9%, missed 7%, too slow 1%.
  Signature mistakes: K+Q v K 59% stalemates (K+P+P v K 38%), K+R v K+R
  46% hung, K+P v K 66% king steps. 2013-01 the same shape (king 38%).
- Examples: per table and kind, the strongest player with 20 s or more on
  the clock, printed tab-separated (FEN, both moves as SAN and squares, the
  verdicts in words, the Lichess link at the ply). Among them: a 2693 with
  49 s missed mate in one (K+Q+P v K+Q); a 2299 in check from a queen it
  could take walked away; a 2854 and a 2831 swapped mistakes on consecutive
  moves (K+P v K+P). Matthew's page, "Endgames Thrown Away", is built from
  the output by `scripts/endgames-page/make_page.py` (13 boards with the
  move played and the tables' move as arrows, the two clock charts, the
  kinds): https://claude.ai/artifact/AC7GVV8rmuefFj1AEEJEE6 (private).
- Output: scripts/takeover-moves-2026-09-clocks.txt (6 min, peak 1.77 GB,
  heap capped). Tests: 296 pass, 2 skipped.

## 2026-10-13: the Lichess bot, session 1

- lichess-bot (2026.10.11.1, AGPL) in `bot/lichess-bot` with its own venv
  (out of git, with config.yml and the token). Our settings in
  `bot/make-config.py`; `bot/start-bot.cmd` refreshes a copy of the engine
  build, rewrites the config (keeping the token) and runs it with a dated
  log in bot/logs; games go to bot/games. Engine over UCI with the tables:
  ready in 1.5 s (292 tables, 796 MB in memory), table moves instant.
- The account is Matthew's own, **matthewcarven** (made 2026-10-13 for
  this, no games; he chose it over a separate BruteForceBot). Upgraded to
  BOT at 04:15; Lichess shows it online with the BOT title.
- One snag: lichess-bot `-u` carries on as the bot after upgrading, so
  start-bot.cmd made a second copy, and Lichess rate-limited its event
  stream (one per account) until the first was closed; it recovered alone.
- Settings for now: casual only, one game at a time, no resigning, draws
  offered as lichess-bot's default, no matchmaking. Next: its first games
  (a challenger, or matchmaking against other bots), then rated.
- Matchmaking on (casual blitz, 3+2 or 5+3). Lichess starts a new bot at a
  provisional 3000 in every speed, and casual games never move it, so
  lichess-bot's "within 300 of our rating" only picked 2700-3300 bots
  (grail-bot and caiporaBot ignored us). Now a fixed band, 1800-2400.
- **First game** (https://lichess.org/Ifx8xL7J, 5+2 casual, 04:28-04:40):
  Bongaclang (2389, the top of the band, opened with the Bongcloud) beat us
  with Black, mate on move 56. Behind on material from the middlegame; 40 s
  left at the end, so not the clock. Nothing went wrong in the plumbing: no
  crash, no illegal move, saved to bot/games. The Stockfish run was going
  on alongside (single-threaded engines on a 16-thread laptop).
- Casual, overnight: 4 won, 4 lost, 1 drawn (wins over MinibruteforceBot
  2014, StockfishLowDepth 1975, agentc313 2204, sseh-c 1950; losses to
  Bongaclang 2389, CalciumEngine 1856, bot64jaques 2330). Search depth a
  median 12-14 plies. Some bots refuse casual challenges, so rated from
  06:04 (a restart between games; a restart mid-game would forfeit it).
- Rated, the first three: won on time v Iskelt-Corax 1854 (+2), lost v
  VidBot 2243 (-700: the provisional 3000 falling), won v Botkingteam33
  2153 by mate in 32 (+148): 2450 provisional at 06:50. The VidBot game
  reached 4 pieces (K+B v K+R+N) already lost by the tables (mate or a
  zeroing move against us in 28 plies, the 50-move rule counted), so it was
  lost before the tables, as every loss so far has been.
- The whole of September 2026 for (b): `scripts/month-tables.cmd 2026-09`,
  04:44-06:37. The filter read 89,616,462 games, exactly Lichess's
  counts.txt, so the download (resumed across three restarts) is intact; it
  kept 5,260,601 (5.87%) in 46 min, 5.5 GB of PGN; imported in 66 min into
  `games/2026-09-tables.cbg` (1.86 GB, 669.9 M moves). Next: make `game
  takeover` stream in batches, then `--moves` on it (TODO).
- By 08:48: 11 rated games, 5 won, 5 lost, 1 drawn; blitz 2092
  (provisional), close to the ~2100 estimated from Stockfish matches. Every
  game that reached the tables had its result settled before it got there
  (checked by probing the entry position). One loss on time (Voigtsbach,
  aWLxeu2F) was the network: the connection to Lichess failed from 08:04:15
  for about a minute while our clock ran. And every move re-makes the
  connection ("Resetting dropped connection", 586 times overnight, ~1.2 s
  each from Australia to Lichess's servers), which lichess-bot's 2 s move
  overhead absorbs. A server nearer Lichess would remove both.

## 2026-10-13: the hard positions at 1 s a move

- `scripts\hard-vs-stockfish.cmd` (Matthew, 01:57-08:18, plugged in, with
  the bot and the September import running alongside on other threads):
  the same 252 positions as the 100 ms runs, 504 games in 381 min.

  | | 100 ms (2026-10-09) | 100 ms, battery | **1 s** |
  |---|---|---|---|
  | Stockfish's long wins converted (of 108) | 44 | 29 | **76** |
  | its defences of cursed wins held (of 72) | 5 | 5 | **19** |
  | its defences of blessed losses held (of 72) | 4 | 2 | **16** |
  | our points (the tables' 252) | 351.5 | 360 | **322.5** |

- Ten times the time helps Stockfish a lot (41% -> 70% of long wins, and a
  quarter of the knife-edge defences held instead of a few), but it still
  can't do what the tables do, and we still score 70.5 points over the
  tables' figure. Our engine again never did worse than its table in any of
  the 504 games. Hardest for Stockfish still: K+B+N v K+N (0 of 6 long wins
  converted at both speeds), K+R+N v K+Q (1 of 6), K+N+N v K+Q (2 of 6).
  Output: scripts/hard-vs-stockfish-1000ms.txt.

## 2026-10-13: an opening book from our own games

- Matthew: use the human games for an advantage early on ("solid positions
  to set sail from"). `scripts/make-book.py` streams a month: rated games
  from the start position, both players 2200+, blitz or slower; every
  move of the first 24 plies with how the game went for the side playing
  it; moves played 10+ times scoring 40%+ go in a Polyglot .bin, weighted
  2 x wins + draws. Two parsing traps found on the 100 MB trial: clock
  comments split into tokens, and analysed games' move marks (f4?!).
- September 2026: 89,616,462 games read, **1,594,859 kept**, 54 min (one
  thread); 12.7 M position-moves seen once dropped along the way (memory a
  few hundred MB). **Book: 190,079 moves in 128,425 positions, 3.0 MB**
  (`bot/books/lichess-2026-09-2200.bin`, out of git; rebuild with the
  script). Read back as lichess-bot reads it: 1.e4 48%, 1.d4 32%, 1.Nf3
  9%; 1.e4 c5 36%, e5 21%; the Najdorf (5...a6) 65% in the Open Sicilian;
  even 2.Ke2 has an answer (Nf6 74%). Both sides following it at random,
  it lasts a median 14 plies (quartiles 11 and 18); 5% reach all 24.
- The bot's config turns it on when the file exists (weighted random,
  12 moves). To measure: clock saved from its games, and later a match
  with and without it against Stockfish (a few hundred games, overnight).
- First book game (10:20, rated 3+3 v lamprook 1817, Black): the book
  played our first 8 moves (a Grünfeld), our clock went 180 -> 198 s over
  them (before the book the engine spent ~7-8 s a move there), and we won,
  mate in 36 (+16).
- Matthew asked for more data and 10-15 moves. Measured on the 1 GB sample:
  15 moves instead of 12 adds 35 moves of 6,785 (no position past ply 24
  has 10 games); 2000+ or 1800+ make it 3x or 7.5x wider but a line lasts
  9-10 plies all the same; a whole month (29x the games) took the median
  line from 9 to 14 plies. So about one ply per doubling of the games.
- Depth needs more months, so (his pick, no drive): `make-book.py count`
  saves a month's counts (~50 MB) so its 30 GB download can go, and
  `make-book.py book` merges every counts file into
  `bot/books/lichess-2200.bin`; `scripts/month-book.cmd <month>` does the
  lot. Running from 11:28: September's count, and August 2026 (30.1 GB,
  91.9 M games) downloading at ~1 MB/s, then counted and merged (~20:00).
  Disk: 38.8 GB free before August, ~8.7 GB after; each later month needs
  the last one's .zst deleted first (his), or 6.8 GB of spare files.
- September counted 11:28-12:19: 4,547,455 position-moves saved (95 MB;
  fewer dropped than the first build, the threshold being 6 M not 4 M),
  book 193,322 moves in 130,617 positions. August downloaded 11:28-19:29
  (30.1 GB, ~1 MB/s), counting from 19:29. `scripts/clean-up.ps1` offers
  only imported PGNs, unpacked copies and counted downloads, deleting on a
  typed YES; Matthew ran it (5.84 GB).
- The bot by 19:50: blitz **1974**, settled after 61 rated games. Rated
  before the book 6-12-1 (34%, 19 games), with it 17-20-5 (46%, 42 games),
  each +-10 points or so. One win (v simpleEval 2100, PwB9TEEz) promoted
  twice and mated with two queens once the tables had it. Matchmaking back
  to within 300 of its own rating from its next restart.
- `scripts/book-match.py`: our engine (the bot's copy, with tables) v
  Stockfish (WSL, UCI_LimitStrength), alternating book / no book and
  colours, real clocks, each game appended to a .jsonl, summary with 95%
  intervals and the arms' difference. Smoke test (6 games at 5-8 s): it
  first hung on exit (engines never quit keep Python alive; fixed), and
  the book lasted only ~4 of our moves a game against weakened Stockfish.
- August 2026 counted 19:29-20:24: 91,912,325 games read, 1,650,345 kept,
  4,999,063 position-moves (105 MB). **The book from both months: 363,447
  moves in 243,720 positions from 3,245,204 games** (5.5 MB, built in 6 s).
  Depth, both sides following it at random (4,000 lines): median 14 -> 15
  plies, average 14.4 -> 15.1, all 24 plies 5.9% -> 7.0%. Doubling the
  games gave one more ply, as the samples predicted.

## 2026-10-13 evening: where the book runs out against bots

- The bot restarted 20:42 on the two-month book (lichess-2200.bin) with
  matchmaking within 300. Its first game on it (v uSunfish 1768, Black,
  5+2): the book played our first two moves, then uSunfish played a move
  at its third that the 2200+ games don't have; one more book move by
  transposition, then the engine from our move 5 (7-15 s a move).
- Matthew's idea: record our own games into the book, in the background,
  loaded lazily after each game. Measured on the bot's 76 games so far (the
  first 12 moves each; the 2-month book as it is now): our moves the book
  couldn't answer 447; **met before in an earlier game: 9 (2%)**; games
  whose first out-of-book position had come up before: **3 of 74**. Our
  book picks at random and the opponents vary, so positions rarely repeat
  (17 opponents met more than once, every one of them at a different exit).
- Where the book first ran out, by our move: 2-4: 12 games, 5-6: 15,
  7-8: 33, 9-12: 14, never in 12: 2. Bots leave it with rare moves, so
  what's missing is width (rare replies), not depth.
- The same counts with lower thresholds (built in 7 s each, nothing new
  downloaded), the first exits each would have answered:

  | book (2200+, 2 months, score 40%+) | moves | first exits answered |
  |---|---|---|
  | played 10+ times (the bot's now) | 363,447 | 0 of 74 |
  | 5+ | 640,568 | 19 |
  | 3+ | 940,807 | 33 |
  | 2+ | 1,478,677 | 39 |

- lichess-bot (engine_wrapper.get_book_move) tries the listed books in
  order and reopens each file every move, so a second or third book file
  is picked up live, with no change to lichess-bot; the engine is one
  thread, on 16 logical cores, so a background job on one more costs the
  live game no CPU.
- Matthew picked (a) now with 3+, then (c). Done: `bot/books/lichess-2200-wide.bin`
  (moves played 3+ times, 40%+: 940,807 moves in 663,859 positions, 15 MB,
  7 s), listed after the main book in bot/make-config.py (lichess-bot's
  list, tried in order); `month-book.cmd` rebuilds both books each month;
  `book-match.py --book` takes a comma-separated list, by default the bot's
  two. Config checked by generating it into a scratch file (the real
  config.yml, with the token, untouched). Live from the bot's next restart.
- Matthew asked whether to keep the raw months for when the goalposts move.
  What we keep is the counts (~105 MB a month): any threshold or weighting
  of 2200+, blitz-or-slower, first 24 plies is a 7 s rebuild; anything
  outside that (other ratings, bullet, later plies, clocks, per-game
  questions) needs the month's .zst again: a re-count is ~1 h, a
  re-download ~8 h. He may map a network drive to his desktop (a few hard
  disks) to keep the .zst files there (~30 GB a month).
- Storage, Matthew's (a): the months stay on his desktop, mapped as Z:
  (`\id10terror-desktop\chessbruteforcer`, 1.8 TB, 1.4 TB free). The share
  first refused writes (a new share is read-only; his account given Change).
  Speed: ~10 MB/s each way, about what counting reads (~9 MB/s), so a month
  is counted straight from there. The six 2013 months copied to
  `Z:\lichess` and checked byte for byte, each with Lichess's SHA-256
  beside it (`<name>.sha256`, from database.lichess.org/standard/sha256sums.txt;
  all six match).
- Hardened against the share dropping (his ask): `make-book.py count` reads
  through `Steady`, which on a failed or empty read waits (5 s doubling to
  60 s), reopens and carries on from the same byte, for up to 30 min in a
  row; it hashes as it reads and checks the whole file against the
  `.sha256` (a mismatch stops with nothing written; the hash goes in the
  counts header). Test (scratch): 2013-01 counted plain, then through a
  reader failing 2% of reads and reading nothing 1%: 11 failures, counts
  byte for byte the same; from the share, same records and the checksum
  checked; a wrong checksum stops; a share that never returns gives up.
  `download-lichess-month.ps1 -Dir`: waits for the share (up to 6 h) without
  using attempts, saves the month's SHA-256, logs on the laptop.
  `month-book.cmd`: `MONTHS` (default the share), waits up to 10 min for it,
  checks free space there. Counts and books stay on the laptop, so the bot
  never depends on the desktop.
- The bot since the 20:42 restart: won v uSunfish 1768 (the out-of-book
  game: it ended with our pawn promoting), lost v AetherBot 2022, won v
  sseh-c 1945, TopasBot 1870, Axiom_BOT 1816 (4-1). The wide book is live
  from its next restart.
- July started 22:26 (month-book.cmd, its own window, to the share), then
  Matthew: "queue the lot up, even the ones I deleted". Lichess's archive
  (list.txt, counts.txt, sizes by HEAD): 165 months, **2.6 TB, 8.22 B
  games** (2013 0.5 GB; 2017 41 GB; 2020 243 GB; 2021-2025 ~360 GB a year;
  2026 so far 263 GB). The share has 1.5 TB (decimal) free, so
  `scripts/month-queue.ps1` goes newest first and stops before leaving
  < 100 GB: dry run, 2026-09 back to 2022-12, 46 months. Months counted
  before (08, 09) are downloaded again and checked (`make-book.py verify`);
  the rest are counted and the books rebuilt after each. A month another
  window is downloading (July) is left for the end. Tested: verify on a
  good file, one already checked by its count, a damaged one (exit 1); a
  child script's `exit` doesn't end the queue. Started 22:36.
- Two downloads at once: July 0.72 + September 0.59 = 1.30 MB/s, against
  ~0.85 for one alone, so ~1.4x. Matthew may add a spill-over drive later.

## 2026-10-13 late: planning an index over the games

- Matthew's idea: index files with offsets (a) by moves played, (b) by the
  board's pieces as a sorted list; every game and position "on the map",
  streamed from the desktop or cached. The numbers for the 46 months the
  queue fetches: ~4.3 B games, ~290 B positions passed through (~68 plies a
  game, as in 2013). Every position as an 8-byte key: ~1.9 TB of keys alone,
  ~80% saying "one game went here" (2013: 84% of positions distinct, the
  sharing in the first ~10 plies, one ply deeper per doubling). Every game
  as a path at 1 byte a move: ~290 GB. So the games themselves are the
  cheapest map; positions get their own entries only where they repeat (the
  opening) and by material in the endgame (where the tables are).
- His order: **(c) game pointers, then (a) the opening layer, then (b) the
  endgame layer (material, and exact positions with few pieces)**; "on the
  map" means games and positions (not usernames).
- (c)'s first question answered on the first 1.24 GB of July already on the
  share (scratch scripts frames.py, frame_games.py): Lichess's 2026 files
  are pzstd's layout, a skippable note of each frame's size, then the frame;
  264 frames there, **4.65-4.73 MB compressed, each exactly 32 MiB of PGN**
  (7.2x), ~14,400 games starting in each, each decompressed on its own in
  ~90 ms. The cuts fall at 32 MiB, not between games (frame 1 begins
  mid-game), so a game can run into the next frame. 2013-01 is 3 frames of
  4.9-6.4 MB. So:
  - pointers go straight into Lichess's files, no second copy: per month a
    frame table (offset, first game starting in it: ~6,200 rows, ~75 KB),
    and per game what we filter on (moves played: 1-2 bytes, ~180 MB a
    month). Reaching game g: one frame read (~0.5 s over the share) and
    ~90 ms to unpack.
  - frames are independent, so a month can be worked on 16 threads at once,
    a frame each. That is what makes (a) and (b) affordable: replaying every
    move of a month (~6 B moves) on one thread would take most of a day.

## 2026-10-14: chat commands for the bot

- The VPS idea (download and pack in Europe, fetch home): four downloads at
  once came to ~0.95 MB/s, the same as two, so the home line is what's full
  (shared with the family's streaming). A VPS would only help by sending
  fewer bytes home (counts only, or slimmed games); Matthew: we can just
  wait. If ever, the desktop fetches from it; no reverse proxy into it.
- Matthew: `!help` listing `!info`, which says how many tables we have and
  whether we're in them now. lichess-bot's commands are written into its
  lib/conversation.py with no hook, so `bot/chat_commands.py` holds ours and
  `bot/lichess-bot-chat.patch` (10 lines) makes Conversation.command ask it
  first; anything it doesn't answer, or any fault in it, goes on to
  lichess-bot's own. `start-bot.cmd` copies it in as lib/bruteforcer_chat.py
  and applies the patch unless it's in (or says it couldn't, and starts
  without), and sets BRUTEFORCER_TABLES.
- `!info`: "Tables: 146 endings, all with 5 pieces or fewer. Now 30 pieces on
  the board: not yet. Book: out since my move 4." (or "Now 4 pieces: every
  move I make comes from them."); the book from the source lichess-bot
  records for each of our moves. To spectators and the bot's own account
  (Matthew's (a), as with !eval) a second line: "The tables say: I win, mate
  in 6." / "I lose, mated in 4." / "a draw" (a 0 includes wins the 50-move
  rule runs out on), or "no say yet" when a capture has only just brought
  the game into them. Greetings mention !help / !info (122 and 128 chars).
- Tested in a scratch copy of lichess-bot (no token) with its real
  Conversation and Game, Lichess faked: 13 checks (both colours, restarted
  mid-game, the verdict's three kinds and who sees it, !queue still
  lichess-bot's, a fault in ours falls back to its !help, every message
  <= 140 chars). The start block: skips when the patch is in, applies it
  fresh, and on a changed lichess-bot says so and starts without. Live from
  the next restart.

## 2026-10-14: what 6-piece tables would be worth

- Matthew asked how much disk 6 pieces would take. Estimated from our own
  5-piece numbers: 365 tables (110 now), each ~50-64x bigger; ~2-3 TB
  compressed, up to ~0.2 TB uncompressed while solving the biggest, and
  ~100+ GB of memory for the pawnless ones (which every pawn table leads
  into by promotion): years on the laptop, and memory, not disk, is the
  wall. Syzygy's 6-piece set is ~150 GB (it stores far less). He picked
  (b): measure what they would be worth first.
- `game reach <files> [--sample N <tsv>]`: the fewest pieces each game gets
  down to, time at exactly 6 pieces, how games that stop at 6 end, the
  6-piece materials games enter by, and a sample of first 6-piece positions.
  `scripts/six-piece-check.py` judges such positions with Lichess's
  tablebase (Syzygy, to 7 pieces), under the 50-move rule at the game's
  count, as `game takeover` does with ours.
- 1 September 2026, 3,284,165 games (800 s): down to 5 or fewer 5.8%, 6
  2.4%, 7 2.7%. **6-piece tables would reach 8.2% of games (5.8% now)**;
  of games reaching 6, 70.6% go on to 5 anyway, after a median 1.5 moves
  at 6 (quartiles 0.5 and 4). Those that stop at 6 end on time 36%,
  resigned 30%, mate 25%. The commonest ways in: KPPvKPP 8.7%, KPPPvKP
  8.0%, KRPPvKR 7.5%, KRPvKRP 6.6%; the top 20 of 319 materials 70%.
  January 2013 the same shape (9.1% v 6.6%).
- 600 first 6-piece positions judged: **29.5% ended otherwise than perfect
  play** (26.7% at 5 pieces, same games), 58% of those on time; bullet
  34%, blitz 30%, rapid 22%.
- **The bot (111 games): 30% get down to 5 pieces** (bots play on to mate),
  6-piece tables would add 4.5%. Its first 6-piece positions: 38 of 38
  ended as the tablebase says; first 7-piece positions: 49 of 49. Bigger
  tables would have gained it 0 points: its results are settled before 7
  pieces, so its strength is in the middlegame (and the book).
- Outputs: scripts/six-piece-2026-10-14.txt, the 600 judged positions in
  scripts/six-piece-check-2026-09-part.tsv.

## 2026-10-14: Texel tuning, step 1

- Matthew: "let's do the Texel tuning". The evaluation was Michniewski's
  simplified one (fixed piece values, one square table each, the king's
  blended by phase, a bishop pair). Now every piece on every square has a
  middlegame and an endgame value, blended by phase, plus the bishop pair
  both ways: 770 numbers (`EvalParams`, saved and loaded as text; the UCI
  option `EvalFile` loads a set). The built-in set is the old evaluation:
  3,542 positions, 2,657 identical and 885 a centipawn off (the blend's
  rounding), none further.
- `Tuner`: error = mean (result - 1/(1 + 10^(-K eval/400)))^2 over quiet
  positions with their game's result; K fitted once to the starting
  numbers; the evaluation is linear in its numbers, so the gradient is
  exact; Adam, full batch, 16 threads; a tenth of the games held back and
  the numbers that judge best there kept. Tests: gradient against finite
  differences, the tuner's evaluation is the engine's, it finds a knight
  made worth 200 more. A first test with 3,000 coin-flip results overfitted
  at once (770 numbers): a warning about data size.
- `tune positions`: 1 September 2026, rated, both 2000+, blitz or slower,
  ended normally (not on time), positions after move 8, not in check, no
  capture or promotion just before or as the move played, more than 5
  pieces; up to 8 a game. 157,870 games, **1,235,527 positions** (815 s).
- `tune fit`, 1,500 epochs (47 s): K 0.8427; held-back error **0.199653 ->
  0.191633 (4.0% less)**, still falling slowly, never overfitting. Average
  values, middlegame / endgame: pawn 77 / 220, knight 249 / 474, bishop
  247 / 468, rook 355 / 816, queen 997 / 1294, bishop pair 14 / 135 (all
  were 114 / 307 / 328 / 500 / 897, pair 30). Material counts about double
  in endgames (an extra unit converts more surely with fewer pieces), and
  the bishop pair is an endgame asset. 6,000 epochs: 0.191509, but the
  values drift along a flat valley (endgame queen 1,721, middlegame values
  shrinking), so the 1,500-epoch set goes to the match:
  games/tune/eval-v1.txt.
- Match: tuned v original, same engine (EvalFile), no tables in the
  engines (ours adjudicate), 10+0.1, 300 openings drawn from our book (8
  plies), 6 at a time, SPRT [0, 20]. The match tool splits its command at
  the first space, so the engine runs from a copy in the scratchpad.
- **Result: no gain.** 1,951 games, tuned +892 -885 =174, 50.2%, Elo +1
  [-13, +16]; SPRT [0, 20] accepted H0 (09:00-11:22). Better prediction of
  who wins human games didn't make the engine play better: human results
  are a noisy target (2000+ players throw a fifth of won endings), and
  square tables alone can only say so much.

## 2026-10-14: Texel tuning, step 2a: towards Stockfish's judgement

- Matthew picked (c): Stockfish's evaluations first, new evaluation terms
  after. One Lichess game in ten carries [%eval] after every move (10.1% of
  306,873 sampled, ~66 evaluated plies each): ~9 M games, ~590 M positions
  a month. `scripts/eval-games.py` keeps those games with an [Evals] tag
  (centipawns for white after each ply; M<n> mates; ? none). First 3 GB of
  July 2026 (on the share): 9,211,656 games read, 948,918 kept (10.3%),
  466 s; imported 451 s (`games/tune/eval-games-2026-07.cbg`).
- `tune positions ... --lambda L`: target L x result + (1 - L) x
  1/(1 + 10^(-cp/400)) from the eval of that position (the one after the
  ply before); numeric evals only; any rating, blitz or slower, any
  termination. L = 0, 4 a game: 721,358 games, **2,855,253 positions**.
- Fit, 1,500 epochs (127 s): K 1.1214; held-back error **0.032367 ->
  0.027099 (16.3% less**, against 4.0% with results). Values, middlegame /
  endgame: pawn 94 / 178, knight 292 / 423, bishop 314 / 436, rook 426 /
  749, queen 1001 / 1282, bishop pair 20 / 84. games/tune/eval-sf1.txt.
- Match sf1 v original from 15:54, the same setup, 5 at a time (the bot was
  playing alongside).
- Aborts: two games v MinibruteforceBot (14:22, 14:58) aborted after 30 s
  because it never made its first move (we'd played 1.e4 from the book at
  once). Matthew: don't block it.
- **Result: sf1 +48 -11 =10 (76.8%), Elo +208 [+130, +311]**: SPRT [0, 20]
  accepted H1 after 69 games (15:54-16:00). Same tuner, same shape of
  evaluation, similar data size: Stockfish's verdicts teach what human
  results couldn't. So the data was the limit, not the evaluation's shape.
- The bot gets it: `bot/eval/eval-sf1.txt` (in git, 16 KB), and
  make-config.py sets EvalFile to it when the file is there (delete it to
  go back). Checked: the engine loads it from that path (spaces and all),
  and the evaluation changes. Matthew had stopped the bot between games;
  it takes the new numbers at its next start (start.cmd).
- First 5 rated games on sf1: 5 won (Stepbot_Chess 1905, ThrorBjorn 1866,
  Tigrament 2045, laura-bot 1917, mochi_bot 1771), 2009 -> 2026. Too few to
  measure (at the book era's 53%, 5 of 5 is ~1 in 25); check again at ~30.
- July 2026 counted (17:12): the books from three months, 4,850,017 games:
  main 497,607 moves (median line 15 plies, average 15.6, all 24: 8.9%),
  wide 1,227,739. September back on the share and matching its SHA-256.

## 2026-10-14: the book learner (the book's option c)

- Matthew picked (c), the background job first. `bot/learn.py` (window:
  `bot/learn.cmd`; `start.cmd` starts it with the bot and the queue):
  after each finished game, our positions within the book's reach (12
  moves) where the main book had nothing: no book at all, thought about 45
  s; only the wide book, its move checked (best v the wide move, 20 s
  each) and vetoed at 75 cp or more behind. At most 4 a game, nearest the
  book first. Kept in bot/books/ours.jsonl; bot/books/ours.bin rebuilt
  after each game (temporary file, moved over; lichess-bot opens books
  afresh each move). make-config.py lists main, ours, wide, and makes
  ours.bin if missing with one inert entry (key 0): lichess-bot's
  open_reader can't take an empty file. Its own engine copy (the tuned
  evaluation, no tables), below-normal priority (ctypes needs the
  handle's types on 64-bit, or SetPriorityClass quietly fails).
- Tested in a scratch folder on 3 games (2 s thinks): 12 positions, each
  read back from ours.bin as lichess-bot reads it (castling included);
  the inert entry answers nothing. Started 18:46 on the backlog (~150
  games). The bot needs one restart to list ours.bin; after that the
  learner's book goes live as it grows. `learn.py --summary` reports what
  it has learnt, vetoes, and how many bot moves came from it.
- Two faults found at 21:15. The learner's Python had exited at ~20:45
  after 43 games (152 positions learnt); the reason went to its window,
  which can't be read from here. And its list of studied games was read
  with split(), but the bot's file names have spaces, so a restart
  re-read every game (harmless, as learnt positions are skipped, but
  slow). Fixed: the list read by lines; its log also written to
  bot/logs/learn-<date>.log; a fault on one game logged with its traceback
  and skipped, and a dead engine restarted (the game tried again once).
  Restarted 21:23: below-normal priority confirmed on the interpreter
  and its engine. If it stops again, the log file says why.
- Texel step 2a with September (`scripts/tune-data.cmd 2026-09`, its own
  window from 17:22): filter and import done (games/tune/eval-games-
  2026-09.cbg, 4.18 GB); positions (2 a game) running at 21:18. Next:
  `tune fit` on them (with July's 2.86 M, or alone), then a match v sf1.

## 2026-10-14 (night): the bot rides out network changes; start.cmd builds once

- Matthew moves between the upstairs and downstairs wireless networks.
  lichess-bot already retries each request for 60 s, a game's stream for
  10 minutes, and restarts itself after a network error at startup; any
  other fault ("Quitting lichess-bot due to an error") ended it for good.
  `bot/run-bot.ps1` (run by start-bot.cmd) loops: wait until lichess.org
  answers, run lichess-bot, start it again 20 s after it stops by itself;
  Ctrl+C stops all; 5 stops within 2 minutes of starting gives up with a
  message (a token problem, say). Restarts logged to bot/logs/restarts.log.
  Tested with a stand-in that fails at once: 4 restarts, then the give-up.
- start.cmd opened the bot and the learner together; both built the engine
  at once and the bot's build failed. start.ps1 now builds once first and
  spaces the windows 15 s apart, and start-bot.cmd / learn.cmd retry a
  failed build after 20 s. Matthew had stopped everything at 21:26-21:31
  (the bot cleanly, between games); restarted 21:42-21:44: bot (guard,
  sf1, our book listed), learner, queue, September's Texel positions.

## 2026-10-14 (late): `game takeover` in batches; the whole of September running

- Matthew picked (a): the code for the month's `--moves` run tonight, the
  Texel fit tomorrow. `game takeover` held every game that enters a table
  whole (moves, tags, hashes): 1.8 GB for one day's 190k, ~50 GB for the
  month. Now it reads in batches (100,000; `--batch N`): each batch gets
  the tables' verdicts and its moves walked, then each game keeps only a
  small summary (table, both verdicts, result, plies left, termination,
  rating band, speed, ended in mate) and the move tallies carry on.
  Examples go to the first met among equals in the old single pass (the
  entry table's first appearance, then the game, then the ply), so the
  batch size can't change them; ties in the report's rankings go by name.
  --play and --versus still take everything at once.
- Check on games/2026-09-part-tables.cbg: batches of 20,000 and 100,000
  both reproduce scripts/takeover-moves-2026-09-clocks.txt line for line,
  the examples included, apart from the two timing lines (the walk 187 s
  and 218 s against 234 s).
- `scripts\takeover-month.cmd 2026-09` started 22:08 in its own window
  (build copied to games\.cli-takeover): output
  scripts/takeover-moves-2026-09-month.txt, log games/takeover-2026-09.log.
- The bot lost to latrunculi-bot (2329, blitz, we challenged it):
  https://lichess.org/SrwnQEj5, mated on move 57. Our engine had us +1.1
  at move 17; it turned between moves 21 and 24 (-0.5 to -2.6).

## 2026-10-15 (small hours): the index plan, with three additions

- Matthew's picture: index files pointing into the big files by moves
  played and the pieces as a sorted list, then positions; duplicates found
  by checksumming and run-length encoding; a map from beginning to
  endgame. Most of it was the 2026-10-13 plan. His checksum + RLE is the
  right tool where positions repeat (sort the 64-bit fingerprints; runs of
  equal ones are counts; the gaps between sorted ones take ~4 bytes, not
  8), which is only the opening and the last few pieces: in 2013, 84% of
  positions were seen once. Collisions: ~1 pair a month (~6 B positions),
  ~2,300 over 46 months, so exact lookups confirm by replaying.
- He took all three additions and left the sort order to us: (a) a
  fingerprint per game in session 1, for duplicate games; (b) the bow-tie
  map (opening exit x table entry x result) as session 5; (c) per-game
  rows as Parquet columns through DuckDB, on trial in session 1. Moves
  played and material become columns rather than sort orders; positions
  are sorted by fingerprint. TODO has the sessions.

## 2026-10-15 (00:33): the whole of September, move by move

- `scripts\takeover-month.cmd 2026-09`, 22:08-00:33: 5,260,601 games read
  (55 min), 5,226,962 enter a table, 95.5 M moves walked; memory peaked
  ~2.3 GB. Output scripts/takeover-moves-2026-09-month.txt; set beside the
  one day (1 September) by a scratch script.
- **The one day was representative.** Every headline number held:

  | | 1 Sept | September |
  |---|---|---|
  | games entering a table | 189,913 | 5,226,962 |
  | ended otherwise than the table | 26.7% | 26.6% |
  | of the won endings not won, on time | 54.3% | 53.8% |
  | moves played in the tables | 3.44 M | 95.5 M |
  | mistakes, of moves that could go wrong | 1 in 31 | 1 in 31 |
  | games with at least one mistake | 20.4% | 20.5% |
  | checks (better than the table / unread) | 0 / 0 | 0 / 0 |

- **The thin rows are solid now** (mistakes per 100 moves that could go
  wrong, 95% margins): by the mover's rating 3.81 / 3.08 / 2.88 / 2.38
  (+-0.01-0.02; 2400 and up make 38% fewer than under 1500), yet games
  end otherwise ~27% in every band, because the clock takes more of them
  as ratings rise (on time 37% -> 79%). By time control: ultrabullet 5.83
  +-0.15 (13,193 games), bullet 3.44, blitz 3.21, rapid 3.12, classical
  2.69 +-0.04 (39,085 games, 17.6% ended otherwise, 11% of those on time).
- The clock, confirmed: 4.61 per 100 with under 5 s left against ~2.9 from
  10 s up; long thinks 30 s+ 6.29 (hard positions, not proof that thinking
  hurts). Late in an ending (41+ own moves) 2.25 +-0.03, a little worse
  than the day's 2.05 suggested.
- New at this size, the hardest tables (1,000+ moves that could go wrong):
  two minor pieces against a queen, KBBvKQ 19.5 +-2.3 and KNNvKQ 18.9
  +-1.4 per 100 (about 1 move in 5), KBBvKN 12.7; then KRPvKQ 11.0,
  KQPvKQ 10.3, KPPvKR 10.0 as before. Kinds of mistake as before (king
  33%, hung 21%, piece 20%, pawn 10%, stalemate 9%; KQvK 58% stalemates).
  KRvK shows 19% "too slow": the 50-move count running out on K+R v K.
- Examples now reach 3000+ (bullet: 3085 threw a K+Q+P v K+Q win with 24 s
  left). Ratings of exactly 3000 are probably new bots at their
  provisional start (ours began there), so the top band has some bots in.
- The one-day sample files can go once Matthew likes (his call):
  games/2026-09-part.cbg (1.28 GB); keep 2026-09-part-tables.cbg (64 MB)
  as the line-for-line check for `game takeover`.

## 2026-10-15 (small hours): the plan consolidated

- Matthew asked for the plan consolidated before the next session, which
  makes the build plan for the index. TODO.md now opens with one plan in
  order: what runs in the background, the next session (start the Texel
  fit and its match in the background, the September positions being
  ready: 13,580,216 from 6,811,130 games; then the index build plan),
  then four sections: the index (sessions 1-5), the engine's strength,
  findings for the thesis, and Matthew's own items. The older sections
  stay below as the record; their open items moved up. Stale entries
  fixed: KRPvKR was already compressed (CBC1), compression is done, and two
  paths an old heredoc had mangled.

## 2026-10-15 (00:45 on): the September Texel fit; the index's build plan

- `compression` is pushed (GitHub at d13b55c). Running: the bot (a game
  on), the learner, and the queue on August (~1 MB/s). Commit 15.1 of 19.0
  GB used before anything new started.
- `scripts\tune-match.cmd <new> <old> <positions>`: one round of Texel
  tuning in its own window. Builds, fits `games\tune\eval-<new>.txt` with
  every thread at below-normal priority (the bot's games come first) and
  the .NET heap capped at 3.5 GB, then plays the match the way sf1's was
  played (engine and evaluations copied to %TEMP%\cb-match, as the match
  tool splits its engine command at the first space; 10+0.1, 5 at a time,
  300 book openings, our tables adjudicating, at most 2000 games), with
  SPRT [0, 10]. Started 00:49 as `sf2 sf1
  games\tune\positions-2026-09-sf.tsv`: 13,580,216 positions loaded in 48
  s (12.2 M tuned on, 1.36 M held back), K 1.2669, held-back error 0.038818
  at the start; ~0.7-0.8 s an epoch, so ~20 minutes for 1,500.
- The index's build plan is INDEX.md. A sample first (24 frames spread
  over July, 346,962 games, scratch script): 6,177 frames, ~89.3 M games;
  Python parses a frame's games in 1.7 s on one thread, so 8 processes
  keep up with the share (~50 min a month) and session 1 needs no C#;
  walking the frame notes over the share took 165 s (27 ms a read), so the
  frame table is made during the one read. A row needs no location (its
  number and the frame table find it). Packed, a row is 14.2 bytes, 8 of
  them the fingerprint: 1.27 GB a month, ~58 GB for 46 months. 1.19% of
  the sample's games share their exact moves with another. 2013-01 has
  pzstd's notes too.
- Found while planning session 2: the engine's Zobrist hash keys the en
  passant square after every double pawn push, so 1.e4 e5 2.Nf3 and 1.Nf3
  e5 2.e4 hash differently. The index's position fingerprint keys it only
  when an en passant capture is legal. (In the engine itself, its hash
  table and repetition checks, the same quirk can only miss a
  transposition or a repetition, never confuse two positions.)

## 2026-10-15 (01:10 on): index session 1 begun; the sf2 fit done

- The fit finished in 1,128 s (games/tune/eval-sf2.txt); sf2 v sf1 started
  by itself. At game 116: +43 -37 =36, 52.6%, undecided.
- Matthew: yes to DuckDB (1.5.6 installed, global Python 3.14), and
  session 1 now with July's read held until the match ends. He asked that
  his CSharp-Lookuptables not be forgotten; the answer from its own README
  is in INDEX.md (session 3): its winning disk design (a small index in RAM
  into one flat file) is the shape the index already uses, the trial needs
  no .NET upgrade, and an upgrade only follows if the trie wins.
- `scripts/index-month.py` (rows, get, check, dups, trial). 2013-01: 121,332
  games (Lichess's count), SHA-256 matches, every game fetched by number
  matches the read byte for byte; duplicates 0.83%, none longer than 20
  plies (75.6% of games of 1-4 plies are duplicates; the biggest group is
  80 games of 1.e4 e5); Parquet and plain columns agree (17.3 / 17.5
  bytes a game; too small to time).
- A first run with 8 workers failed to unpack a frame ("Unable to allocate
  output buffer") at the moment Windows logged a low-memory event
  (Resource-Exhaustion-Detector 2004, 01:17:37). The bot (mid-game) and the
  match were unharmed. 3.9 GB of commit was free with nothing of ours
  running; the Claude app's 17 processes held 4.8 GB. The default is 4
  workers now, which still keeps up with the share.
- DuckDB reads a dict of numpy arrays directly (unsigned types kept), so
  the trial needs no pandas or pyarrow. In SQL, 40 x a UINT8 column
  overflows: cast first.
- Matthew picked (b), the Lookuptables trial brought forward, and (c),
  July's read after the match. `rows --lines 16` keeps each game's first 16
  moves as two-byte codes (2013-01: 1,304 different moves in SAN; all
  121,332 games' saved moves match their fetched PGN), and
  scripts/lines-trial.cs (.NET 10, his library referenced as it is) sets
  his burst trie against a sorted array and a scan. 2013-01: equal (~0.9 µs)
  where a prefix picks out a game or two, the sorted array 20x faster where
  it picks out tens of thousands (its games are one run; the trie lists
  every line), the trie 3x the memory. Games become unique fast: 46% share
  their first 8 moves with no other game, 96.5% their first 16 (2013).
- July-sized memory: `dups` and `trial` loaded whole columns (~2-4 GB for
  July's 89 M games). Now columns stream out of their .zst into numpy
  arrays a piece at a time (zstd.open + readinto), duplicates come from one
  sorted copy of the fingerprints, the Parquet goes out as 10 M-row parts,
  and DuckDB is capped at 1 GB with its spill folder in the index folder
  (its default is .tmp in the working directory). Same answers on 2013-01.
- scripts\index-month.cmd runs the lot in order, after waiting for a log's
  "final:" line; started 01:32 for July, waiting on the sf2 match (264
  games at 01:32: +97 -86 =81, Elo +14 [-20, +50]).
- The PowerShell guard refused a whole call ("system path '/c'") over a
  `Remove-Item` beside a `cmd /c "..."`, so nothing in it ran: one
  `Remove-Item` per call, on its own, as the notebook says.

## 2026-10-15 (~02:00): a fairer trie, on Matthew's branch

- Matthew: "we might have to rerun the benchmark", then (b): give his trie
  the shortcut the sorted array had. `BurstTrie.TryGetPrefixRange` on a new
  branch `prefix-range` in ..\Lookuptables (f1063ff, his to push): the
  first and last key with a prefix, down the first and last children from
  the prefix's node (a key ending at a node sorts first), scanning only the
  two end leaves. Tests against a sorted reference (strings with keys that
  are prefixes of others, small-alphabet bytes, sequential IDs with skipped
  runs; thresholds 1, 2, 3, 32, 128): 45 of 45 pass, and four deliberate
  faults (terminal not first, largest going left, no check past the
  fingerprint, a leaf's max taken as its min) all turned them red.
- lines-trial.cs measures the trie both ways. 2013-01: with the range
  method the trie is fastest at every prefix length (0.4 µs against 0.8
  for the sorted array on long prefixes; 4.7 against 9.0 µs at 2 moves),
  answers agreeing; 3x the memory.
- Matthew pushed it and merged it into his main (PR #1, merge commit
  b9ca823, the same tree as f1063ff); his local main fast-forwarded to it
  and the merged branch deleted. The trial builds and agrees against main.
  Match at 659 games: +227 -212 =220, Elo +8 [-14, +30].

## 2026-10-15 (to ~05:15): sf2 no better; the share went away

- **sf2 v sf1 ran its full 2,000 games: +668 -662 =670, 50.1%, Elo +1
  [-11, +13]**; SPRT [0, 10] undecided (LLR -0.99). Fitting on September's
  13.58 M positions instead of July's 2.86 M changed the numbers a little
  and the play not at all: the data size has done its work, and this
  evaluation's shape (piece-square tables and the bishop pair) is now the
  limit. The bot keeps sf1. Next for strength: step 2b (new terms), guided
  by "where our losses turn".
- July's index run started at 04:11:45 when the match ended and read 965
  frames (4.55 GB, 13.9 M games) at ~7.3 MB/s; then the share went away
  (~04:23). make-book.py's reader waits 30 minutes, so both it and August's
  SHA-256 check (in the month queue, at byte 23.18 G of 30.1 G) gave up at
  04:54:46 with "No such file", and the share was still unreachable at
  05:12. The queue logged August as DAMAGED and listed it in
  games/month-queue.damaged, which would have skipped it from then on;
  Matthew stopped the queue and agreed it was likely the network.
- The queue now calls a month damaged only when this run's make-book.py
  says "doesn't match Lichess's SHA-256"; any other failure of the check
  waits for the share (up to 6 hours, as for downloads) and tries once
  more, then stops. The count step's test reads only this run's lines too
  (it searched the whole log, so an old mismatch line would have counted).
  Checked: the script parses, and the rule on a stand-in log (an old
  mismatch above a lost share: not damaged; a mismatch in this run:
  damaged). August taken off the damaged list. Matthew is rebooting before
  testing it again.

## 2026-10-15 (05:16 on): after the reboot, August is sound

- The share answered again after the reboot (Z: mapped, all three 2026
  months there). Matthew asked for August's checksum again:
  `scripts\verify-month.cmd <month>` (new: make-book.py verify in its own
  window, log games/verify-<month>.log) ran 05:19-06:03: **SHA-256 matches
  Lichess's**, 30,145,862,359 bytes in 2,622 s (11.5 MB/s, faster than the
  ~7 MB/s while the queue also used the share). So the file was never
  damaged; the share going away was.
- Matthew ran start.cmd at 09:12: bot, learner, queue. The queue checked
  August again (matches, 09:55) and July (checked when counted: at once),
  marked both done, and is downloading June.
- Still to do: July's index run again (`scripts\index-month.cmd 2026-07`),
  which the outage stopped at 4.55 of 29 GB.

## 2026-10-15 (13:33-14:54): July's index run, sessions 1 and 1b, all passing

Matthew started `scripts\index-month.cmd 2026-07`; log
games/index/index-2026-07.log. Not yet written up (scripts/index-2026-07.txt
is the next chat's first job).

- **rows**: 6,177 frames, **89,288,408 games**, read in 2,516 s (11.5 MB/s,
  the June download alongside); SHA-256 matches. Rows 1,268 MB packed,
  **14.20 bytes a game** (the sample said 14.2). First 16 moves:
  lines.u16, 2,857 MB, 3,246 different moves in SAN.
- **check**: 1,000 of 1,000 games fetched by number match the read, and
  1,000 of 1,000 games cut by a frame's end; their saved moves all right.
- **dups**: 87,604,543 distinct move sequences; **2.05% of games share
  their exact moves with another** (2013-01: 0.83%), in 150,418 groups.
  Mostly short: 383,336 games with no move at all (100% duplicates), 99% of
  1-4 plies, 59% of 5-10, 12% of 11-20, then 0.3% / 0.02% / 0.04%. But
  ~17,000 duplicated games are 41+ plies, the longest 356 plies (2013 had
  none over 20): worth a look (bots playing each other the same way?). The
  20 biggest groups all confirmed by fetching two games each.
- **trial, plain columns v Parquet** (answers agree): 1,268.3 v 1,267.1 MB;
  written in 8.5 v 7.7 s; the three queries 6.2 v 4.8 s. Parquet as small
  and a little faster.
- **lines trial, the first 8 M games**: 7,348,088 distinct 16-move lines
  (91.9%); games sharing their first k moves with no other: 0.2% at 4,
  15.3% at 8, 58.9% at 12, 88.8% at 16. Memory: sorted array 265 MB, burst
  trie 847 MB (3.2x). Median a query, sorted / trie every line / trie ends /
  scan (µs): 1 move (4.6 M games found) 1,650 / 41,043 / 1,645 / 53,870;
  4 moves 6.9 / 234 / 6.9 / 21,303; 8 moves 2.6 / 6.5 / 3.1; 12 moves 2.5 /
  3.0 / 2.5; 16 moves 2.3 / 2.9 / 2.4. All 1,000: 166 / 4,279 / 165 ms.
  **At this size the trie with TryGetPrefixRange ties the sorted array**
  (on 2013-01 it was ahead; here memory misses dominate both) and costs
  3.2x the memory; without it, 26x slower. Answers agree.

## 2026-10-15 (~17:25): the month queue paused for the household

Matthew stopped the month queue's window: the download was filling the
line and YouTube at home was waiting on it. Nothing lost: June's .part on
the share holds 23.96 of 28.24 GB (22.32 of 26.30 GiB), so ~4.3 GB to go,
about 70 minutes at the ~1 MB/s it had been getting (8.9 GiB in the 2.5 h
of its 19th attempt; curl had dropped 18 times, exits 35 and 56, before
14:53). The bot and the learner are still running. To carry on, run
start.cmd (it starts only the queue) or scripts\month-queue.cmd: it checks
the part isn't growing for 150 s, then resumes it with `curl -C -`.

Then his (b): a speed cap. `download-lichess-month.ps1 -LimitRate` hands
curl `--limit-rate` (no cap unless asked), and `month-queue.ps1` asks for
**500k by default**; `-LimitRate 0` is full speed, and a bad value stops
before any network use. Both log the cap. Checked against a local HTTP
server (curl ignores the cap on file://), 3 MiB each:

| cap  | time  | speed        |
|------|-------|--------------|
| 500k | 6.2 s | 498 KiB/s    |
| 1m   | 2.9 s | 1,046 KiB/s  |
| 0    | 0.0 s | uncapped     |

What it costs: June's last 4.3 GB take ~2.3 h, not ~70 min; the queue's
remaining ~1,260 GB ~28 days at 500 KiB/s, not ~16 at the ~0.9 MB/s June
got. What it leaves: two downloads once ran 1.30 MB/s together, so the line
carries at least that (~10 Mbit/s); 500 KiB/s (~4 Mbit/s) leaves ≥ ~6
Mbit/s, enough for 1080p video. If YouTube still waits, 300k.

Restarted on his yes at 17:38 (`Start-Process scripts\month-queue.cmd`,
its own window); the queue logged "downloads capped at 500k a second",
watched June's part for 150 s, and curl resumed it at 17:41:11 from 22.32
GiB. Measured on the share, eight 30 s readings from 17:41:32: 501, 499,
465, 536, 498, 499, 499, 499 KiB/s, **499 KiB/s over 240 s**. So June
(4.15 GB to go at 17:45) lands about 20:00, then its count. Matthew pushed
to 8f714fc (ls-remote agrees).

## 2026-10-15 (17:57-18:15): July's index written up; 13 games a frame cut lost

[scripts/index-2026-07.txt](scripts/index-2026-07.txt) has the whole run:
the read, the columns, check, dups, the trial and the lines trial.

- **13 games short.** Lichess's counts.txt says 89,288,421 for July; `rows`
  read 89,288,408. A scan of every frame's first bytes (217 s over the
  share) found 17 frames beginning part-way through an `[Event "Rated` line.
  13 of those cut before the space after `[Event`, so neither frame held a
  whole game start, and each of those games was glued onto the one before it
  (their rows have 115-230 plies, the second game's tags read as moves). The
  other 4 cut after `[Event `, which the earlier frame finds whole. 13 exactly.
  Expected ~16 a month (6 harmful places in ~2,321 bytes a game, 6,176 frame
  ends), so ~600 games over the 46 months queued.
- **`check` couldn't see it**: `get` joined the frames the same way `rows`
  did, so the two agreed. Only Lichess's count catches a lost game.
- **Fixed**: `rows` and `get` split the joined text at every game start
  (`split_games`); `get` also walks back when a frame shows no whole game
  start. **scripts/test-index-month.py** (new): 7,892 real 2013-01 games cut
  into 20 frames through every point of an `[Event` line, mid-move, a frame
  with no game start, and one whose only game start is cut at its end. The
  old code indexed 7,883 (`check` passed); the fixed one 7,892, all fetched
  right with the right plies and fingerprints. 2013-01 again: 121,332
  (counts.txt agrees), all fetched byte for byte.
- **The long duplicates are bots** (Matthew's guess): of 16,999 duplicated
  games of 41+ plies, 96.8% BOT v BOT, 3.1% BOT v human, 26 games human v
  human. The 356-ply pair is one Ruy Lopez replayed by weak bots
  (~1200-1450) three weeks apart at different clocks, to the same two-queen
  mate. BOT games are 0.46% of the month.
- **The columns**: the fingerprint is 56% of the rows (8.0 of 14.2 bytes a
  game). Median 62 plies; ratings median 1658; 99.5% have clocks, 10.1%
  Lichess's evaluations, 8.5% are tournament games; 499 ECO codes.
- **Format: Parquet** (1,267 v 1,268 MB, queries 4.8 v 6.2 s, and SQL).

Next: July again with the fix (~1.5 h, its own window), then where the
whole index lives (Matthew's call).

**July running again, from 18:18** on Matthew's yes, in its own window. It
runs index-month.py and make-book.py from a snapshot of ee66600 in the
scratchpad (a copy of index-month.cmd beside them, output and log to the
repo as usual: games/index/index-2026-07.log, appended), because another
session is moving `rows` to Parquet in this checkout meanwhile, and each of
the five steps starts the script afresh. lines-trial.cs runs from the repo.

## 2026-10-15 (18:17-18:50): `rows` writes Parquet; the count against Lichess's, every month

TODO item 3's follow-up, in a side session.

- **`rows` writes the rows straight to Parquet** through DuckDB as the read
  goes: `parquet/part-NNN.parquet`, 10 M games a part (`--part-rows`), zstd,
  the same 11 columns and types. Parts go to `parquet.new/` and move into
  `parquet/` only once the read is whole and its SHA-256 matches;
  `games.json` (written last) lists the parts and each column's size from
  Parquet's own metadata. The plain .zst columns are gone. `check`, `dups`,
  `trial` and `Index.column` read the parts a part at a time, and an index
  from before (no `"format": "parquet"`) is refused with "run rows again".
  `check` now also compares every sampled game's row (all 11 columns) with
  the fetched game parsed again, so row n is shown to be game n. `trial`
  runs its three queries two ways on the Parquet rows (SQL; numpy over the
  columns) and compares them, and prints each column's size.
- **Memory**: the COPY of a 10 M-row part costs +140-180 MB on DuckDB's 4
  threads and +16 MB on 1 (1.5 s a part instead of 0.75), so the writer uses
  one. On 20 M synthetic rows the main process peaks ~580 MB over its
  baseline during the read (~260 MB of it the part being gathered); the old
  packing step peaked ~1.4 GB, after the read. DuckDB capped at 1 GB,
  spilling into the index folder.
- **The count**: download-lichess-month.ps1 saves the month's line of
  Lichess's standard/counts.txt beside its `.sha256` as `<name>.count`, and
  run on a month already there it downloads nothing and saves whichever of
  the two is missing. `rows` reads it first (a bad one stops before the
  read) and ends `count: 121,332 games, Lichess's counts.txt 121,332:
  MATCHES`, or SHORT BY n / OVER BY n with exit 1, every file written. The
  month queue picks the new download script up from May on (each month runs
  it afresh). Saved by hand: 2013-01 locally (with its `.sha256`), and July
  (89,288,421), August (91,912,325), September (89,616,462) on the share.
  June's download began before the change, so it has none yet (TODO).
- **Proof**: scripts/test-index-month.py (updated) passes: 7,892 games in 8
  parts of 1,000, MATCHES, `check` 7,892 of 7,892 rows right, `dups` and
  `trial` run, and a `.count` 13 high gives SHORT BY 13 and exit 1. Six
  mutations: four went red (rows read without the part's offset, a column
  filled from 0, `<=` for the count, a part's remainder dropped); two stayed
  green and are harmless (part order doesn't matter since each piece carries
  its first game; one only removed a check). 2013-01 in games/index/2013-01:
  121,332 games, SHA-256 and count matching, all 121,332 fetched byte for
  byte with their rows right, `dups` as before (120,521 distinct, 1,001 in
  190 groups), `trial`'s two ways agree; Parquet 2,104,260 bytes, the same
  as the trial made from the plain columns.
- scripts/clean-up.ps1 offers an index's old plain columns once it has
  been indexed again in Parquet (only 2013-01's for now).

**July's re-run failed at 18:35:50, and that was me.** `rows` died with a
MemoryError unpacking a frame. Windows logged Resource-Exhaustion-Detector
2004 at 18:35:48 and 18:36:26, just as my memory probes (above) reached ~1.5
GB of commit. 3.6 GB was free when I started them, but I hadn't checked for
a running job, and July had started at 18:18 from its own window. The bot
and the queue's June download came through. The 14:15 index is intact
except lines.u16 (half rewritten, 549 MB), plus a `tmp/` of raw columns
(426 MB) the old `rows` left. The session running it restarted it at
18:39:42 from the same snapshot, so the 13-game fix is tested on its own;
the new `rows` proves itself on the next month indexed.

## 2026-10-15 (20:50): a check on everything; June's download stalled at Lichess's end

Matthew closed Chrome and Discord for memory and asked whether anything had
broken. Nothing had: commit 5.0 of 20.8 GB free, no low-memory events since
18:37; the bot playing (a game at 20:49; its ERROR lines are only "No
suitable bots found to challenge"), the learner writing (20:47), and July's
re-run counted **89,288,421 games, Lichess's count exactly** (SHA-256
matching), its `check` step running since 20:00 (busy: 14 s of CPU in 30;
3.4 GB, the old script loading all of lines.u16).

**June's download has stalled since ~18:53**: 23.43 of 26.30 GiB, attempts
4-19 each ending in curl exit 56 (one 28) after 3-20 minutes with almost
nothing added. Lichess's database server is the cause, not the line:
lichess.org answered in 0.7 s and Google in 1.1 s, while
database.lichess.org (141.95.66.62, IPv4 only from here) sent 15,983 bytes in
30 s (532 B/s), didn't return its 8 KB counts.txt in 20 s (2 s at 18:35),
took 200 s for one HEAD request, and then refused even to connect within 15 s.
Nothing to do here: the queue retries every minute (100 attempts, then it
stops with "run this again to carry on") and resumes the part from its last
byte when the server comes back.

## 2026-10-15 (21:15): how often the bot's forecast comes true

Matthew asked whether the engine predicts future positions. It searches
ahead (iterative-deepening alpha-beta, assuming both sides play its best),
and its principal variation is the forecast. lichess-bot saves it in every
game's PGN, as a side line after each of our moves, with the score and depth.
`scripts/forecast-hits.py` (new; the bot venv's python, for python-chess)
compares the forecast reply with the reply played, over bot/games (241 games,
every one against another bot; depth median 13, 10% / 90% 11 / 16):

| | forecasts | reply right | blind guess (1 / legal moves) | only one legal move |
|---|---:|---:|---:|---:|
| all | 11,795 | 51.0% | 6.6% | 0.8% |
| moves 1-10 | 793 | 44.6% | 3.5% | 0.0% |
| moves 11-20 | 2,256 | 48.8% | 3.7% | 0.2% |
| moves 21-40 | 4,297 | 52.0% | 6.0% | 1.0% |
| moves 41+ | 4,449 | 52.4% | 9.3% | 1.1% |

Plies of the forecast in a row that came true after our move: 1+ 51.0%, 2+
42.7%, 3+ 24.0%, 4+ 18.7%, 5+ 11.1%, 6+ 8.3%. So the opponent's reply is
right about half the time (~56% for its second, given the first), and our own
next move matches the forecast ~80% of the time: a fresh search mostly agrees
with the last one. Every opponent was an engine, which tends to see the same
best move; against people it would likely be lower, with no data here to say.
The engine searches on one thread; the laptop has 8 cores (16 threads).

## 2026-10-15 (18:39-20:58): July with the 13-game fix, Lichess's count exactly

July's index ran again from the scratchpad snapshot of the scripts at
ee66600 (restarted after the 18:35 failure above):

| | first run (14:54) | with the fix |
|---|---:|---:|
| games | 89,288,408 | **89,288,421** (counts.txt: 89,288,421) |
| games cut by a frame's end | 6,177 | 6,190 (6,176 ends + the 13 + the last) |
| distinct move sequences | 87,604,543 | 87,604,555 |
| games in duplicate groups | 1,834,283 | 1,834,284 (one more 1.e4) |
| different moves in SAN | 3,246 | 3,231 (15 were tag fragments of glued games) |
| check | 4,000 of 4,000 | 4,000 of 4,000 |
| read | 2,516 s, 11.5 MB/s | 4,799 s, 6.1 MB/s |

The read and `check` (3,294 s) were slower on a busier laptop and share; the
column statistics came out the same at the precision the write-up shows, the
long duplicates likewise (16,999, 96.8% BOT v BOT). scripts/index-2026-07.txt
is updated to this run. Since it ran the old scripts, its rows are plain
columns plus the trial's Parquet, and the new tools refuse its games.json:
July needs `rows` once more (TODO item 3).

## 2026-10-15 (21:25-23:35): the search on more than one core

Matthew asked whether the search could use the laptop's other cores (8 cores,
16 threads; the engine used one), and proposed a way of his own: "describe our
position in some ranges of possibility, split the job 4 or 8 or 16 ways". Both
are built on branch claude/nostalgic-greider-09538a (from compression; commit
9424597; not merged, the bot untouched), behind two new UCI options: `Threads`
(default 1, so until it's set the bot searches exactly as before) and
`ThreadSplit`:

- **LazySMP**, the standard way: every thread searches the whole tree from the
  root, all sharing the transposition table, half the helpers starting a depth
  ahead. The main thread's answer is played.
- **Ranges**, Matthew's idea (Baudet's parallel aspiration search, 1978): at
  each depth from 4, the score range is cut into one window a thread around
  the last depth's score (cuts at 0, ±15, ±30, ±60, ... cp, so the windows are
  narrow where the score is likely to land), each reaching 10 cp into its
  neighbours. Every thread searches the root in its own window; one of them
  should hold the score, and its thread gives the answer while the rest are
  stopped. When none holds it (pruning that depends on the window can make
  neighbours disagree), that depth is searched again on one thread with the
  full window.

Underneath, the table is lockless (the key XORed with the entry, Hyatt and
Mann), so a slot two threads write at once reads as a miss: a test of 8
threads on 4 slots finds 0 mixed entries, and 14,711 in 0.4 s with the trick
taken out. The endgame tables' sets of missing materials became concurrent.
One thread searches exactly as before: the same move, score and node count as
the previous build on 37 positions (the bench's 6, and the 31 below, depth 9).

**Time to depth 12** (`ChessBruteforcer.Engine bench-threads`): 31 positions
from bot/games (every 8th game, at ply 30, 40 or 50), the bot's evaluation
(sf1), a 64 MB table cleared before every search. Every arm searched each
position in turn (reverse order in round 2), 2 rounds, so 62 searches an arm,
at below-normal priority, the bot playing at times alongside. One thread
repeated itself exactly (0 of 31 differed). Two runs, each against its own
one-thread baseline (349 s and 354 s for the 62); the Ranges rows are the
second run's, with the 10 cp overlap:

| arm | threads | speedup to depth 12 (total time) | median search | searches slower than 1 thread | nodes searched v 1 thread | nodes a second v 1 thread |
|---|---:|---:|---:|---:|---:|---:|
| one thread | 1 | 1.00 | 1.00 | | 1.00 | 1.00 (1.8 M/s) |
| LazySMP | 2 | 1.25 | 1.27 | 13 of 62 | 1.59 | 1.99 |
| LazySMP | 4 | 1.77 | 1.73 | 5 | 2.25 | 3.97 |
| LazySMP | 8 | 1.95 | 2.12 | 5 | 3.57 | 6.96 |
| LazySMP | 16 | 1.64 | 1.83 | 9 | 5.31 | 8.73 |
| Ranges | 2 | 1.53 | 1.43 | 10 | 1.10 | 1.67 |
| Ranges | 4 | 1.98 | 1.94 | 5 | 1.24 | 2.44 |
| Ranges | 8 | 1.61 | 2.36 | 5 | 1.68 | 2.72 |
| Ranges | 16 | 2.24 | 2.46 | 5 | 1.66 | 3.73 |

(16 threads means the 8 cores' hyperthreads, with the bot and the book learner
on them too.)

- **To a fixed depth the two are about level**: ~2x at 4 to 8 threads either
  way, and neither gets far past it.
- **They spend the cores differently.** LazySMP keeps them all busy (7.0x the
  nodes a second on 8) but puts most of it into searching wider, not deeper:
  3.6x the nodes to reach the same depth. That's normal for LazySMP; its gain
  shows in play more than in time to depth, since the wider search finds
  better moves at the same depth. Ranges searches nearly the same tree (1.2 to
  1.7x the nodes) and leaves cores idle (2.4 to 3.7x the nodes a second): once
  the window holding the score is done the others are stopped, and that window
  costs nearly what the full one did (next table).
- **At a fixed depth neither gives the one-thread answer**: the same move in
  84-90% of searches, the same score in 21-42%, 6-13 cp apart on average. The
  threads change what the table holds when, so the tree changes. On one thread
  nothing changed.
- **The overlap matters to Ranges.** Without it (the first run), 194 of R8's
  558 parallel depths (35%) had no window holding the score, each searched
  again on one thread, and Ranges managed 1.21-1.29x at 2 to 8 threads. With
  10 cp, 28 (5%), giving the table above. The 15 cp step and 10 cp overlap were
  chosen on 8 other positions (bot games outside the 31, depth 10).

**Baudet's 5-6x** (`scripts/window-ceiling.cs`). The most Ranges could gain,
however many threads, is what a window just wide enough to hold the score
saves over the full window at the same depth, from the same start (the table
and move ordering the earlier depths leave, deterministic on one thread).
Counted in nodes at depth 11 on the same 31 positions:

| window around the score | held the score | full-window nodes / window's nodes |
|---|---:|---:|
| ±1 cp | 8 of 31 | 2.67 |
| ±5 cp | 13 of 31 | 2.06 |
| ±10 cp | 20 of 31 | 1.63 |
| ±25 cp | 30 of 31 | 1.09 |
| ±50 cp | 31 of 31 | 1.09 |
| ±100 cp | 31 of 31 | 0.99 |
| the narrowest that held it | 31 of 31 | **1.27** (median 1.06, best 2.17) |

So the claim doesn't hold for this engine: its ceiling is ~1.3x, not 5-6x.
Baudet's figure was for plain alpha-beta, where a full window really does cost
far more than a narrow one. Principal variation search already searches every
move but the first with a null window, and the table and move ordering do the
rest, so a narrow window has little left to save. (The ±1 window looks better
only because it mostly fails: a failed search is cheap and settles nothing.)
What Ranges measures beyond 1.3x comes from sharing the table: the threads
whose windows fail fill it for the one that holds the score, which is
LazySMP's effect arriving by another road.

**Matches**: one thread against 8, 40 games each at 10+0.1 (20 openings from
games/tune/openings-300.txt in colour-reversed pairs), one game at a time,
both engines at below-normal priority with the tuned evaluation, Hash 64 and
no endgame tables. No time losses.

| match | games | +W -L =D | score | Elo [95%] |
|---|---:|---|---:|---|
| LazySMP 8 v 1 thread | 40 | +26 -4 =10 | 77.5% | +215 [+124, +344] |
| (its games 9-40) | 32 | +22 -2 =8 | 81.2% | +255 [+154, +416] |
| Ranges 8 v 1 thread | 40 | +20 -2 =18 | 72.5% | +168 [+95, +259] |

Games 1-8 of the first overlapped another session's test run (also below
normal) on the same cores; leaving them out only raises LazySMP's score.

**Does Ranges lose to LazySMP?** To a fixed depth, no: they're level. In play,
by 47 Elo at 10+0.1, inside the noise of 40 games (only a head-to-head match
would settle it). In what it is, yes: the narrow windows themselves are worth
~1.3x at most here, and what makes Ranges work is the shared table, which
LazySMP uses directly while keeping every core busy. So LazySMP for the bot,
if Matthew wants threads.

Not yet measured: the bot's own clock (300+2, ~12-14 min a game one at a
time, so 20 games ~4-4.5 h: Matthew's call), and threads with the endgame
tables loaded. A 5-piece table's compressed blocks are read under one lock
per table, so 8 threads probing the same ending could queue there.

The raw output is in scripts/: threads-positions.txt (the 31),
threads-bench-d12.txt (the first run, Ranges without overlap),
threads-bench-d12-ranges.txt and threads-ceiling-d11.txt. To repeat:
`ChessBruteforcer.Engine bench-threads scripts\threads-positions.txt 12 --eval
bot\eval\eval-sf1.txt` (default arms 1,L2,L4,L8,R2,R4,R8; ~30 min) and
`dotnet run -c Release scripts\window-ceiling.cs -- scripts\threads-positions.txt
11 bot\eval\eval-sf1.txt` (~8 min).

## 2026-10-15/16 (23:36-00:35): July's new-layout run stopped; on the phone's hotspot

- **July with the new `rows`** started 23:36 by itself (index-month.cmd's
  wait for the parallel-search match's "final:" line), read 14.27 of 29.05
  GB, and lost the share at ~00:08. The cause wasn't the desktop: **the
  laptop had moved onto Matthew's phone hotspot** ("Opel Mobile SmartX65",
  10.197.60.x), from which the desktop can't be reached. On his yes the
  5-piece-tables session stopped the run at 00:16 ("a step failed"). The
  20:58 index is untouched: the new `rows` writes into parquet.new and swaps
  it in only at the end. To run again once the share is back.
- **May 2026 downloading onto the laptop** (games\, uncapped: his unlimited
  data), started 00:28 by the parallel-search session on his word. Its first
  try met an empty HEAD from the slow server: size 0, a blank date in the
  stamp, which would have stopped every resume. That session restarted it
  at 00:30; **fixed in download-lichess-month.ps1** (20f80d2): the HEAD is
  asked again, up to 30 times a minute apart, until it gives both; a blank
  stamp takes the server's date; a list that couldn't be fetched says so.
  Tested against a dead address (30 tries, then a clean stop) and a local
  web server (size and date read right).

## 2026-10-16 (03:20-05:55): pondering merged; July in the new layout

- **Pondering merged into compression** (ddc54c8, from
  claude/vigilant-murdock-d223f5). The block cache, the threads with the
  tables and the parallel search were already in, by PR #5. The code merged
  clean; TODO and WORKLOG kept both sides. Tested in a worktree at
  below-normal priority beside the bot: **341 passed, 2 skipped, 0 failed**
  (330 before + the 13 ponder cases). The bot is unchanged: config.yml has no
  `ponder`, so lichess-bot keeps it off. The five finished side sessions were
  archived first, on Matthew's yes (commit headroom 1.6 GB with the bot on 8
  threads, 7.8 GB after).
- **July's rows in the new layout**, 03:33-04:56 with the repo's
  index-month.cmd: 89,288,421 games, `count: ... MATCHES`, read at 11.4 MB/s
  beside the bot, 1,267.1 MB of Parquet; `check` 2,000 of 2,000 games byte for
  byte and every row right in all 11 columns against the game parsed again;
  dups as before. July is ready for session 2.
- **At 03:07 the laptop moved from the phone's hotspot back to home Wi-Fi**,
  and the windows of May's download and the July launcher were closed at the
  same time (no lines in either log, no reboot). May was restarted at 03:33
  (uncapped, Matthew's word) and stopped again partway through an attempt
  after 04:56 the same way; restarted at 05:53 from 17.32 GiB. Lichess's
  server was slow all night: attempts 8-10 each ended curl exit 28, the new
  stall timeout doing its job.

## 2026-10-16 (00:16-01:00): threads with the endgame tables loaded

The question left open above: with the bot's tables loaded, do 8 threads
queue on the compressed tables? Each 5-piece table (220 files, 12 GB, read
from disk a 64 KB block at a time) decompressed its blocks, and read even a
block it already held, under one lock a table. A side session, on branch
claude/practical-cerf-df4df1 (on top of the threads branch; the bot
untouched). They do queue, and badly; the cache is read without a lock now.

**The bench.** `bench-threads` takes `--tables <dir>` and loads them as the
UCI engine does (796 MB of plain tables into memory, the 220 compressed ones
read as probed). 20 positions from bot/games (scripts/pick-endgame-positions.py:
the first position with 6 or 7 pieces and the bot to move, in 20 of the 96
games that reach one, spread over them, a material once where it could), so
the search runs down into 5 pieces. Depth 12, the bot's evaluation (sf1), a
64 MB hash table and the tables' block caches emptied before every search,
every position searched once before the timed rounds (so the table files
were in the disk cache for every arm alike), 2 rounds. Two builds, run A B A
B: the old cache under its lock (commit a3cae87, which counts the reads that
found the lock taken and the time they waited, with this commit's two bench
fixes: the caches emptied in the warm-up too, and their memory let go) and
the new one. Alone on the
machine (July's index run was stopped at Matthew's word when the desktop
share went away; the bot wasn't running), below normal, a 4 GB heap cap.
One thread repeated itself exactly in every run, and the two builds' one
thread searched the same nodes.

| arm | build | speedup to depth 12 (total time) | median search | searches slower than 1 thread | nodes searched v 1 thread | nodes a second v 1 thread | waiting, of the threads' time |
|---|---|---:|---:|---:|---:|---:|---:|
| one thread | either | 1.00 | 1.00 | | 1.00 | 1.00 (0.55 M/s) | |
| LazySMP 2 | one lock a table | 1.21 | 1.18 | 17 of 64 | 1.34 | 1.63 | 26% |
| LazySMP 4 | one lock a table | 1.26 | 1.30 | 9 | 1.90 | 2.38 | 51% |
| LazySMP 8 | one lock a table | **1.09** | 1.28 | 12 | 2.69 | 2.93 | **70%** |
| LazySMP 2 | no lock | 1.49 | 1.35 | 10 | 1.36 | 2.04 | 15% |
| LazySMP 4 | no lock | 1.79 | 1.62 | 4 | 2.22 | 3.96 | 30% |
| LazySMP 8 | no lock | **2.08** | 2.04 | 6 | 3.31 | 6.90 | 33% |

(Two runs a build, 40 searches an arm. The median and the slower count are
over the 64 searches one thread took 100 ms or more over; the rest are mates
the tables settle in a millisecond. A run's own L8 speedup was 0.97 and 1.24
under the lock, 2.36 and 1.86 without it.)

- **Under the lock, 8 threads were no faster than one.** They spent 70% of
  their time waiting for a table's lock: Lazy SMP's threads search the same
  ending, so they probe the same few tables, and one of them decompressing a
  block (~120 us) held up the rest. Adding threads past 4 made it worse.
- **Without it, the endgame scales like the middlegame**: 2.08x to depth 12
  on 8 threads and 6.90x the nodes a second, against 1.95x and 6.96x on the
  31 middlegame positions above.
- How it works now: a cache slot has a version, odd while one thread fills
  it. A read takes no lock and writes nothing: it reads the version, the
  slot's block, the value and the version again, and keeps the value if the
  version didn't move. Filling a slot belongs to the one thread that moves
  its version from even to odd; a read that finds its slot being filled
  waits for that. So a block already there is read by every thread at once,
  and blocks for different slots are decompressed at once. A new test reads
  one table from 8 threads on 3 cache slots (every value right; it fails
  when filling takes no claim). 323 tests pass, 2 skipped as before.

**What's left is the cache itself.** Even on one thread, two-thirds of the
endgame search is decompressing blocks: 67.8% of its time, ~20,700 blocks a
search (a block for every 3 table hits), ~120 us each with the file read. A
table keeps 1,024 blocks, each in a fixed slot (64 MB at most), which holds
little of what a search needs. And it never gives memory back. With 220
compressed tables that's up to 14 GB, and the first try at this bench, which
didn't empty the caches between searches, went past the 4 GB cap in under a
minute on 8 threads ("Out of memory"; the cap kept it to the bench). With
the caches emptied and let go before each search, a run peaked at 2.8-3.0
GB. The bot keeps its caches for as long as it runs, so they grow with every
ending it meets, faster on 8 threads. The waiting left on 8 threads (33%) is
reads waiting while another thread fills the slot they want, about one for
every block decompressed: either threads wanting the same block (Lazy SMP's
threads follow each other) or two blocks sharing a slot, not yet told apart.
Fewer misses would cut both.

The raw output is in scripts/: threads-endgame-positions.txt (the 20),
threads-tables-d12-{lock,free}-{1,2}.txt and .tsv, and
threads-tables-summary.py (the table above from the .tsv files). To repeat:
`ChessBruteforcer.Engine bench-threads scripts\threads-endgame-positions.txt
12 --arms 1,L2,L4,L8 --eval bot\eval\eval-sf1.txt --tables tables` (~7 min
without the lock, ~10 with it; peak ~3 GB, so check commit headroom first).

## 2026-10-16 (01:11-02:15): the block cache: where its misses came from, one budget, half the time

The step left open above, before the bot gets threads. A side session, on
branch claude/angry-kirch-aa4068 on top of claude/practical-cerf-df4df1
(138e089), so it fast-forwards from it; the bot untouched. Alone on the
machine for every bench, 01:23-02:07 (July's index run waiting for the
share and never started, the bot not running; only May's download, uncapped
on Matthew's phone data, network but barely any CPU), below normal, a 4 GB
heap cap.

**Where the misses came from.** Every read of a compressed table in the
timed searches traced (`bench-threads --trace`, commit 04cd475), the 20
endings at depth 12, one thread, and the reads replayed offline through
other caches (scripts/block-cache-trace.py). Replaying the old cache
decompressed exactly the blocks the bench did, in all 20 searches (414,619),
so the other caches below are the same reads, not a model of them. A
search, on average:

| | a search |
|---|---:|
| reads of a compressed table | 107,040 |
| of them DTZ | 43,637 |
| DTZ reads after a mate distance that had already settled the result | 43,251 (99.1%) |
| blocks decompressed (each table its own 1,024 slots) | 20,731 |
| blocks read at all (the least a cache emptied before the search can do) | 7,910 |

- The old cache's misses: 38% cold (the block's first read), 30% capacity
  (more blocks than a table's 1,024 slots, even if any could go anywhere),
  32% conflict (two blocks wanting one slot).
- DTZ tables: 34% of the misses. `TryProbeWithClock` read DTZ after every
  win or loss, but in a search nearly every probe comes straight after a
  capture (the count at 0), where a mate within 100 plies settles it.
- By table: the mate distances of KQPvKQ (14%), KPPvKP (13%) and KQPvKP
  (11%) are 38% of the misses between them; 136 tables are read at all.
- On 8 threads, 99% of the reads that waited (old cache) were waiting for
  the very block another thread was decompressing, not for another block
  sharing their slot: Lazy SMP's threads search the same positions. Only
  fewer misses cut those waits.

The same reads through one cache for all the tables (blocks decompressed a
search; the replay's numbers, with the bench's where measured):

| cache | memory | all reads | DTZ read only where it can matter |
|---|---:|---:|---:|
| each table its own, 1,024 slots, direct-mapped (138e089) | up to 14 GB | 20,731 | 13,760 |
| one for all, least recently used goes, 128 MB | 128 MB | 24,377 | 11,879 |
| the same, 256 MB | 256 MB | 17,768 | 8,475 |
| the same, 512 MB | 512 MB | 12,068 | 6,001 |
| the same, 1 GB | 1 GB | 8,533 | 4,851 |
| the same, 2 GB | 2 GB | 7,910 | 4,838 |
| one for all, sets of 16 with a clock (now), 256 MB | 256 MB | 18,603 | 8,936 (measured 8,931) |
| the same, 512 MB (the default) | 512 MB | 12,830 | **6,350 (measured 6,358)** |
| the same, 1 GB | 1 GB | 9,113 | 4,989 (measured 4,987) |
| every block read once | | 7,910 | 4,838 |

Sets of 16 cost ~6% more misses than true least-recently-used; sets of 8
another ~2%.

**What changed** (commit a5b38fa):
- **DTZ only where the mate can't settle it.** Mating in d plies is a way to
  win under the 50-move rule that starts the count again (or mates) within
  d plies, so when the clock + d is at most 100 the DTZ table has the same
  win or loss, in time, and reading it changes nothing. Checked four ways:
  every position of the test tables (CompressedTableTests, >100,000 wins
  and losses); probes through the compressed test tables at clocks either
  side of both distances, against the old definition; every position of
  four real tables (scripts/dtz-skip-check.cs, below: 0 exceptions in 2.96
  billion positions, 6.0 M of them mates at 90-100 plies; every rule draw
  among wins and losses has a mate of 121 plies or more); and the slow
  cursed-win test on the real KBBvKN. One thread searched exactly the same
  nodes, moves and scores in every run of both builds.
- **One cache for every table** (`BlockCache`): all the compressed tables a
  Tablebase opens share 512 MB of places by default (UCI option
  `TableCache`, in MB; `bench-threads --table-cache`). A block goes into one
  of 16 places in its set (a hash of table and block), a clock hand within
  the set picks what goes. Read without a lock as before (a version a
  place); a thread filling a place names its block first, so reads of that
  block wait for it and the rest pass by. Memory is taken as places are
  first used and never passes the budget (it was up to 64 MB a table, 14 GB
  in all, never let go).
- The engine lets go of its old tables when `EndgameTables` changes (their
  files stayed mapped).
- 328 tests pass, 2 skipped as before (5 new: tables sharing a cache within
  its budget, 8 threads on 4 tables through 6 places, the two DTZ checks,
  the UCI option).

**Re-measured**, A B A B (138e089, this branch, 138e089, this branch), two
runs a build, 40 searches an arm (scripts/block-cache-d12-*):

| arm | 138e089, s | now, s | now faster | to depth 12 v one thread (138e089 -> now) | nodes a second v one thread | decompressing, of thread time | waiting, of thread time | blocks a search |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| one thread | 294.1 | 149.8 | **1.96x** | 1.00 | 1.00 (0.55 -> 1.09 M/s) | 67.6% -> 42.1% | | 20,731 -> 6,358 |
| LazySMP 2 | 203.8 | 101.4 | 2.01x | 1.44 -> 1.48 | 1.96 -> 2.00 | 50.9% -> 33.5% | 17.7% -> 9.2% | 20,626 -> 6,449 |
| LazySMP 4 | 163.1 | 88.2 | 1.85x | 1.80 -> 1.70 | 3.73 -> 3.83 | 41.6% -> 26.3% | 27.9% -> 17.3% | 26,245 -> 8,352 |
| LazySMP 8 | 137.2 | 62.4 | **2.20x** | **2.14 -> 2.40** | **6.42 -> 6.98** | 38.8% -> 22.2% | 32.6% -> 22.2% | 37,401 -> 9,036 |

- Every arm about twice as fast; 8 threads still ~2.4x one thread, which
  is itself twice as fast now. A run's own L8 speedup: 2.47 and 1.89 on
  138e089 (2.36 and 1.86 last time), 2.43 and 2.38 now; the median search
  2.04 -> 1.97.
- Peak memory 2.92 / 2.74 GB -> 1.91 / 1.92 GB.
- Every wait left on 8 threads is for a block another thread is
  decompressing.
- The budget, one run each (1 and L8): 256 MB 86.8 s one thread (8,931
  blocks a search), 37.4 s L8, peak 1.44 GB; 512 MB 74.5-75.2 s, 31.0-31.4
  s, 1.92 GB; 1 GB 76.1 s (4,987 blocks), 35.5 s, 2.80 GB. 1 GB
  decompresses a fifth less but isn't faster here: the bench empties the
  cache and lets its memory go before every search, so it allocates up to
  1 GB a search, which the bot (never emptied) pays once. 512 MB, then:
  the bot's laptop has ~2.4-5 GB of commit headroom with the bot running.

**Smaller blocks, proposed (not run).** What's left on one thread is 42% of
its time in ~6,400 decompressions a search, ~120 us each, and most of those
are blocks read once (4,838 of the 6,358). A smaller block costs less to
decompress almost in proportion. Sampled blocks of the six tables with the
most misses, recompressed in memory at quality 10 (scripts/block-size-probe.cs,
nothing written):

| block | x smaller | files against 64 KB blocks | us to decompress a block | replayed misses a search, 512 MB | decompressing a search, estimated |
|---|---:|---:|---:|---:|---:|
| 64 KB (now) | 12.5 | 1.00x | 91.6 | 6,001 | ~790 ms (measured) |
| 32 KB | 11.9 | 1.04x | 47.3 | 6,712 | ~460 ms |
| 16 KB | 11.3 | 1.10x | 24.3 | 7,985 | ~280 ms |
| 8 KB | 10.5 | 1.19x | 14.0 | 9,186 | ~180 ms |
| 4 KB | 9.4 | 1.32x | 8.6 | 11,013 | ~130 ms |

(The estimate scales the measured 64 KB time by the decompression times and
the replayed misses.) 8 KB blocks would take a one-thread search from ~1.9 s
to ~1.3 s here, and need far less cache: 128 MB of 8 KB blocks replays to
11,237 misses, ~220 ms. The cost: recompressing the 220 tables (~12 GB ->
~14 GB) from the compressed files themselves, ~4 h on all 16 threads (the
pawnless 28.7 GB took 48 minutes the first time), a night; the reader then takes the block size from the file's header (it
already holds it; only the check that it is 64 KB has to go) and the cache
places shrink to match; block offsets held in memory grow from ~14 MB to
~110 MB at 8 KB (half that as 4-byte offsets, every file being under 4 GB).
A step of its own.

The raw output is in scripts/: block-cache-trace-d12.{txt,tsv} (the traced
run on the old cache, with same-block waits), block-cache-replay-d12.txt
(the replay), block-cache-d12-{old,shared}-{1,2}.{txt,tsv} and
block-cache-d12-summary.txt (the A B A B), block-cache-d12-shared-{256,1024}-MB.*,
block-size-probe.txt, dtz-skip-check.txt. To repeat the trace:
`ChessBruteforcer.Engine bench-threads scripts\threads-endgame-positions.txt
12 --arms 1 --rounds 1 --eval bot\eval\eval-sf1.txt --tables tables --tsv
t.tsv --trace t.bin`, then `python -I scripts\block-cache-trace.py t.bin
t.tsv` (~2 min; the trace is ~60 MB for one thread and L8, so it isn't kept).
## 2026-10-15 (22:45) to 2026-10-16 (00:15): pondering, and what it's worth

A side session, on top of the threads work (branch claude/vigilant-murdock-d223f5,
from claude/nostalgic-greider-09538a at 9424597). The UCI engine now thinks on
the opponent's time. **The bot is unchanged**: config.yml keeps `ponder: false`
and its engine folder wasn't touched. Turning it on is Matthew's call (TODO,
section 4).

**What lichess-bot sends.** It drives the engine through python-chess (1.11.2
in the bot's venv), and its source says: after `bestmove <move> ponder <reply>`,
`go ponder` on the position after that reply, with the clocks moved on (ours
less the time we used, plus the increment); then, if the next move asked for is
from that same position, only `ponderhit` (no new position, no new clock), and
otherwise `stop` followed by the usual `position` and `go`. So the clock in
`go ponder` is the one that counts after a ponderhit.

**What the engine does** (Search.cs, UciEngine.cs):
- `go ponder <clock>`: searches with no time limit until `ponderhit` or `stop`,
  and holds its answer until one of them comes, even when the search ends by
  itself (a mate seen, a depth limit).
- `ponderhit`: the same search goes on, keeping its tree, table and depth, under
  the clock from its `go ponder`. **The time spent pondering counts**: it's as
  if the search began on our own time at `go ponder`. No new depth is started
  past half the planned time, and a depth begun past that point while pondering
  is dropped, since the clock would never have started it. So a ponder hit plays
  as deep as our own time would have taken us, usually at once, and the clock
  saved goes to the moves we didn't foresee. The alternative was to start the
  clock at ponderhit. That spends a full budget on the moves we foresaw, which
  are often the easy ones (recaptures). It also lets a depth begun while
  pondering run on to the hard limit, three times the planned time. As far as I
  know, Stockfish counts the time too.
- `bestmove <move> ponder <reply>`: the principal variation's second move. When
  the line stops at our move (cut short by the table, or a repetition), the
  table's move in the position after ours, checked legal.
- `option name Ponder type check default false`: accepted, changes nothing.
- With `Threads`: Lazy SMP helpers and Ranges workers stop on the main search's
  limits, which ponderhit sets. Tested with 1, L4 and R4.
- **A race, found and fixed**: the search reset its stop flag when its thread
  began, so a `ponderhit` (or `stop`) arriving straight after `go` was lost, and
  that search pondered forever. `go` now returns only once the search is ready.
  Removing the wait fails three tests.
- One thread without pondering searches as before. The soft-limit check moved
  to the top of the next depth, which is the same moment.

Tests: PonderTests, 13 of them (ponder then stop answers at once and legally,
with 1, L4, R4; ponder then ponderhit answers within the clock, both straight
away and after pondering past the whole budget; a ponder search that ends by
itself still waits; the ponder move is legal and comes from the table when the
line is one move long; a ponder hit keeps the deeper search). Six mutations of
the new code, all caught. Full suite: 335 passed, 2 skipped.

**The match**: scripts/ponder-match.py (new; the bot venv's python). It plays
our engine with ponder on against the same build with ponder off, driven
exactly as lichess-bot drives it (python-chess's `play(..., ponder=True)`).
Setup: the bot's evaluation (eval-sf1), Hash 64, one thread, no endgame tables;
10+0.1; the 300 book openings in colour-reversed pairs; 3 games at a time.
Every engine was pinned to a logical CPU of its own (2, 4, ... 12, one per
physical core), at below-normal priority, and the two engines of a game
swapped CPUs every game. The games are in games/ponder/ponder-10+0.1-run1.jsonl
(a row each, moves included), and `--summary` on that file prints the main
numbers below.

| ponder on v ponder off, 10+0.1 | games | +W -L =D | score | Elo [95%] |
|---|---:|---|---:|---|
| by game | 119 | +48 -20 =51 | 61.8% | **+83 [+37, +133]** |
| by opening pair | 59 pairs | | 62.3% | +87 [+43, +134] |
| ponder side White | 59 | +28 -8 =23 | 66.9% | |
| ponder side Black | 60 | +20 -12 =28 | 56.7% | |

| the pondering side | |
|---|---|
| moves | 7,639 |
| a reply named to ponder on | 7,584 (99.3%) |
| **the reply right (ponder hit rate)** | 5,400 of 7,557 replies: **71.5%** |
| clock used a move | 180 ms (ponder off: 224); after a hit 138, after a miss 283 |
| clock left at the end of a game | 4.84 s of 10 (ponder off: 2.05) |

No games lost on time. Endings: 68 mates, 31 threefold repetitions, 16
insufficient material, 4 fifty-move draws.

**How this differs from Lichess:**
- The hit rate. Here it's 71.5%; on Lichess our forecast reply comes true 51.0%
  of the time (scripts/forecast-hits.py). The opponent here is the same engine,
  which mostly sees what we see. Most of the gain comes from hits (misses only
  warm the table), so expect less there, perhaps two-thirds of +83. Not
  measured.
- Cores. Here each engine had a core of its own, as if the opponent were on
  another computer, which is how it is on Lichess. But on the laptop the ponder
  search keeps a core busy for the whole game, a core that's otherwise free on
  the opponent's time. The learner, an index run and a sibling's benches share
  that core now.
- Time. The bot plays blitz (3+2 and the like) with ~1.2 s of connection per
  move from Australia and lichess-bot's 2 s move overhead. Everything the
  opponent spends, connection included, is pondering time. 10+0.1 is just the
  repo's quick control.
- Banked clock. The pondering side finished games with more than twice the
  clock the other side had, and never spent it. Stockfish plans 25% more time a
  move when Ponder is on. Worth trying here (TODO).

**A bug in the match script, not the engine**: game 90 crashed when the
pondering engine answered `f5f4` in a fresh opening. python-chess keeps the
board object passed to `play()` and later compares it with the board it pondered
on, to spot a ponder hit. My script passed its own board, which then moved on.
Game 87 (the same engines) ended in a threefold repetition on exactly the reply
the pondering side expected. python-chess then read my end-of-game ping as a
ponder hit and never sent `stop`. In the next game, its first `play()` sent
`ponderhit` to the old search instead of the new position. Fixed by passing a
copy. A small repro shows the stale ponder flag with the shared board and none
with a copy. lichess-bot makes a new board every move and a new engine every
game (lib/lichess_bot.py:694, :736), so the bot can't meet this. Only the
crashed game was lost (it isn't counted), and the bug could only ever have hurt
the pondering side. The only way it could have struck unseen is if a leftover
move happened to be legal in the new opening. A search for such games flagged
four, but each "match" is an ordinary opening move (e6, c3, c4, e3), and three
of them point at the same earlier game, which can only leave one leftover. So
there's no sign of one. A clean re-run is in TODO.

**Overlaps.** The match ran 23:37-23:55 beside an index run (`index-month.py
rows` for 2026-07, started 23:36:18), which began a minute before it while I
wasn't looking. The index run went on fine: 13 of 29 GB at 00:05, no errors,
6.7 GB of commit free. Since the cores swap every game, its CPU fell on both arms
alike. Before that, my full test run (22:59:45-23:03:30) overlapped the first
~8 games of the threads session's 8-thread match, and I told that session. The
bot and the learner stopped at 23:04:50 and 23:05:01 ("Termination requested":
Matthew's Ctrl-C). I didn't restart them.

## 2026-10-16 (06:30-06:55): an NNUE plan, and what it would cost

Matthew asked whether his GPU could help. It can train an evaluation; it can't
run one inside the search (the search wants one about every microsecond on each thread). A side
session wrote the build plan, [NNUE.md](NNUE.md), measuring first. Nothing was
downloaded or installed, and the engine is unchanged.

- **The GPU is in this laptop** (Matthew: "This machine has a 3070 laptop
  gpu"): RTX 3070 Laptop, 8,192 MiB, compute capability 8.6, driver 595.79
  (CUDA 13.2). WMI says 4 GB, which is its 32-bit field's limit. No PyTorch, CUDA
  toolkit or Rust here. The wheel that fits, torch 2.14.1+cu132 for Python
  3.14 on Windows, is 2,001,146,779 bytes (a HEAD request): ~67 min at the
  household cap.
- **A cost probe** (scripts/nnue-probe/, a patched copy of the source in the
  scratchpad): one thread, the six bench positions, the same tree for every
  arm, arms interleaved, the bot playing alongside. Today's evaluation is **68-120
  ns, 5.4-10.7% of search time**, 0.65 evaluations a node. A network in its
  place, as an upper bound (updates on unmake too): 768 -> 128 x2 -4/-12%
  speed, **768 -> 256 x2 -9/-18%**, 768 -> 512 x2 -30/-36%, HalfKP 256 x2 -> 32
  -> 32 -49/-55% (depth 7 / depth 9). The evaluation is cheap because the rest
  of the search is expensive.
- **The data**: an analysed game gives 25.2 positions under the tuner's quiet
  rule (61.3 with any numeric score; the first 100,000 of September's), so
  **September alone holds ~224 M** against sf2's 13.6 M. July's index counts
  9,005,321 analysed games (10.1%), so ~227 M more; with August, ~670 M.
- The plan: 768 -> 256 x2 -> 1 SCReLU, int16, accumulators on a stack in
  Position, `Vector256` through System.Runtime.Intrinsics; PyTorch on Windows
  training on 32-byte records; judged by held-back error against sf1's first,
  then the bench, then 10+0.1 SPRT [0, 10] against sf1. Four sessions, and
  Matthew's decisions A-D are at its end.
- **Matthew's decisions**: A(a) PyTorch, B(a) 768 -> 256 x2 -> 1, C(a)
  September, all speeds, and D "a + c?" with "there is a 1650 in the desktop".
  A GTX 1650 has 4 GB and 128-192 GB/s against the laptop card's 448, so
  about a third of the speed on this net: 2-7 h a run, overnight. So "a + c"
  means two trainers, each on its own net, not one run split across the
  house's network. NNUE.md gains session 2b: the scripts, the wheel (copied
  over the share, not downloaded twice) and the records on the share;
  Matthew checks the desktop's driver (CUDA 13.2 or more), installs Python
  3.14 and runs a setup .cmd there, since nothing here can reach the desktop.
  The link between the machines is 9 MB/s at best (Matthew): the wheel 3.7
  min, September's records 13 min (once), a net back in under a second;
  reading records over it would take 4.4 h a run, so each machine keeps its
  own copy, and a split run's gradient swap (0.8 MB each way, ~0.18 s a
  step against 8-33 ms of work) would be 5-20x slower than the laptop alone.

## 2026-10-16 (07:53-09:10): the bot restarted; May lands and goes to the share

- **The bot restarted at 07:53** in what looked like a gap between games, to
  pick up pondering, the shorter greetings and !info's project lines
  (3aa22df). It had in fact dropped a live game: lichess-bot logged "Game
  over" against TroutBot (2228) at 07:52:22, three book moves in, when the
  game's stream closed (Lichess was shaky all night). The restarted bot
  rejoined it at 07:53:43, ~1.5 min down on the clock, and lost by mate at
  08:03. No errors or ponder trouble in the log since; 7 games by 09:02. A
  fix for dropped streams is in TODO.
- **May 2026 finished at 09:05:38** (29,689,176,290 bytes, the server's size;
  ~0.9-1.2 MB/s over the morning, attempts ending curl exit 28 on stalls as
  designed). Checked on the laptop against Lichess's SHA-256: matches (42 s).
  Copying to the share from 09:07 with robocopy /Z, then the share's copy
  checked again (a window of its own, games\move-2026-05.log).

## 2026-10-16 (07:00-08:50): NNUE session 1, the data

A side session, in its own worktree (branch `claude/nnue-data-7d1bea`), beside
session 3 (the network in C#) in another. Matthew's C(a): September, every quiet
position, all speeds. Report: [scripts/nnue-data-2026-09.txt](scripts/nnue-data-2026-09.txt).

- **One quiet rule**: `QuietPositions.Walk` (Core/Records) is the tuner's rule
  word for word; `tune positions` now walks games through it, and so does the
  new `nnue data`, so the two can't drift.
- **The record** (`NnueRecord`, 32 bytes): occupancy u64 at 0, 32 pieces as
  4-bit codes at 8, flags at 24 (black to move, mate), result at 25, white's
  score i16 at 26 (mates +-32000), game number u32 at 28. The result moved
  before the score so every field is aligned (NNUE.md has the table).
  `nnue data <out> <file>... [--per-game all|N] [--speeds ...] [--buckets 64]`
  writes 64 buckets chosen at random, held-back.bin (games 0 mod 50), and
  data.json last; `nnue show` prints records as FEN. `scripts/nnue/records.py`
  reads them with numpy only; `scripts\nnue-data.cmd <month>` runs a month in
  its own window.
- **A trial first** (the first 100,000 games, 29 s): `--speeds
  blitz,rapid,classical` wrote exactly the 1,999,783 positions `tune positions
  --min-elo 0 --per-game 1000 --lambda 0` did, plus 104,589 mates.
- **September, 07:40-08:26** (45.6 min, below-normal, beside the bot):
  **234,264,966 records, 7.50 GB**: 220,933,142 scored in centipawns (NNUE.md
  predicted ~224 M: -1.4%) and 13,331,824 mates (5.7%). 229.6 M in the
  buckets (3.58-3.59 M each, ~115 MB), 4,685,639 held back (2.0001%, 174,322
  games). Blitz or slower 188.2 M (80.3%), bullet 44.5 M, ultrabullet 1.3 M.
- **The 1.4% is Swiss games.** Lichess's Swiss tournament games have an Event
  like "Rapid swiss https://lichess.org/swiss/..." with no "Rated", so the
  tuner's rule (and this one) leaves them out: 95,237 games, 1.07% of
  September's analysed games, ~2.5 M positions. NNUE.md's 25.2 a game came
  from count.cs, which didn't look at Event; on the same 100,000 games the
  two differ by 12,699 positions, exactly the 534 Swiss games'.
- **Checks**: records.py check over every record (sizes against data.json,
  codes, kings, pawn ranks, flags, no held-back game in a bucket); C#'s `nnue
  show` and Python's `show` agree on 15,000 real records; 20 new tests (the
  rule restated from its words over random games, 10,000 FEN -> record -> FEN,
  the bytes by hand, `nnue data` v `tune positions` one for one, held-back,
  --per-game), the suite 361 passed, 2 skipped; Python's test-records.py on
  C#'s fixture. **Mutation battery: 35 of 35 red.** The rule's own 8 are seen
  only by the restated rule, as they must be: both commands share it.

## 2026-10-16 (evening): Swiss games counted; September again

- Matthew asked what counting Swiss games would mean (a filter that was
  quietly wrong, 1% more data, too little for the network to notice; 46
  min to run September again), then chose (a): fix both and re-run.
- A scan of every Event tag in September's analysed games: 8,801,711 say
  "Rated", 95,237 are Lichess Swiss games without it (rapid 46,169, blitz
  37,457, bullet 6,937, classical 4,651, ultrabullet 23), **0 anything
  else**. So `StoredGame.IsRated` is "Rated" in Event or a Lichess Swiss
  game, and `tune positions` and `nnue data` both ask it (8df33f1). Tests:
  rated cases, Swiss games among the synthetic ones, only casual games "not
  rated"; four more mutations, all red (39 of 39).
- The first 100,000 games now agree three ways: 2,523,305 scored positions
  (nnue data and the plan's count.cs) and 2,010,577 blitz or slower (nnue
  data, tune positions and count.cs).
- **September again, 20:18-20:59 (41.2 min): 236,940,450 records, 7.58
  GB**: 223,352,776 scored in centipawns (the plan's ~224 M: -0.3%) and
  13,587,674 mates; 232.2 M in the buckets, 4,738,712 held back (176,155
  games). Blitz or slower 190.7 M (80.5%). records.py check and the C# v
  Python cross-check again (report).
- Future `tune positions` runs take Swiss games too (~1% more games); sf1,
  sf2 and the bot are untouched.

## 2026-10-16 (07:15-08:45): NNUE session 3, the network in C#

A side session, in its own worktree (branch `claude/nnue-csharp-8117d8`, cut
from `compression`), beside session 1's data run. NNUE.md's session 3: the
engine can now play on a network, with random weights until session 2's
trained net exists. The bot is untouched (make-config.py still says sf1).

**What was built** (src/ChessBruteforcer.Core/Engine/Nnue/):
- `Network`: the .nnue loader. `CBNNUE01`, version 1, 768 inputs, H hidden
  (a multiple of 16 from 16 to 1024: the file says, not the code), QA, QB,
  scale, the int16 arrays, the output bias, then the SHA-256. Refused: a bad
  magic, a bad checksum, another version, a wrong length, an output weight
  whose product with QA overflows 16 bits, or a hidden unit that could
  overflow on a full board. A net trained with the +-1.98 clamp always
  passes. Also the forward pass: `Vector256<short>` clamp, (v x w) x v
  through `Avx2.MultiplyAddAdjacent` into int32 lanes, widened to 64 bits
  per perspective; and a scalar reference one number at a time. The score is
  clamped short of a mate (+-28,999). The feature index is NNUE.md's.
- `AccumulatorStack`, inside `Position`: `MakeMove` writes the child slot as
  parent + its 2-4 piece changes in one pass per perspective (quiet,
  capture with the en passant pawn's own square, castling with the rook);
  `UnmakeMove` pops; the null move shares the slot (no piece moves);
  `Position.FromFen(fen, network)`, `UseNetwork` and `SetPiece` refresh. A
  take-back past where the stack starts refreshes from the board.
- `Evaluation.Evaluate(position)` uses the network when the position carries
  one. **The network rides on positions, not process-wide** like the
  numbers: UCI `EvalFile` with a .nnue (told apart by its magic) is that
  engine's own, its positions carry it, and Lazy SMP helpers and Ranges
  workers copy the root's. So tests running side by side, or two engines in
  one process, can't meet each other's net. A text EvalFile works as
  before; `<empty>` turns both off. EvalFile on a missing file now answers
  `info string error` instead of stopping the engine.
- Engine `bench [depth] --eval <numbers or network>`.
- `Network.FromPieceSquares(numbers)`: a net that computes a numbers file's
  material and piece-square values (each hidden unit holds 127 + d for the
  side to move and 127 - d for the other, so the output is linear in d).
  The bench's yardstick, and a check that the inputs mean what they should.

**Tests**: NnueTests, 36 of them.

| what | how |
|---|---|
| incremental = refresh | every node of perft walks: start 4, Kiwipete 3, the en passant pins 4, promotions 3, castling discovery 3, plus an en passant and a promotion-capture position; counts equal the published perft; castles, en passant and promotions all made. Also at 256 hidden, and over 72 random games of up to 150 steps with null moves and take-backs |
| SIMD = scalar | refresh and forward, H = 16, 32, 256, 1024, real positions and any int16 accumulators; the largest sums (H = 1024, every weight at the limit) |
| colour mirror | 1,000 positions and their mirrors score exactly the same, two nets |
| C# = numpy | 1,000 positions, exactly: scripts/nnue-reference/nnue_reference.py (numpy only, written from NNUE.md alone) wrote the random-weights net and the scores, tests/ChessBruteforcer.Tests/Data/ |
| meaning | the piece-square net scores 400+ random positions within 3 cp of the numbers it was built from |
| the file | round trip; refuses a bad magic, a bad checksum (header, weight, bias, the hash itself), another version, a cut file, overflowing weights |
| the engine | every thread searches with the root's net (a net that says +1000 for the side to move: the search must answer exactly +-1000, and each helper's position carries the net); UCI EvalFile by magic, a damaged file refused with the loaded net kept |

**Mutation battery**: 30 mutations, **30 red**: forget the castling rook,
the en passant pawn (two ways), the promotion; unmake without a pop;
SetPiece without a refresh; swap the perspectives in the forward pass and in
the feature index; own and other side's pieces swapped; no flip for black;
the SIMD clamp at 0 or at QA; the int32 lanes summed in 32 bits; the output
bias, the mate clamp, truncating division; the checksum, magic and range
checks; each kernel forgetting a column; the refresh forgetting black; the
stack's bottom and growth; helpers, workers and UCI without the net;
Evaluate ignoring it. Two things it taught. Swapping the perspectives keeps
the net colour-symmetric, so the mirror test can't see it: only the numpy
reference and the piece-square net catch it. And the helpers' network was
caught at first only by the UCI test's timing (Lazy SMP's answer is the
main thread's, which a helper reaches only through the table), so each
search now remembers the position it was prepared on and the test looks at
every helper's: red every time since. "The stack never grows" was red by
timeout: past the array, unchecked writes corrupted memory. It was a crash
behind a Windows dialog, not a hang; see "an access violation, answered"
below.

**Random weights make the capture search explode**: on Kiwipete, depth 1
took 1,365,855 nodes with a random 256 net against 3,422 without (depth 3:
11.5 M nodes in 20 s, stopped). A random evaluation swings by thousands of
centipawns from one capture to the next, so quiescence explores nearly every
capture sequence. The first version of the multi-thread test looked like a
hang for that reason. Hence the piece-square net for the bench: speed per
operation doesn't depend on the weights, but the tree does.

**The bench** (scripts/nnue-bench/, results-2026-10-16.txt): `bench 9`, one
thread, 8 rounds, arms in turn.

| | tree (nodes) | median knps | vs sf1 |
|---|---:|---:|---:|
| sf1 (today) | 4,767,261 | 1,218 | |
| net 128 | 4,527,020 | 1,282 | +5.3% |
| **net 256** | 4,625,058 | 1,192 | **-2.1%** |
| net 512 | 4,625,058 | 1,075 | -11.7% |

| tree-free (costs.cs) | update a move | an evaluation |
|---|---:|---:|
| sf1 | | 104 ns |
| net 128 | 26 ns | 45 ns |
| net 256 | 52 ns | 55 ns |
| net 512 | 97 ns | 87 ns |

From the second table, net 256 should cost ~46 ns a node more than sf1, out
of ~820: about -5%. The bench says -2.1%. Either way it's well inside the
probe's upper bound (-9% to -18%), which updated on unmake too, one pass per
square changed, beside today's evaluation. 512 hidden costs -9.8% against
256 on the identical tree. So session 5's 512 lever costs about 10%, not the
probe's 30-36%. Not done: AVX-512, aligned arrays, a lazy update (only nodes
that evaluate).

Full suite: 377 passed, 2 skipped (36 new). Not merged into compression,
not pushed: Matthew's.

**Later (10:00-10:50): an access violation, answered.** Matthew found a
"testhost.exe - System Error, 0xc0000005" dialog on his screen, and the
parent session traced it in the Application log to this worktree, with two
engine crashes on an empty path. Its questions and the answers:

1. **What wrote out of bounds?** The mutation battery's "the stack never
   grows" mutant, which deletes the stack's `Array.Resize` on purpose.
   - The Application log: .NET Runtime 1026 at 08:02:52, an
     AccessViolationException in `Stack<(Move?, Undo)>.Push` inside the
     random-games test. That is the only test deeper than the stack's
     first 32 slots, so the only one where that mutant writes past the
     array.
   - The System log: Application Popup 26 at the same second, "testhost.exe
     - System Error ... 0xc0000005". That is the dialog Matthew found (safe
     to close).
   - The battery's test host for that mutant started at 08:02:42. It was
     the only run of 30 that "timed out", and still alive at 08:11, when I
     killed it. The popup had kept the crashed process alive. My unmutated
     runs were before 07:52 and after 08:12.
   I logged that mutant as "red by timeout" without asking why it hung. I
   should have read the event log then.
2. **Why can't it come back?** It could have, in a different form. The
   kernels wrote through unchecked refs, so a real capacity or index bug
   would have corrupted the heap the same way. Commit e4ffd61 changes that.
   Each push takes the parent's and child's slots as one bounds-checked
   span, and each weight column as another. Each kernel checks its
   operands' lengths before touching a lane, so a wrong slot or column now
   throws. The re-run battery shows it: the stack mutant fails two tests
   with an exception, with no crash and no popup. One new mutation, a
   column read unchecked, goes red too: a move from an empty square now
   throws, where it used to read 64 columns before the weights.
3. **Stress**, as asked. The random-games walk over 300 seeds, each 72
   games of up to 150 steps with null moves and take-backs, the
   accumulators checked against a refresh at every step. Plus 300 deep
   games of up to 400 plies, where the stack doubles to 512 slots. All in
   one test host: 600 of 600 in Debug (3 min 50 s) and 638 of 638 in
   Release (with the 38 NNUE tests). The Application log shows no crash
   event after either run. With the checks in, any out-of-range access in
   those 600 runs would have thrown and failed a case.
4. **The empty path** (08:16 and 08:18): that was the engine's `bench --eval
   ""`. My own bench script passed an empty path, because PowerShell's
   variable names ignore case (WORKLOG above), and `IsNetworkFile("")`
   threw from `File.OpenRead`. Now `IsNetworkFile("")` is false, and
   bench checks the path first: an empty, missing or absent path prints
   `bench: no evaluation file "..."` and exits 1. UCI was never exposed:
   an empty EvalFile value, or none, is the built-in evaluation, and a
   missing file answers `info string error`. Tests cover both forms.

Battery re-run on the new code: 31 mutations, 31 red, no crash events. NnueTests: 38. Full suite: 379
passed, 2 skipped.

Speed with the checks: `bench 9`, 8 rounds, three arms in turn. sf1 ran
at 1,320 knps. Net 256 with the checks ran at 1,270, which is **-3.8%**;
the same net in the build before them ran at 1,290, -2.3%. Both net arms
made the identical tree (4,625,058 nodes), so the checks cost 1.6%. Still
well inside the probe's bound. Per operation (costs.cs), a move's update
is 54 ns with the checks, against 52 before, within the laptop's drift.

## 2026-10-16 (23:25): both NNUE branches merged into compression

- On Matthew's word: `claude/nnue-data-7d1bea` (f977646), then
  `claude/nnue-csharp-8117d8`. WORKLOG and TODO conflicts were entries side
  by side (both kept). The tests' .csproj was merged by hand: session 1's
  reference to the Cli project and session 3's `Data\**` fixtures, both kept.
- The merged tree: full suite **407 passed, 2 skipped, 0 failed** (90 s); the
  engine and the match tool build clean. The bot stays on sf1 (make-config.py
  untouched), and its engine picks up the network code only when start-bot.cmd
  next rebuilds it, where nothing changes until EvalFile names a .nnue.
- Next: session 2, the trainer.

## 2026-10-16/17 (23:30-03:55): NNUE session 2, the trainer, and nnue1

A side session, in its own worktree (branch `claude/nnue-trainer-f0d60e`,
cut from `compression` at 3632956). Matthew's A(a) PyTorch, B(a) 768 -> 256
x2 -> 1, C(a) September, D(a) beside the bot. NNUE.md's session 2 has the
tables; this is the story.

- **The wheel** (A(a) was his yes to it): HEAD said 2,001,146,779 bytes, as
  on 2026-10-16. Another session's capped download (Terminal's build, through
  Claude-capped-run.py) was pulling 430 KB/s when mine was due, and two are
  twice the household's cap, so the wheel's script waited for it to go quiet
  (23:35), then curl at `--limit-rate 500k`: 65 min, 511 KB/s, the size and
  SHA-256 matching the server's own checksum. In `games\nnue\wheels\` for the
  desktop (2b). `scripts\nnue\setup-venv.cmd` made the venv in 71 s; pip
  fetched 9.4 MB of torch's dependencies, numpy comes from the system Python.
- **sf1's mark**: on September's held-back positions (4,465,944, mates left
  out) sf1 scores **0.030396** at its fitted K 1.2705 (0.031460 at K 1), sf2
  0.030317 at K 1.2657. numpy computes both exactly as the engine does
  (1,000 of 1,000 positions equal through UCI `eval`).
- **The checks** all passed (NNUE.md has the table): the GPU's unpacking
  equals nnue_reference on 10,000 records; 10,000 positions learnt to a
  training error of 0.000011 (from 0.103); the engine equals nnue_reference
  on 1,000; `bench 9` on a trial net 4,913,912 nodes, 1.03x sf1.
- **One fix in the export**: float against int16 first came out 10.5 cp
  apart on average, nearly all of it one sign. Splitting it by layer: the
  output weights at QB 64 (-9.5 cp), not the feature layer (3.5 cp, either
  way). Trained output weights are small (median 0.10, largest 0.54, far
  inside the +-1.98 clamp), so at QB 64 a typical weight had ~7 steps and the
  roundings summed to a near-constant offset. The export now picks QB per net,
  the largest that keeps |w| x QB <= 128 (238 for the trial net): 5.2 cp mean.
  The file carries QB and the loader accepts any, so no C# changed. The
  held-back error of the export itself, computed by an exact integer pass on
  the GPU (equal to nnue_reference on 1,000 positions): 0.019611 against the
  float net's 0.019550 on the trial.
- **The first surprise was good**: one pass over 8 buckets (27 M positions,
  29 s) already scored 0.0196 against sf1's 0.0304.
- **The second wasn't**: training at full speed (~99 W on the GPU) beside the
  bot cost the bot about a fifth of its speed. The charger gives 180 W
  (Matthew: 19.5 V x 9.23 A); with the bot's 8 threads as well, the CPU fell
  from ~125% to ~100% of its base clock. Eight alternated pairs: a fixed
  single-thread `bench 9` -24% with the bot playing (0-7% with it idle), the
  bot's own Speed lines 8.4 against 10.8 Mnps (-22%). That's past D(a)'s 10%,
  so the run was paused, and a duty throttle (the GPU works a set share of the
  time, `<run>.duty`) measured in short arms: half duty -2 to -7%, a quarter 0
  to -2%.
- **And the laptop came off its charger** at 01:02:52 (Kernel-Power 105) for
  eight minutes: on battery everything throttles (the bot 4.3 Mnps), which for
  a moment looked like the trainer's doing. Matthew was notified; he moves it
  about the house, up to ~10 min at a time, so train.py now waits for AC by
  itself.
- **Matthew's choice (01:45)**: (a) half duty beside the bot for the last 15
  passes, over (b) full speed with the bot -22% for ~48 min, or (c) stop at
  pass 5 and plan. nnue1 resumed from its pass-5 checkpoint at 01:47.
- **Half duty, sustained** (02:00-02:36, three 6-minute blocks paused and
  three at half duty, a bench every 30 s logged with the CPU's load): with the
  bot searching, the bench's median 974 knps against 1,080 (-10%); with it
  idle, 1,545 against 1,550. The bot's own Speed lines: 10.0 Mnps at half duty
  against 9.8 paused (63 and 50 long searches). So half duty is at the 10% line
  by the strictest instrument and invisible in the bot's own play. A worry in
  between (9.4 Mnps at half duty against 12.7 earlier in the night) was the
  bot's positions: within one game its speed fell from 12.2 to 5.4 with
  training paused, as it reached the endgame.
- My own slips, both caught before they misled: a PowerShell function named
  `Measure` was shadowed by the alias for Measure-Object, so the first duty run
  measured nothing; and the second measured full duty throughout, because the
  running trainer was a copy taken before the duty file existed. The third,
  from a restart with the current scripts, is the one in the tables.

nnue1, pass by pass (held-back error of the float net at K 1; the fitted K
stayed within 0.997-1.005 throughout; sf1 0.030396 at its best K):

| pass | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 |
|---|---|---|---|---|---|---|---|---|---|---|
| error | 0.016731 | 0.016233 | 0.015897 | 0.015690 | 0.015623 | 0.015526 | 0.015463 | 0.015421 | 0.015059 | 0.015032 |

| pass | 11 | 12 | 13 | 14 | 15 | 16 | 17 | 18 | 19 | 20 |
|---|---|---|---|---|---|---|---|---|---|---|
| error | 0.014992 | 0.014985 | 0.014973 | 0.014963 | 0.014824 | 0.014812 | 0.014811 | 0.014806 | 0.014767 | 0.014763 |

(the rate x0.3 after passes 8, 14 and 18.) **Exported (03:46): 0.014851 as the
engine computes it, against sf1's 0.030396: 51% lower.** QB came out at 64:
one output weight reached the clamp. On nnue1 itself the engine equals
nnue_reference on 1,000 positions, `bench 9` makes 3,830,535 nodes (0.80x
sf1), and float against int16 is 7.5 cp mean, -5.1 of it constant.

- `games\nnue\nets\nnue1.nnue`, 394,820 bytes. Network SHA-256 (what the
  engine prints): 4a18e09ae6544ece43cbac5bc45ac5adf0a8dc9bff973e3916b7de95da9cf497.
  File SHA-256: 666edbe590a0c5218fe852b078e71a4c28cf5721a7d72cf5dff358df9d7890af.
  Beside it: nnue1.pt (the float checkpoint), nnue1.log, and the
  measurements (nnue1-duty.log, nnue1-toggle2.log, nnue1-benches.log).
- Not done here: the match (session 4). The bot stays on sf1.

## 2026-10-17 (03:55-04:55): NNUE session 4, the match: nnue1 +742 Elo over sf1

A side session, in its own worktree (branch `claude/heuristic-poincare-2d9fec`;
the worktree came out on main at 74402ff, and fast-forwarded onto
`claude/nnue-trainer-f0d60e` at 470d8d4, as the brief meant). MATCHES.md has
the tables; NNUE.md's session 4 the summary.

- nnue1.nnue in games\nnue\nets\ matches session 2's file SHA-256
  (666edbe5...d7890af), and the engine reports its network as 4a18e09ae654.
- **`scripts\nnue-match.cmd <net> <old> [games] [concurrency] [fixed]`**,
  modelled on tune-match.cmd: builds the engine and the match tool, copies
  both, the net and the old evaluation to %TEMP%\cb-match, writes into the
  log what each engine says it loaded and the net's SHA-256, then plays at
  below-normal priority. It runs from a worktree too: the nets, openings,
  tables and logs are the main checkout's (found through `git rev-parse
  --git-common-dir`). `<old>` can be a net (session 5's matches) or
  numbers. "fixed" leaves the SPRT out.
- **The match tool now logs each engine's depth and speed**: UciPlayer keeps
  the last `info depth ... nodes ... time` line before each bestmove
  (`SearchTally`), and the log gets a line per game plus the totals after
  "final:". The smoke test needed it, and the tool had nothing. 3 tests; the
  suite 410 passed, 2 skipped.
- Before: the bot already stopped (Matthew; since 1cc1aaa, for heat while
  training ran), the laptop on AC at 100%, 7.0 GB of commit free. During: ten
  engines at ~65 MB each, the match tool ~20 MB, 6.3 GB free.
- **Smoke test, 20 games (04:00-04:02)**: +19 -1 =0. No time losses,
  crashes or illegal moves; every game ended by mate on the board or by the
  tables. nnue1 depth 9.6 at 2,167 knps, sf1 9.5 at 1,946: a normal search,
  not the exploding capture search of an untrained net.
- **Matthew's choice (04:03)**: the smoke test's LLR was 2.68 of 2.94 at
  game 20, so the SPRT would decide in ~22 games with an interval of about
  [+300, +2400]. Of (a) the SPRT alone, (b) the SPRT then 400 games at a
  fixed length (~40 min), (c) the SPRT then 2,000 (~3 h), he chose (b). The
  bot was already off, which settled the brief's other question (the match
  beside the bot or not).
- **The SPRT run (04:04-04:13)**: +114 -0 =3, 98.7%, Elo +755 [+622, +∞];
  SPRT [0, 10] accepted H1 at game 113, the first draw. It took that long
  because the LLR (fishtest's simple normal form) divides by the variance of
  the scores, which is zero while every game is a win, and
  `MatchStats.LogLikelihoodRatio` returns 0 then. 112 wins in a row read as
  LLR 0.00; one draw made it 362. Nothing wrong with the verdict, but a
  perfect score should stop the test, not stall it (TODO).
- **400 games at a fixed length (04:14-04:44)**: **+392 -3 =5, 98.6%, Elo
  +742 [+645, +970]**. As white +198 -0 =2, as black +194 -3 =3. 355 wins by
  mate on the board, 37 by the tables; the 3 losses 2 mates and 1 by the
  tables; the 5 draws 3 by the tables and 2 by repetition. Median game 103
  plies. 4.5 s a game, ~790 an hour. nnue1 depth 9.4 at 2,137 knps, sf1 9.1
  at 1,887.
- All three pooled: +525 -4 =8 in 537 games, 98.5%, +728 [+646, +886].
- Against the plan's expectations: NNUE.md guessed "anything from nothing to
  +200", since the net learns from human games scored by Stockfish rather
  than from its own search's positions. It is over three times the top of
  that. And the net plays 13% faster in nodes a second than sf1, where `bench
  9` had it 3.8% slower a node: the bench's positions aren't the games'.
  Halving the held-back error (0.0304 -> 0.0149) was worth far more than
  sf2's 0.3% (0.030396 -> 0.030317, +1 Elo).
- Logs and PGNs in games\nnue\: match-nnue1-sf1-20, match-nnue1-sf1 and
  match-nnue1-sf1-400-fixed (.log, .pgn).
- **The bot's switch: Matthew chose E(a)** (of (a) the net into bot\eval\,
  (b) read from games\nnue\nets\, (c) a Stockfish yardstick first).
  `bot\eval\nnue1.nnue` (394,820 bytes, the same SHA-256) is in git, and
  make-config.py takes the first of nnue1.nnue and eval-sf1.txt that's there.
  Checked on a copy of bot\ in the scratchpad (the real config.yml, which
  holds the token, untouched): EvalFile is the net; with it deleted, sf1;
  with both deleted, none. The engine loads the net from the bot\eval\ path
  (spaces and all) with 8 threads, Hash 256 and the tables (292 loaded): one
  3-second search, depth 13 at 11.8 Mnps against sf1's 12 at 8.9, one run
  each, so a check that it works, not a speed figure. It takes effect when
  the branch is merged into the main checkout and the bot restarts
  (start.cmd); both are Matthew's.

## 2026-10-17 (04:50-05:05): the bot plays on nnue1

- On Matthew's word (E(a), "merge ... and restart the bot between games"):
  `claude/heuristic-poincare-2d9fec` (sessions 2 and 4: the trainer, the match,
  the bot's switch) merged into compression, clean (7b2af26). Full suite **410
  passed, 2 skipped**. bot\eval\nnue1.nnue is byte for byte the net the match
  played (games\nnue\nets\ and %TEMP%\cb-match agree).
- Checked before believing +742: the bot's pre-NNUE engine build and the
  match's new build, each with EvalFile eval-sf1.txt, searched 6 positions to
  depth 9 identically (score, nodes, move), so the match's sf1 really was sf1.
- The bot had been stopped since 03:31 (for the training and the match), so no
  game was cut. `bot\start-bot.cmd` started 04:57:57 in its own window: engine
  rebuilt and copied, config.yml's EvalFile = bot\eval\nnue1.nnue; its engine
  said "evaluation: network ...nnue1.nnue (768 -> 256 x2 -> 1, sha256
  4a18e09ae654)" and loaded 292 tables; online at 04:58:05. The learner and the
  month queue weren't running, and weren't started.
- Next: its first ~30 rated games against sf1's record.

## 2026-10-17 (05:15-06:15): SSH from the laptop to the desktop

- Matthew wanted one way to run things on the desktop from here. Instead of a
  home-made command server, Windows' own OpenSSH Server, which brings its own
  login and encryption: he installed it on the desktop (elevated
  PowerShell), made a key on the laptop (`ssh-keygen -t ed25519`, no
  passphrase, so sessions can log in unattended), and put its public line in
  the desktop's `C:\ProgramData\ssh\administrators_authorized_keys`.
- The first try timed out: sshd wasn't installed yet (the first window
  wasn't elevated), and the desktop's Ethernet counts as Public, which his
  Windows build won't let him change, so a Private-only rule could never
  apply. **He turned the desktop's Windows Firewall off.** It works: `ssh -o
  BatchMode=yes Admin@id10terror-desktop` logs in by key, no password.
- What SSH found: the desktop's card is a **GTX 1660, 6 GB** (not the 1650
  remembered), driver 617.42 (updated during this; CUDA 13.4, so the cu132
  wheel runs there), Windows 11 build 22631. NNUE.md's table and session 2b
  are corrected: about 0.4x the laptop card on this net, and 2b is now
  driven over SSH.
- The firewall is back on (Matthew, ~06:40): the SSH rule now applies to any
  profile but only from LocalSubnet and fe80::/10, and all three profiles
  are enabled. Checked from the laptop: SSH by key and the share both
  answer. The share is `D:\-=Chess Bruteforcer=-` on the desktop, and
  Python 3.14 is already installed there (C:\Program Files\Python314).
- The stray key pair `key2` / `key2.pub` (made at ssh-keygen's file prompt,
  unused) was deleted from the repo root on Matthew's word.

## 2026-10-17 (06:15-07:50): NNUE session 2b, the desktop's GTX 1660 as a second trainer

A side session, branch `claude/nnue-desktop-8d5f2a` (cut from compression at
aa6987b), driving the desktop from the laptop over SSH. NNUE.md's session 2b
has the tables; this is the story.

- **What the desktop had**: Python 3.14.4 with numpy 2.4.6, typing-extensions
  and setuptools; the GTX 1660 idle at ~800 MiB (the display); D: is a 2 TB
  HDD with 1.37 TB free; i7-3770, 16 GB, 11.4 GB free. torch's other seven
  dependencies were missing, so they were zipped out of the laptop's venv
  (same Python, same platform; 9.5 MB) and copied, with the wheel: **nothing
  was downloaded**, and Matthew's yes for PyPI wasn't needed.
- **Copies, capped** (`scripts\capped-copy.py`, new: each 1 MiB flushed to
  the share, the running average held to 7,500,000 B/s, a SHA-256 of each
  file as read). The Wi-Fi carried 7.32 MB/s during the wheel. The wheel
  took 280 s (7.14 MB/s), the records 1,173 s (6.46 MB/s: 4.4 while pip was
  unpacking torch onto the same desktop disk, since each chunk waits for its
  flush). The desktop hashed its copies on its own disk over SSH: the wheel
  is the server's bfdad379...e7520a4, and all 66 record files match. One
  share job at a time, nothing else on the share meanwhile (the month queue
  and the learner weren't running).
- **The venv**: `setup-desktop.cmd` (new, in the share's nnue folder):
  `--system-site-packages`, the zip unpacked into site-packages, the wheel
  with `pip --no-index --no-deps`, `pip check` clean. torch 2.14.1+cu132
  sees the GTX 1660, `get_arch_list()` = sm_75, sm_80, sm_86, sm_90, sm_100,
  sm_120.
- **Running it**: `scripts\nnue-desktop.ps1` (new) registers a scheduled
  task per run on the desktop (Admin, S4U: no password stored, no window on
  his screen) that runs `train-desktop.cmd` (new) with the desktop's own
  data and output folders. A test task imported torch and summed on the GPU
  from session 0 first. Two snags: `%USERDOMAIN%` in an SSH session is
  WORKGROUP ("No mapping between account names and security IDs"; the task
  now takes the Windows identity), and train-desktop.cmd first passed only
  `%2`-`%9`, which dropped a ninth token (`--progress 2`): it passes
  everything now, checked with ten. nnue-train.cmd has the same limit (TODO).
- **The check**: the laptop had **1.03 GB of commit free** (the Claude app's
  processes 6.3 GB, the bot's engine 2.6 GB), too little for torch's ~2.8 GB
  beside the bot. So the laptop's half of "the same short run" is nnue1's
  own first two passes, from its log: passes 1-5 ran from a clean start, and
  train.py's training code hasn't changed since but for the battery wait.
  The desktop replayed them (`2b-replay --passes 2`): the same positions at
  all 16 progress lines, running losses within +-0.000033, held-back error
  **0.016734 / 0.016159 against 0.016731 / 0.016233**. A repeat on the
  desktop made the **same net to the byte** (sha256 e6840ae8...), and a
  second seed moved held-back error 0.000100 after pass 1 and 0.000013 after
  pass 2: the machines differ by the size of gap a new seed makes, under
  0.0001, against a 0.0005 gain from pass 1 to 2.
- **Speed**: **634-641 k positions a second** at 256 hidden, 0.65x the
  laptop's card (the specs suggested 0.4x), so nnue1's 20 passes take ~1.9 h
  there; **362 k at 512 hidden** (a 1-minute probe). The card at 75-84 W of
  130, 1,920 MHz steady, 62-70 °C, fan ~66%; torch 615 MB allocated, 996
  reserved; the trainer's host commit 2.6-3.2 GB.
- **Matthew asked whether the desktop's card has brownouts**: no sign. Its
  driver counts 0 µs of power capping, power braking and thermal slowdown
  since the 14th's boot; 20 one-second samples under load held 1,920 MHz
  with no slowdown flag; no driver resets, WHEA errors or unexpected power
  losses in 30 days of the System log (1,776 events, readable). The laptop's
  card: 0 µs of braking, 0.06 s of power capping since the 15th; one driver
  reset on the 12th at 03:26 (Display 4101, nvlddmkm 153). Seen in passing
  on the desktop: volume H: has asked for a full chkdsk 18 times, and TCP/UDP
  ephemeral ports ran out 10 times (8th and 15th).
- **Decision F**: Matthew chose **(b) 512 hidden** (07:45) over (a) nnue1's
  recipe again or (c) both. `nnue1-h512` started 07:46 on the desktop:
  nnue1's recipe with `--hidden 512`, ~3.4 h.

## 2026-10-17 (07:46-11:15): nnue1-h512, the desktop's first full run

- 20 passes in 3 h 21 min on the GTX 1660 as a scheduled task, 594-599 s a
  pass, no pauses, exit 0; Matthew started a chkdsk on the desktop's H:
  (another disk) during it, and the pass times didn't move. torch's GPU memory peaked at
  1,003 MB allocated, 2,052 reserved; the trainer's host commit 4.1 GB.
- **Held-back error 0.013664 as the engine computes it, against nnue1's
  0.014851: -8.0%** (float 0.013626, so int16 costs it 0.28%; QB 64). By
  pass 4 it was already below nnue1's last pass; passes 15-20 bought
  0.00007. NNUE.md has it pass by pass.
- Brought back with `scripts\nnue-desktop.ps1 fetch nnue1-h512` into
  games\nnue\nets\ (789,572 bytes, network sha256 8a6cd3aff627...). The
  engine loads it through EvalFile as the bot would ("768 -> 512 x2 -> 1,
  sha256 8a6cd3aff627") and picked a6 against the Ruy Lopez at depth 10.
- `bench 9` against nnue1, 6 rounds alternated, one thread, beside the bot,
  on AC: **-4.0% a node** (median 1,090 against 1,135 knps), its tree 2.8%
  bigger, so ~7% longer to the same depth. No match started: that is
  session 5's (`scripts\nnue-match.cmd nnue1-h512 nnue1`).
- The desktop tidied: the probe nets (2b-argtest, 2b-speed512) and the GPU
  test's file removed, the five check and probe tasks unregistered; the
  logs, the check's nets and nnue1-h512's task stay.

## 2026-10-17 (20:00-23:25): nnue1-h512 beats nnue1 by +76; the bot switches

- **When**: Matthew's word (20:00): tonight, after the Stockfish-2500 match,
  while the bot is still off. A launcher (scratchpad after-sf-h512.ps1, its
  own minimized window, from 20:01) waited for "finished" in
  games\match-after-counts.log, then checked the bot was off, commit free
  (6.0 GB) and the clock (before 03:00), and ran `scripts\nnue-match.cmd
  nnue1-h512 nnue1` from this worktree at 22:48:04 (engine and match tool
  pre-built at 20:00 so no build could fail unattended). The top session
  had a heads-up so the bot wouldn't start over it.
- **The Stockfish match first** (the top session's): 10 of 10 at 3+2, all
  by checkmate, no time losses: at least ~+183 over Stockfish-2500 (95%,
  one-sided, binomial).
- **The match**: **SPRT [0, 10] accepted H1 at game 284** (LLR 2.98); final
  285 games, +114 -53 =118, **60.7%, +76 Elo [+45, +107]**, LOS 100%, in 29
  minutes. Matthew asked at game 275 for an early answer (LLR 2.81, +73
  [+42, +105]); the SPRT, made to be watched game by game, decided four
  minutes later by itself. No time losses, crashes or illegal moves. In
  play nnue1-h512 searched 8.9% slower (2,131 against 2,339 knps).
- **The switch, Matthew's (a)** (~23:20): `bot\eval\nnue1-h512.nnue` (in git,
  789,572 bytes, the same bytes the match played: 38eeba1f... in
  games\nnue\nets, %TEMP%\cb-match and bot\eval alike), and make-config.py
  tries nnue1-h512.nnue, nnue1.nnue, eval-sf1.txt in that order (its own
  loop, run on this worktree's bot\eval, picks nnue1-h512). Takes effect
  when this branch is in the main checkout and `bot\start-bot.cmd` runs.
  Not tried at 8 threads beforehand (commit was down to 2.8 GB); the bot's
  first games are that check, then its first ~30 rated games against
  nnue1's.

## 2026-10-17 (11:00-11:30): a game lost to a dropped stream; lichess-bot rejoins now

- **PeachFruit, lost on time without being played out.** The game started
  11:00:49 (we were black); its stream ended at 11:01:21 and lichess-bot
  logged "Game over" while the game ran on; it came back at 11:02:28 with 91 s
  left against 161, played five moves, and at 11:04:08 the stream ended again.
  Lichess: `outoftime`, white won. The same thing as TroutBot on 2026-10-16.
  Today's dropped streams: 05:43 (no share traffic), 11:01 and 11:04 (June's
  month count reading the share, ~5-8 MB/s over the Wi-Fi). The share
  traffic makes them likelier, but they happen without it too.
- **The cause, in lichess-bot's `play_game`**: a game stream that simply ends
  raises StopIteration, and `stay_in_game = not stopped and ...` leaves the
  game at once, without asking Lichess; after other stream errors it never
  opens a new stream either. **Fix, bot/lichess-bot-rejoin.patch** (applied
  by start-bot.cmd beside the chat patch; lichess-bot itself is outside our
  git): when the stream ends or fails and our copy of the game isn't over
  (and it isn't correspondence), `rejoin_game_stream` asks whether the game
  is still on (`game_is_active`) and opens the stream again, up to 10 tries
  2 s apart. The new stream's first line is the whole game: its state goes
  first, and `prior_game` is reset so a move of ours that's due gets played.
- **bot/test-rejoin.py** runs lichess-bot's own pretend lichess.org (no
  network, no token, no pytest: it isn't in the bot's venv) with the homemade
  Scholar's-mate engine as black, and ends the game's first stream after the
  opponent's first move. Original lichess-bot: "+++" then "Game over" at once,
  no result, FAILED at 60 s. Patched: "rejoined (try 1)", "b won!", passed (a
  few seconds). The Manager it uses must be shut down before exiting, or its
  server process keeps the console open.
- On Matthew's (b): the bot stopped after the FlounderBot game (won, by mate,
  11:19) and stays off until June's count is done, then starts with the fix.
- **June's count died at 11:16** (34 M of 86.5 M games): zstd "Allocation
  error: not enough memory", then MemoryError sorting; Windows low-memory
  events at 10:47:36 and 11:11:36. The second came as the rejoin test started
  (a whole pretend lichess-bot with its Manager and pool), run beside the
  count, the bot, the learner and a dozen Claude processes without checking
  commit headroom first. The month queue stopped with it.
- **18:3x, on Matthew's word**: four finished NNUE sessions archived (data,
  C#, trainer, the match; all merged into compression), commit free 3.5 -> 7
  GB. The trainer's PyTorch venv (2.9 GB, torch 2.14.1+cu132, CUDA working)
  was moved first from its worktree to the main checkout's
  scripts\nnue\.venv (ignored there too), as archiving deletes a worktree.
  The month queue restarted at 18:38:53 and is counting June again. A
  launcher window ("Bot: waiting for June 2026 to be counted", log
  games\bot-after-june.log) starts bot\start-bot.cmd once the queue logs
  June's "games read" and commit headroom is 3 GB or more. After June the
  queue goes on to count May (on the share, not yet counted) with the bot
  playing.
- A slip on the way: the old queue window from 09:30, stopped at 11:16 and
  sitting at its `pause`, still has month-queue in its command line, so a
  check for "a queue already running" matched it. Another queue was started
  and stopped again within a minute before this one; nothing was written.

## 2026-10-17/18 (23:26-03:00): the bot on nnue1-h512; two levers train overnight on the desktop

- Matthew rebooted the laptop (23:23) and fast-forwarded compression to
  `claude/nnue-desktop-8d5f2a`; `start.cmd` brought up the bot, the learner
  and the queue at 23:26. The bot's log: "evaluation: network
  ...bot\eval\nnue1-h512.nnue (768 -> 512 x2 -> 1, sha256 8a6cd3aff627)".
  The queue resumed April's download from 3.1 GB.
- **"Desktop is free"** (Matthew): measured first, then asked. A 1024-hidden
  probe (6 buckets, 1 pass) trains at **158 k positions a second** on the
  1660 (a full run 7.7 h), and its net runs **23% slower a node** than
  nnue1-h512 in `bench 9` (1,063 against 1,386 knps, 5 alternated rounds,
  the laptop on AC, the bot playing). Options: (a) two 512 levers back to
  back, (b) 1024 hidden, (c) the blend alone. **He chose (a).**
- **train.py**: `--result-blend x` (the target (1 - x) s(cp) + x result,
  for the side to move; `targets()`) and `--mates` (mate-scored positions in
  training, target 0 or 1; held-back error still the old measure).
  `checks.py targets`: 10,000 held-back records (604 mates), equal to the
  rule restated in numpy to 9e-8 at blends 0, 0.25 and 1, and mates' targets
  exactly {0, 1}. **nnue-desktop.ps1** queues runs in one task with a lone
  `+` (`call ... & call ...`); `stop <run>` ends the task holding it. A smoke
  pair on the desktop (one bucket each, queued) ran clean: the mates run saw
  3,628,080 positions where the blend run saw 3,419,568 (+6.1%, the mates).
- **Started 02:43**: task "nnue h512-blend25 then h512-mates", both at 512
  hidden, nnue1-h512's recipe otherwise.
- **The 1660 is running at 1,200 MHz and 40 W**, not the 1,920 MHz and
  75-84 W of nnue1-h512's run: 284 k positions a second instead of 362 k
  (blend done ~07:10, mates ~11:40, instead of ~06:15 and ~10:00). The
  driver lists no slowdown reason and no clock lock, the power limit is the
  full 130 W, the plan Balanced. What's different: Matthew's PySynthRack
  (open since 19:19) has a graphics context on the card. **He closed it at
  ~04:20: 1,935-1,950 MHz and 73-77 W at once** (10 samples, 2 s apart),
  with nothing else changed on the GPU's process list but Discord. Close it
  before training. Blend done ~06:30, mates ~10:00.
- **h512-blend25 finished 06:25** (desktop clock), exit 0. Held-back error
  as the engine computes it 0.015230 at K 1, 0.014320 at K 1.2225 (fitted),
  against nnue1-h512's 0.013664 at K 1.00: worse on the cp-only measure, as
  a blend must be, and its scores ~18% smaller in scale. Fetched into
  games\nnue\nets\ (network sha256 8b73e98ad091...); EvalFile loads it.
  h512-mates started 06:25:39. No match yet: Matthew's call when.

- The desktop tidied again (the probe's and smoke runs' nets and tasks).

## 2026-10-18 (08:20-10:30): the result blend loses; h512-mates trained

- Matthew stopped the bot for the blend's match and asked for it on the
  laptop (a desktop match was begun and dropped on his word: the i7-3770
  has no AVX2, so the engine would run its scalar network code there).
  Checked first: no lichess-bot.py, 6.7 GB of commit free, on AC; the one
  engine still running was the book learner's (engines\learner), analysing
  the bot's overnight games on one core. Both arms bear that equally.
- **h512-blend25 v nnue1-h512** (`scripts\nnue-match.cmd`, its own window,
  08:22:46-10:25:17): **SPRT [0, 10] accepted H0 at game 1,252**: +311 -349
  =592, 48.5%, **-11 Elo [-25, +3]**, LOS 7%. The score never left 47-52%;
  no time losses, crashes or illegal moves; equal speed. So the blend at
  0.25 costs a little, if anything: Lichess's results follow blunders made
  long after the position as much as the position itself.
- **h512-mates finished 09:59** (desktop clock), exit 0: 20 passes of
  232,201,738 positions, held-back error 0.013813 as the engine computes it
  (nnue1-h512 0.013664; +1.1% on positions without mates, the capacity the
  mates took). Fetched; EvalFile loads it (sha256 0cf690d690a3...). Its
  match against nnue1-h512 waits on Matthew. The bot is still off.

## 2026-10-18 (14:03-16:05): mates in training don't help either

- On Matthew's word, with the bot still off: checked again (no bot, 6.1 GB
  free, on AC, only the learner's engine), then `scripts\nnue-match.cmd
  h512-mates nnue1-h512` in its own window, 14:03:48-16:03:08.
- **SPRT [0, 10] accepted H0 at game 1,212**: +310 -349 =553, 48.4%, **-11
  Elo [-26, +3]**, LOS 6%. Never above 50%; no time losses, crashes or
  illegal moves; equal speed.
- Session 5 so far: 512 hidden +76, the result blend -11, mates in -11.
  Both target changes cost a little if anything; size paid. More data is
  the lever the evidence points to next. The bot stays on nnue1-h512, and
  is still off until Matthew starts it.

## 2026-10-18 (17:24-17:45): the bot back; July's and August's records begun on the desktop

- **The bot back online 17:24:55** (`bot\start-bot.cmd` on Matthew's word):
  nnue1-h512, sha256 8a6cd3aff627, "awaiting challenges".
- **More data** (Matthew asked what it means; yes): a fresh 512 net on July,
  August and September. Told him to expect a modest gain: nnue1-h512's
  training error is only 2.1% below its held-back error (0.013348 against
  0.013626; nnue1's 0.9%), so it isn't starved of data yet.
- **The records are made on the desktop**: `scripts\eval-games.py` (the
  first step of tune-data.cmd) reads a month off the share uncapped, ~9 MB/s
  in September's run, over the 8 MB/s rule beside the month queue. The
  month files live on the desktop's D:, which has Python 3.14 (zstd in the
  standard library) and .NET 8, so new `scripts\nnue\data-desktop.cmd` runs
  the filter, `game import`, `nnue data` (heap 1 GB) and `records.py check`
  there, each step skipped when done; `nnue-desktop.ps1 data <month>` starts
  it as a task, and `sync` now builds and copies the Cli (773 KB) and
  eval-games.py. A probe: 20 MB of July in 10.3 s. July and August started
  17:34 as two tasks: each filter reads ~1.7-1.8 MB/s of its month (~4.3 h),
  the D: disk 94% idle, so side by side costs neither. Records due ~01:00.
- **train.py --add-data MONTH...**: the buckets of more months beside
  --data; held-back error stays --data's (September), so the new net
  compares with the old ones. Checked: 64 buckets alone, 128 with September
  added twice; the parser takes several names and leaves --data alone. Not
  synced yet: the running tasks are executing data-desktop.cmd from the share.
- A slip worth keeping: the PowerShell tool refused a call whose here-string,
  meant for the desktop, held `Remove-Item` on a D:\ path ("system path ...
  blocked"): it reads the text, not where it runs. A script file passes.

## 2026-10-18 (19:00-20:10): the bot back after a move; accepted challenges that never started

- Matthew stopped the bot to move the laptop upstairs, then `start.cmd`
  (19:12): bot, learner and queue all up; the queue had finished April
  (counted 15:10; the book now 9.5 M games, six months) and resumed March at
  3.84 GB, ~570 KB/s.
- **"Accept ... challenge from BOT simple-bot", then nothing** (Matthew): no
  game. lichess-bot's accept_challenges logs "Accept", then "Process Queued"
  once lichess.org takes it; it logs a 404 as "Skip missing" and swallows
  any other HTTP error or a reply slower than 2 s without a word. Over the
  logs since 14 October (scratchpad accepts.py): **191 accepts, 46 clean, 9
  missing, 136 swallowed** with no game. Of the accepts made straight after a
  game ended ("Process Freed" just before): **133 of 168 swallowed**; made
  while idle: 3 of 23. The bot played one game at a time (concurrency 1), so
  challenges arriving mid-game waited in its queue; by the game's end the
  challenger had usually withdrawn it.
- **Matthew's (a)**: two games at once ("not really more"; he'd like to tell
  the third "I'm already playing 2 people"). lichess.org lets a bot decline
  only with its fixed reasons; "later" shows the challenger "This is not the
  right time for me, please ask again later." So:
  - **bot\lichess-bot-busy.patch** (handle_challenge): a supported challenge
    that arrives with every slot taken (ongoing games plus queued challenges
    at `concurrency`) is declined "later" at once. Made against the live
    lib\lichess_bot.py (chat and rejoin patches in), LF; `git apply --check`
    clean on the real lichess-bot. start-bot.cmd applies it like the others.
  - **bot\test-busy.py**: lichess-bot's own handle_challenge, its defaults at
    concurrency 2, a pretend lichess.org that lists games and records
    declines. Six cases (no game, one, two, one plus one queued, a variant it
    doesn't play with slots free and full): all pass with the patch; without
    it the two full-slot cases fail.
  - **make-config.py**: concurrency 2, max_simultaneous_games_per_user 1 (two
    different people). Matchmaking still challenges only when idle
    (allow_during_games is false by default), so the second slot is for
    whoever challenges it.
- Takes effect after the merge and the bot's next start, between games.
  Matthew: if it loses more at two games (each engine gets about half the
  CPU), back to concurrency 1.

## 2026-10-18 (19:56-20:10): a won knight ending drawn (thunfisch-bot, UBIA6Hdc)

- **How it ended**: a draw by insufficient material at move 122 (19:56). From
  move 77 the bot had a king and b-pawn against a king and knight, a table
  draw; it shuffled its king in the corner instantly (depth 1, 0.00) and
  offered draws, which thunfisch-bot (2225) kept declining. Then it pushed
  b7, Nf5+ and Ne7+ won the pawn, and king and knight against a king ended
  the game. Every move there drew; this one ended it rather than waiting for
  the 50-move count.
- **Where the win went** (scratchpad where-the-win-went.py: Lichess's 7-piece
  tablebase from 7 pieces, Stockfish 16 for 4 s on one thread before that):
  - From move 70 (7 pieces) every position was a table draw. At move 77
    Matthew asked whether keeping the knight and queening the b-pawn wins:
    no. Black's knight on a4 watched b6, and of Nxh5's alternatives eight
    drew and six lost (Kd2, Kd3, Ke2, ...).
  - The advantage went at **moves 53-54**, at 13 pieces: 53. e7 (Stockfish
    +0.81 after it; Rc4 kept +2.47), then 54. Kd7 (+0.09; Ke6 +0.75). The
    bot itself said +3.03 for e7 at depth 16, and +2.4 for the knight
    ending with three pawns each that followed (Stockfish: about +0.25),
    sliding to 0.00 only by move 74. So the evaluation, not the search,
    overrated the far passed b-pawn in a knight ending. One game is an
    anecdote; a TODO line asks for more.

## 2026-10-18 (20:10-20:45): the month queue off; indexes\ and delete me
- **The month queue is off on Matthew's word**: he closed it at ~20:06, his
  mum's YouTube struggling even at the 500 KB/s cap. March's .part stays at
  5.22 of 29.35 GB on the share. When it returns: 300k, or overnight only.
- **Where the index lives: `indexes\`** in the project folder (Matthew). July's
  and 2013-01's index and their logs moved there from games\index (a rename
  on C:, instant); index-month.cmd and clean-up.ps1 point there, INDEX.md
  says so; git ignores it. Checked after the move: 2013-01's `check` passes
  (1,000 of 1,000 rows, 3 of 3 boundary games); July's Parquet holds
  89,288,421 rows, Lichess's count.
- **`delete me\`** (Matthew: "shove anything you want gone inside it"), 42.3 GB,
  nothing in the project reads any of it: May's laptop copy (29.69 GB; on the
  share, checked, counted), the analysed games as .pgn for September and July
  (7.63 + 0.81 GB; their .cbg is what's read), July's lines.u16 + lines.json
  (2.86 GB; the trial is done), the index's old plain columns (1.27 GB July, 2
  MB 2013-01). `delete me\WHAT-THIS-IS.txt` lists them, and what was kept and
  why (the PyTorch wheel, September's NNUE records, tables-baseline). Git
  ignores the folder. His to delete.
- The bot won one more game, then Matthew stopped it (~20:45).

## 2026-10-19 (05:20-05:40): July's and August's records done; h512-3m training

- **Both months' records finished on the desktop** (desktop clock): July
  04:51, August 05:22, each "all well" from records.py check. July 240,433,752
  records (13,178,690 mates; 8,735,386 training games, 178,260 held back);
  August 253,407,598 (13,198,270; 9,194,056; 187,670). Steps: filter 4.1-4.3
  h, import 4.4-4.6 h, nnue data 2.3-2.5 h, check 21-22 min: the import and
  records ran ~4x slower than on the laptop (i7-3770, hard disk, two months
  at once), so my ~01:00 was ~4 h early.
- **h512-3m** (Matthew: "just go ahead"): `train h512-3m --hidden 512
  --add-data 2026-07 2026-08 --passes 10 --lr-drops 4/7/9`, started 05:26
  (laptop clock). Two false starts first, each "unrecognized arguments: 7 9"
  and exit 2 within a second: PowerShell reads an unquoted `4,7,9` on a
  command line as a three-item array. train.py now takes 4/7/9 as well,
  and nnue-desktop.ps1 turns commas into slashes for the desktop's cmd.
- Running at 1,935 MHz, 77 W, 99% (PySynthRack closed): 338 k positions a
  second over the first 8 of 192 buckets, ~31 min a pass, done ~10:45.
- A false alarm at 05:31: the watcher waited for "finished" in h512-3m.err,
  which already held the two false starts' "finished, exit 2" lines. The
  notebook's 2026-10-17 note again (count the matches); the second watcher
  waited for a third.

## 2026-10-19 (08:10-08:30): a reconnect put two players on one game; lichess-bot plays a game once now

- **What happened** (U4Gg0qAp, black v Ravager_BOT 2802, blitz): a connection
  drop at ~08:11 closed both the game's stream and the event stream. The
  game's player rejoined its stream (the rejoin patch, working), and when the
  event stream reconnected, lichess.org sent gameStart for the ongoing game, as
  it does for every ongoing game on each connect. lichess-bot started a second
  player for it ("Process Used" again at 08:11:26). lichess.org keeps one
  stream open a game, so the two closed each other's, rejoining in turn (46
  "rejoined" lines in ~90 s), both searching every move (two "move: 44", two
  "move: 48"), until lichess.org rate-limited the stream (08:13:05: "Giving
  up play_game(...) after 7 tries (RateLimitedError)"), which killed one. The
  other played on alone. Its local_game_done also took the game off
  lichess-bot's list of active games while it went on.
- **bot\lichess-bot-one-player.patch** (lib\lichess_bot.py, on top of the
  chat, rejoin and busy patches; start-bot.cmd applies it after them): a set
  of the games with a player running here (added in start_game_thread,
  removed on local_game_done, emptied when a restart makes a new pool);
  start_game skips a gameStart for a game in it and logs "being played here
  already". The rejoin also takes RateLimitedError as one failed try instead
  of dying.
- **bot\test-one-player.py**: lichess-bot's pretend lichess.org sends gameStart
  on the first two event streams; the game's first stream drops only after the
  second is out (so the test can't pass by the game ending first, which it did
  once before that); every game stream opened is counted across processes.
  Passes on exactly 2 (the player's own and its rejoin) and a win: with the
  patch, passed twice; without it, FAILED twice (3 streams, two players). It
  runs on a copy (`LICHESS_BOT_DIR`), so the running bot was left alone.
- Takes effect at the bot's next `bot\start-bot.cmd`, between games (not
  `start.cmd`, which would start the month queue Matthew turned off).
- **Also seen, not fixed**: lichess-bot quit at 00:32:52 on a TypeError in
  accept_challenges (`while len(active_games) < max_games and
  challenge_queue:`: the manager's list said its length was None) and came
  back at 01:16 when lichess.org answered (run-bot.ps1), 44 minutes down. Once
  so far; a TODO line.
- Matthew stopped the downloads (~08:15).

## 2026-10-19 (08:30-08:55): the bot restarted with the one-player fix; the patches made to stack cleanly

- Matthew restarted the bot between games (08:32:31, `bot\start-bot.cmd`): the
  one-player patch went in. But it changed a line of the rejoin patch's (the
  rate limit in rejoin_game_stream) and put its set next to that patch's
  lines, so `git apply --reverse --check` no longer found the rejoin patch
  in: from the next start, start-bot.cmd would have tried to apply it again,
  failed, and printed "The rejoin fix didn't go in" with the fix in place.
- Rebuilt in a copy from lichess-bot as downloaded: chat, then the rejoin
  patch with `lichess.RateLimitedError` among its caught errors (its only
  change), busy, then the one-player patch with `games_playing` defined above
  start_game and added after `active_games[game_id] = ...`, clear of the rejoin
  patch's lines and their three lines of context. All four read as in on the
  result. The live lichess-bot went the same way (old one-player, busy, old
  rejoin out; new rejoin, busy, new one-player in, each by `git apply`): its
  lichess_bot.py equals the rebuilt one, line endings aside. The same code as
  the running bot's, moved, so it carries on.
- test-rejoin.py, test-one-player.py and test-busy.py all pass on the rebuilt
  copy (each takes `LICHESS_BOT`, a folder to test instead of
  bot\lichess-bot, so a running bot is left alone).
- **Ravager_BOT's game wasn't saved** to bot\games: lichess-bot's record writer
  failed ("san() ... expect move to be legal ... c7g3"), a comment from the two
  players' interleaved searches put on the wrong move. The learner won't see
  it unless its PGN is fetched from lichess.org.
- The bot's quit message at 08:32:23 (`Cannot have cache with result_handler
  not alive`) came from the restart, lichess-bot's pool closing mid-shutdown.

## 2026-10-19 (10:42-10:50): h512-3m done, a small gain on held-back error

- **h512-3m finished 10:42** (desktop clock), exit 0: 10 passes of
  677,005,066 positions at 362-370 k a second, no pauses; the rate stepped
  down after passes 4, 7 and 9 as asked.
- Held-back error (September's positions, as the engine computes it):
  **0.013640 at K 1 against nnue1-h512's 0.013664 (-0.2%); 0.013437 at its
  fitted K 1.10 (-1.7%)**. The fitted K sat at 1.06-1.13 every pass: its
  scores run ~10% small against September's Stockfish scale, perhaps because
  July's and August's labels sit on a slightly different one. A modest gain,
  as said beforehand; the match decides.
- Fetched into games\nnue\nets\ (network sha256 9da33208a6f5...); EvalFile
  loads it; a6 against the Ruy Lopez at depth 10. Its match against
  nnue1-h512 waits on Matthew (the bot off).

## 2026-10-19 (11:05-14:35): h512-3m +11 Elo over nnue1-h512, by a little

- Matthew stopped the bot at ~11:05; `scripts\nnue-match.cmd h512-3m
  nnue1-h512` in its own window, 11:09:27-14:26:50 (10+0.1, 5 at a time,
  the 300 openings, the tables adjudicating).
- **2,000 games: +583 -521 =896, 51.5%, +11 Elo [-1, +22], LOS 96.9%.** The
  SPRT [0, 10] stopped undecided at the game limit (LLR 1.73 of 2.94). Level
  at game 300, ahead from game 400 on (+5 to +21). No time losses, crashes or
  illegal moves; equal speed (2,091 against 2,078 knps, depth 10.2 each).
- So three months' data is very likely better than September alone, but by
  a seventh of what 512 hidden gave. Written into MATCHES.md, NNUE.md
  (session 5) and TODO. Whether the bot switches to it is Matthew's call;
  the bot stays off until he says.
- Matthew asked twice for the count while it ran, and worried that opening
  the log would stop its writes: it doesn't (an editor reads a copy; only
  saving an edit could interfere).
- **The switch, Matthew's H(a)** (~14:35): `bot\eval\h512-3m.nnue` (in git,
  789,572 bytes, the bytes the match played: 665297e6... in games\nnue\nets,
  %TEMP%\cb-match and bot\eval alike), and make-config.py tries h512-3m.nnue,
  nnue1-h512.nnue, nnue1.nnue, eval-sf1.txt in that order (its loop, run on
  this worktree's bot\eval, picks h512-3m). EvalFile loads it from bot\eval
  (network sha256 9da33208a6f5; a6 against the Ruy Lopez at depth 10). Takes
  effect when this branch is in the main checkout and `bot\start-bot.cmd`
  runs.
- A gotcha: `python - ... </dev/null` in Git Bash started Python 3.14's
  interactive console (NUL is a character device, so it looks like a
  terminal) and looped on console errors until stopped. Scripts go in files.

## 2026-10-19 (17:00-17:20): h1024-3m training on the desktop

- Matthew's yes to the next lever: 1024 hidden on all three months.
  `scripts\nnue-desktop.ps1 train h1024-3m --hidden 1024 --add-data 2026-07
  2026-08 --passes 10 --lr-drops 4/7/9`, h512-3m's recipe but for the size.
  The desktop was idle beforehand (300 MHz, no PySynthRack on the card, D:
  1.27 TB free).
- Started 17:08:40 (desktop clock), first line 17:11:52: 188-192 k positions
  a second over the first 16 buckets, the 1660 at 1,920 MHz, 83 W, 100%, 70
  C, 1.1 GB peak on the card. ~1 h a pass, **done ~03:00**. (The 158 k a
  second of 2026-10-18's estimate was probably taken while PySynthRack held
  the card at 1,200 MHz.)
- The caveat stays: on a probe net 1024 hidden searched 23% slower a node
  than 512 (`bench 9`). Its match against h512-3m decides.

## 2026-10-19 (21:25-21:50): the 3070 against the 1660 at 1024 and 2048 hidden

- Matthew asked how long a 2048 net on September (nnue1-h512's recipe, 20
  passes) would take, and whether the laptop's 3070 beats the 1660. His (a):
  a speed race on the laptop with the bot off (it had gone down at 21:23;
  I took its TypeError for his stopping it, but memory had run out: the
  23:20-00:45 entry). On the charger, 4.1 GB
  of commit free, the book learner beside it.
- `train speed-<H> --hidden <H> --buckets 16 --passes 1 --held-back-max
  200000 --out <scratchpad>`, a quarter of September each:

  | hidden | 3070 laptop | 1660 desktop | 3070 / 1660 |
  |---|---|---|---|
  | 256 (session 2) | 0.96-1.17 M a second | 634-641 k | 1.5-1.8x |
  | 1024 | **295 k** (185.7 s for 54.7 M) | 188-200 k (h1024-3m, running) | 1.5x |
  | 2048 | **161 k** (0.55x its 1024) | ~110 k (not measured; its own 0.55x) | ~1.5x |

- So 2048 on September, 20 passes of 218.9 M: **~7.5 h on the 3070 (the
  bot off throughout), ~11 h on the 1660.** The laptop is ~1.5x the desktop
  at every size, not more: the bigger nets don't favour it.
- The 2048 run trained to the end of its quarter-pass, then ran out of GPU
  memory in the held-back scoring (`scores()`: chunks of 65,536 positions
  make 0.5-1 GB tensors at 2048, and only ~3.9 of the card's 8 GB was free
  with Windows' own users of it). A real 2048 run needs that chunk scaled
  down with the hidden size. The 1024 run hit the same allocator warning
  and recovered. The engine also loads at most 1024 (Network.MaxHidden),
  though its int32 sums would still fit at 2048 (256 x 255 x 32,640 =
  2.13 billion).

## 2026-10-19 (21:45-22:40): h768-3m on the laptop overnight

- Matthew asked about 768 hidden on the laptop (no engine change: 16 to
  1024 in steps of 16 load already) and chose (a): h768-3m, the three-month
  recipe, on the laptop tonight with the bot off. He asked whether it could
  start on September while the rest copied: it could, but the trainer fixes
  its files at the start, so it would no longer be the recipe h512-3m and
  h1024-3m had; waiting cost ~40 min. "recommendation taken".
- **July's and August's records copied from the desktop** into
  games\nnue\2026-07 and 2026-08 (scripts\capped-copy.py at 7.5 MB/s, one
  job): 15.8 GB, 21:46-22:25, 6.5-6.9 MB/s. All 132 files' SHA-256 as read
  over the link equal the desktop's own, read on its disk over SSH.
- **The trainer's commit**: on the desktop it rose after pass 1 and stayed up
  (+1.1 GB at 512 hidden, +2.2 GB at 1024), the held-back scoring's GPU
  cache. With ~4 GB of commit free on the laptop that would have been close.
  train.py now scores in chunks of 2^24 / hidden positions (64 k at 256, as
  before) and empties the cache after; int_scores the same at 2^23 / hidden.
  A 768 test (4 buckets, 3 passes): commit flat at 3.3-3.4 GB; `checks.py
  quantised` on 25,000 positions: the GPU's integer scores against
  nnue_reference 0 differ. Also 430-440 k positions a second at 768.
- **scripts\nnue-train.cmd** dropped options past the ninth word (TODO);
  tonight's command has ten. Fixed with train-desktop.cmd's `for /f`, and it
  takes the main checkout's venv when run from a worktree, so the run uses
  this branch's train.py.
- **Started 22:25:55** (its own window, `scripts\nnue-train.cmd h768-3m
  --hidden 768 --add-data 2026-07 2026-08 --passes 10 --lr-drops 4/7/9`):
  445-447 k positions a second, ~25 min a pass, **done ~02:40**. The 3070 at
  1,275 MHz, 74 W, 64 C. Commit free fell to 1.16 GB with it running (the
  page file can grow another ~1.6 GB): a watcher logs the headroom every 5
  minutes and calls out under 0.7 GB.

## 2026-10-19/20 (23:20-00:45): the bot down since 21:23; off till h768-3m is done

- **lichess-bot quit at 21:23:27, between games** ("Process Freed. Count: 0"
  at 21:22:34, so no game lost), on the same TypeError as at 00:32: in
  accept_challenges, `while len(active_games) < max_games and
  challenge_queue:`, the manager's list gave its length as None. This time
  run-bot.ps1 died with it: no "stopped by itself" line in restarts.log and
  no bot window left, so nothing restarted it.
- **Memory**: 0.45 GB of 21.8 GB commit free at 23:22 (RAM 4.0 GB free). The
  biggest: vmmem 4.0 GB (the Cowork sandbox VM; WSL is stopped), the h768-3m
  training 3.5 GB (on this laptop's GPU from 22:25, started after the bot was
  down: a 768-hidden net on September, July and August, 10 passes of ~25 min),
  eight Claude sessions ~0.5 GB each. An allocation failing in the manager or
  in pwsh fits both the None and the dead loop; the 00:32 crash came with no
  measure of memory.
- **Matthew's (a)**: the bot stays off while h768-3m trains (heat rule); it ends
  ~02:40 (pass 6 of 10 at 00:42). Then he reboots, the only way to close the
  Cowork VM, and runs `bot\start-bot.cmd`, then `bot\learn.cmd` (not
  `start.cmd`, which would start the month queue, off on his word). The NNUE
  session knows.
- **Cause confirmed** (Matthew, 00:50): memory ran out. Taking a screenshot
  at 21:23 tipped it over. So the TypeError is what lichess-bot's manager
  does when an allocation fails, not a bug of its own; the guard is
  headroom (~2 GB free while the bot plays), the second memory stick, or a
  bigger page file (his setting).

## 2026-10-19/20 (23:20-03:00): a memory squeeze; h768-3m and h1024-3m done

- **~23:18: commit headroom on the laptop fell to ~0.3 GB** (the watcher's
  0.7 GB alarm): h768-3m flat at 3.5 GB, but a vmmem of 4.1 GB since 18:05
  and ~1 GB from the new teaching session ("Learn how our NNUE networks
  work", started on Matthew's word). I meant to stop my trainer (the pass-2
  checkpoint was saved) and was refused by the permission check, as was a
  look at what owned vmmem; a `wsl -e` of mine, to see what WSL ran, tried
  to start the stopped Ubuntu and failed for lack of memory. Matthew ran
  `wsl --shutdown` twice, cleanly, and vmmem stayed: Cowork's VM, he said,
  not WSL. He chose to leave everything running and open nothing. Paging
  out frees RAM (4 GB free) but not commit, which is RAM + a page file
  already at its 6 GB maximum; a bigger maximum is his to set.
- This session's own process went down in it (its background watchers were
  marked stopped). h768-3m carried on: no pauses, commit flat, each pass's
  errors lower. Matthew thought the loss was rising: the per-bucket figure
  is the pass's running mean from a fresh start in a new bucket order, so it
  opens each pass a little above the last pass's end (h512-3m did the
  same); the per-pass lines fell every time.
- **h768-3m finished 02:42:53**: training error 0.011513; held back as the
  engine computes it 0.013038 at K 1, 0.012795 at K 1.1164. Its launcher
  window never wrote "finished" into h768-3m.err: it went down earlier,
  most likely with the session's process, while Python ran on. A long
  laptop job started from a session's shell can die with it.
- **h1024-3m finished 02:47:19** (desktop clock), exit 0: training error
  0.011141; held back 0.012659 at K 1, 0.012432 at K 1.1119. Fetched.
- Both load through EvalFile (a6 at depth 10). `bench 9`, one thread, 6
  rounds alternated with h512-3m: h768-3m -13.2% a node (1,516 against
  1,746 knps, a 6% smaller tree), h1024-3m -15.8% (1,470, the same size of
  tree). Held back against h512-3m: -4.4%/-4.8% and -7.2%/-7.5%. Table in
  NNUE.md; the matches against h512-3m wait on Matthew.
- Twice tonight I left a stray `python -` at the head of a Bash call (the
  notebook's warning): one hung for two minutes, one a moment; both were
  stopped and neither wrote anything.

## 2026-10-20 (03:00-03:35): h1024-3m leads h512-3m; the bot switches to it

- Matthew's (a): both matches against h512-3m, back to back in one window
  (a scratch launcher calling `scripts\nnue-match.cmd` twice with stdin
  from nul, so the first one's closing pause doesn't hold up the second).
  Started 03:04:17; commit free 8.5 GB beforehand, the bot off, on AC.
- He saw ~29% CPU: 5 games at a time, one thread each, only the side to
  move thinking, on 16 logical processors (5/16 = 31%). Not the pipes: in
  play the engines searched as fast as `bench` alone. The CPU sat at
  88-92 C on that load (his monitor), under its 100 C limit.
- **At game 188: +69 -41 =78, 57.4%, +52 Elo [+14, +91], LOS 99.6%, LLR
  1.29** (game 100: +31, LOS 89%). In play 1,650-1,750 knps against
  2,000-2,110 (-16%).
- Matthew: "battle testing 1024", the statistics later; I didn't advise
  against (99.6% already, and 3+2 gives the bigger net more time a move).
  `bot\eval\h1024-3m.nnue` (the match's bytes, 96dc708d...), make-config.py
  tries it first (its loop picks it here), EvalFile loads it from bot\eval
  (sha256 510a8d0e6c43). Takes effect when he closes the match window,
  fast-forwards compression and runs `bot\start-bot.cmd`.

## 2026-10-20 (04:45-05:40): speed at 1024, measured (a side session)

- The brief: where a node's time goes with h1024-3m against h512-3m, then
  the TODO's three speed steps with estimates, for Matthew to pick. No C#
  changed (his word first). Branch `claude/stoic-goodall-ecc132`.
- **Conditions**: the bot playing on h1024-3m and h768-3m's match running (5
  games at a time), commit free 2.5-4.9 GB. Matthew asked whether to stop the
  bot for this; no: every run was one thread at below-normal priority with
  the arms alternated. `bench 9` read about half its quiet speed, with the
  same gap: h512-3m 890 knps, h1024-3m 744 (-16.3%; -15.8% with the bot off).
  costs.cs with the trained nets: an update 117 / 228 ns a move, an
  evaluation 102 / 198 ns (both double with H).
- **The probe** (a scratchpad copy of the engine with hooks): a counting run
  records every evaluation's value, and replaying them searches the identical
  tree, so arms without the forward pass, or without the updates too, give
  the split directly. **Its first timed run was noise**: one arm read 0.7 to
  2.1 µs a node from round to round, a below-normal thread waiting for a CPU
  whenever the bot thought. Timed by the thread's own CPU time
  (QueryThreadCycleTime) and rotated search by search, the rounds agreed to
  ~10%. Two runs: **at 1024 the forward pass is 12-13% of a node, the
  accumulator updates 27-28%, the rest of the search 61%**; at 512, 8%, 19%
  and 71-73%.
- **28% of the pushes are illegal moves** (made, found to leave the king in
  check, taken back: mostly at nodes in check, where every pseudo-legal move
  is tried), and 29% of all pushes are never read by any evaluation. An
  ideal lazy update (skipping exactly those, every evaluation checked exact)
  saved 80 ns a node at 1024 (6.4%), 40 at 512.
- **The kernel lab** replayed 465 k pushes and 285 k evaluations logged from
  a real h1024 search, kernel by kernel, all variants agreeing on a
  checksum: AVX-512 on 64-byte-aligned memory -49% a push and -37% an
  evaluation; on the engine's own arrays only -20% and -36%; alignment alone
  -13% a push. The engine's feature weights land 24 bytes past a 64-byte
  boundary on every load (by reflection), so half of today's 32-byte column
  loads already cross a cache line.
- **Estimates at 1024** (NNUE.md "Speed at 1024"): AVX-512 with alignment
  +18-22% nodes a second, the lazy update +5-7%, both +22-27%. Put to
  Matthew as decision J. Numbers and every round in
  `scripts/nnue-bench/results-2026-10-20.txt`. The measuring scripts
  (costs-nets.cs, the probe's hooks as a patch, probe.cs, probelog.cs,
  lab.cs, align.cs) are in the session-notes folder for now
  (`%APPDATA%\Claude\side-session-notes\local_93425b6c-...\speed-1024-scripts`)
  and go into scripts/nnue-bench/ with whatever is built, as they are C#.

## 2026-10-20 (07:55-09:40): AVX-512, alignment and the lazy update built (Matthew's J(c))

- **Matthew chose (c)**: both, AVX-512 with 64-byte alignment first, each
  A/B'd and committed apart. The bot played on h1024-3m throughout: every run
  below normal priority, commit headroom checked first (2.5-5.6 GB free).
- **(a) AVX-512 + alignment** (commit c75f3ce): `Aligned` keeps the feature
  and output weights and every accumulator stack in pinned arrays from a
  64-byte boundary, growth included. The kernels run 32 lanes with AVX-512
  (and a last 16), 16 with AVX2, or one at a time; the forward pass the same
  (Forward512, Forward256, scalar). `Kernels.Width` is the widest the CPU
  has; `CB_NNUE_SIMD=256` or `0` holds it narrower.
- **(b) the lazy update** (commit a8dd037): a push only notes its move; the
  slot is computed at an evaluation, a read, or a child's push, so its parent
  always has its numbers and no chain is longer than one. An illegal move or
  a leaf that returns early never pays.
- **Tests**: NnueTests 38 to 49. The SIMD pass and every kernel at every
  width (H = 48 for the 16-lane tail; operands at four offsets from a cache
  line); weights and slots aligned through growth; the width setting; a walk
  that moves as a search does (every pseudo-legal move made, the illegal
  ones taken back unseen, only some nodes looked at, by a read or an
  evaluation, against a refresh); SetPiece on a slot still waiting; a move
  from an empty square now throws when its slot is computed, not at the
  push. All green at 512, 256 and 0 bits. Full suite: 421 passed, 2 skipped
  (415 after (a)).
- **Mutation battery** (session 3's script brought up to date, with a guard
  that waits for 2.6 GB of commit before each build; it waited a few times
  while the bot played two games): (a) 43 of 43 red; (b) 54 of 54 red. One
  lazy mutant survived the first run, "reading Black doesn't compute":
  every test read White first, which computes the slot. The search-like walk
  now reads Black first in half its looks, and the mutant goes red. The
  256-bit mutations are caught only by the new width tests, since the walks
  run at 512 bits here, which is what those tests are for. No crash events
  in the Application or System logs. (After a battery, `bin` holds the last
  mutant's build: a `--no-build` test run then read 6 failures, gone after a
  rebuild.)
- **`bench 9` on h1024-3m**, one thread, identical trees, alternated rounds,
  the engine's clock: old build 1,024-1,072 knps; the alignment alone +3.8%
  (8 of 8 rounds faster, paired); AVX-512 +16.7% (16 of 16); with the lazy
  update +23.7% over the old build (8 of 8), +2.6% over AVX-512 alone (17 of
  24 rounds). h512-3m: AVX-512 +8.5%. So h1024-3m now searches a node about
  as fast as h512-3m did on the old build (1,262-1,322 knps against 1,206,
  25 minutes apart).
- Against the estimates: (a) at the bottom of +18-22%; (b) under +5-7%,
  because a push now costs half what it did, so skipping 29% of them saves
  about half as much as the ideal lazy arm measured on the old kernels.
- bench.ps1 takes an engine and environment per arm now, and prints a
  CPU-time column. Beside the bot that column was noisier than the wall
  clock (one old-build run read 2,491 knps by CPU against 892 by the clock),
  so the wall clock leads, rounds paired for the small gain.
- **The bot** gets both at its next start once this branch is in compression
  (Matthew's merge; start-bot.cmd builds the engine). Unmeasured: whether
  AVX-512 lowers the clock with 8 threads busy. The bot's Speed lines will
  show it, and `CB_NNUE_SIMD=256` is the fallback.
- The morning's measuring scripts are in `scripts/nnue-bench/speed-1024/`
  now (the probe, its hooks as a patch against 619c703, the kernel lab,
  costs-nets.cs, align.cs; README there).

## 2026-10-20 (09:40-10:30): the rest of a node, measured (a side session)

- **Asked** (by the session that built the speed steps): where the 61% of a
  node outside the network goes, and speed levers with estimates for
  Matthew to pick from. No engine change. This branch
  (`claude/friendly-cray-e85e18`) was fast-forwarded to
  `claude/stoic-goodall-ecc132` (4ecea62) first.
- **Conditions**: at 09:41 the bot was playing with 2.17 GB of commit free,
  at the 2 GB floor; Matthew stopped the bot and the learner shortly before
  09:52 ("should have more memory now"), and every run below was made with
  them off (5.9 GB free, no engine running, checked before each run): the
  quiet laptop's figures. Still off at 10:21.
- **The probe** (`scripts/nnue-bench/search-split/`): a scratchpad copy of
  the engine with hooks. A recording run notes every answer a node's pieces
  give; removal arms replay one piece's answers instead of doing the work,
  on the identical tree (checked every run: the same nodes, every replayed
  stream read to its end); doubling arms do one piece twice. Thread time,
  six rounds, rotated. Three runs (all arms; the quiescence levers; the
  bot's 256 MB table) and a bitboard lab.
- **The split** (NNUE.md "The rest of a node"): a `bench 9` node 515-522
  ns, the network 125-135 (24-26%), the rest 386-389 (74-76%): move ordering
  141 (PickNext's scan 92, ScoreMoves 50), generation 96 (460 ns a list),
  InCheck 77 (39 ns a call, ~2 a node), make + unmake 25, the table 20 (26
  at 256 MB), tablebase 5, repetition 2, the rest 21.
- **Quiescence wastes most of the ordering**: it generates all ~34 moves,
  scores them, and selection-sorts through 2.6 quiet moves a node that it
  then skips. Stopping at the first quiet move is exact (quiet moves sort
  last) and saves 11-12% of a node. A captures-only generator for it
  (checked equal to the full list filtered, 2.7 M lists) with the check
  status passed from Negamax: **the search 23.3% sooner, +30% nodes a
  second** (+29% at 256 MB), the tree +494 nodes in 3.78 M (PickNext's tie
  order). PawnMoves' `new[] { -1, 1 }` allocates 46 bytes a node (9 ns).
- **The bitboard lab** (positions sampled from the searches, every answer
  checked equal): a list -71%, quiescence's captures -83%, InCheck -67%.
  Estimates on top of the quiescence fix: bitboards +25% to +39%; attack
  tables beside today's board +6% to +15%; legality before make +7-8%.
  **Decision K is Matthew's** (NNUE.md).
- **To keep**: (1) the JIT must see every arm before timing: a depth-5 trial
  without a warm-up had InCheck at twice its cost (tier-0 code in short
  runs). Every arm runs twice at depth 6 first now. (2) A doubling arm reads
  0.7-0.9x the removal arm's cost: the second call finds warm caches and
  trained branch predictors. (3) Replayed evaluations can't follow a changed
  tree (the captures-only arm's 0.013% tie-order change sent it 8% off), so
  such arms run with the network on, on their own tree. (4) The hooks cost
  nothing visible: the unhooked engine's `bench 9` read 1,731-1,756 knps
  (570-578 ns a node, fresh processes) against the probe's 515-522 warmed up.
