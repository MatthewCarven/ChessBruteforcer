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
