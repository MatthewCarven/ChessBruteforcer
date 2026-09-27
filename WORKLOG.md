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
