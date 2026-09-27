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
