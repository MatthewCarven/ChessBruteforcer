# TODO / roadmap

If you're asking "where are we?", this is the answer. Top to bottom is
roughly the order of work. Tick items off here and log them in
[WORKLOG.md](WORKLOG.md).

## Now: the session plan (5-piece tables and the depth ladder)

Agreed with Matthew 2026-09-27. One session per step, in order. Any step
may take two sessions: if it does, stop at a clean commit and write where it
stands here. The goal is 5-piece tables built **up a ladder**: solve to a
capped depth, then extend. More disk then buys more depth, and nothing
below is redone. That only works if the edges are right, which is what
sessions 2 and 3 are about.

Shared facts for every session:
- Full test suite: 20 s on Windows on 2026-09-27 (`dotnet test -c Release
  tests/...`); it was noted as ~11 min before. Not sure why: maybe tables
  cached on disk by an earlier run. If it's slow again, run it in the background.
- Regression check: `scripts/measure-tables.sh <cli bin> <empty dir> <log>`
  solves all 36 tables up to 4 pieces (~8 min) and prints each table's time
  and solver memory. Then `cmp` every .cbt against `tables-baseline/`
  (gitignored, Windows copy: the 36 tables from the pre-step-2 solver,
  plus `before.log`, the baseline memory figures).
- Memory is tight: WSL is capped at 7.8 GB, and Windows runs short with the
  Claude app and Cowork VM open. Run one big solve at a time.

### Session 1: finish the solver's memory (step 2) — done 2026-09-27
Merged to main. `BitSet` flags, `uint` queue entries, `SolverMemory` printed
by `solve`, pawnless 5-piece tables allowed (`Tablebase.MaxPieces` = 5).
- [x] The "hang" was the test: it called KQRRvK "six pieces", but it has
      five and no pawn, so it started a real 242 M-slot solve in the test
      host. Now KQRRvKR. `solve KQRPvK` refuses at once.
- [x] Full suite green (242 pass, 1 skipped). 36 of 36
      byte-identical to `tables-baseline`.
- [x] Before/after: solver memory 2,571 -> 1,660 MB (0.65x), no slowdown
      (479 -> 481 s). Table in WORKLOG.
- [x] Projection to 5 pieces: ~2.7 GB process peak (was ~3.5 GB). Fits
      under WSL's 7.8 GB; no need for the rescan fallback.

### Session 2: cap and resume (ladder parts a and b) — done 2026-09-27
- [x] `Outcome.Beyond(N)`: "not settled within N plies" (a longer win or
      loss, or a draw). `Outcome.Better` compares it honestly: it only loses
      to a win within N. `TryProbe` says "not covered" for it, so the engine
      and the match adjudicator never take it for a draw.
- [x] `solve <material> --cap N` (and `verify ... --cap N`): stops after ply
      N; a table solved to less is carried on. Everything beyond N is stored
      as it stood (tentative values read as Beyond). Capped tables are CBT3;
      a table that runs out of work before its cap is complete and CBT2.
- [x] Frontier file `.cbf` (CBF1) beside the `.cbt`: move counts, slowest
      losses, three bit sets, live queue entries past the cap, en passant
      nodes. Deleted when the table completes.
- [x] Edges: a capture into a capped table that answers Beyond is an
      unknown exit (`_hasUnknownExit`): it blocks a loss, not a win. Exact as
      long as the smaller table reaches cap - 1; otherwise the solve throws.
      On extend, the smaller tables are extended first (Tablebase.Get does
      it), then every unknown exit and pending en passant node is probed
      again. Anything that settles lands past the old cap; queuing behind
      the current ply throws, as a guard.
- [x] The proof: all 36 tables laddered 10, 20, 40, 80, 160, end came out
      byte-identical to `tables-baseline` (36 of 36; 684 s against 481 s
      for a straight solve). `scripts/ladder-tables.sh`. Per-table
      statistics in WORKLOG.

### Session 3a: the 50-move rule (ladder part c) — done 2026-09-28
- [x] DTZ tables (`.cbz`, CBZ1, same index as the `.cbt`): results under the
      50-move rule with the count at 0, and plies to the next capture, pawn
      move or mate. A zeroing move is an exit worth only win / draw / loss
      (the count restarts); the solve stops at 100 plies and the rest is a
      draw. `dtz <material>` (stats, cursed wins, blessed losses),
      `dtz <material> verify [stride]`, and `probe` shows both results.
- [x] Pawn tables in slices by pawn placement (with its mirror image), most
      advanced first: each pawn move leads into a slice already solved. The
      slices share the table's arrays for now (see 3b).
- [x] All 36: 36 of 36 consistent (`scripts/dtz-tables.sh`, every 5th
      position checked against its moves). No cursed wins or blessed losses
      anywhere up to 4 pieces; the longest DTZ is KBNvK's 65 plies.
- [x] Syzygy check via the Lichess tablebase API: 178 positions (every
      table's longest DTZ for each side, 3 random each, 4 en passant), all
      agree. `scripts/syzygy-check.py`, results in
      `scripts/syzygy-check-results.tsv`. Lichess reports a checkmated
      position as dtz -1; we store 0.

### Session 3b: slices with their own memory — part A done 2026-09-28
Plan agreed with Matthew: part A (slices, proven on 4 pieces), then part B
(the first 5-piece pawn table) in its own session.
- [x] `SliceIndex`: one pawn placement, every other piece anywhere, no
      symmetry. A placement is never its own mirror image, so one of each
      mirror pair covers the table exactly once. The solver's arrays hold
      one slice (5-piece, one pawn: 33.5 M slots, not 947 M), reused.
- [x] Pawn moves are exits into slices already solved, read from the whole
      table: DTZ restarts its count, mate distances carry on. En passant is
      a plain exit (no nodes). The whole table is in memory, or with a path
      written straight into its file (memory-mapped, `FileStore`).
- [x] DTZ with pawns always by slices; mate distances with pawns by slices
      when solved to the end (capped pawn tables keep the whole-table
      solver; 5-piece pawn tables can't be capped yet).
- [x] Regression: all 36 mate-distance tables byte-identical to
      `tables-baseline` (pawn tables' solver memory ~127 MB -> ~5 MB, same
      time); all 36 DTZ tables byte-identical to the pre-change DTZ
      (`tables-baseline/*.cbz`, made first); the capped ladder still
      byte-identical; tests green.
- [x] Part B (2026-09-28): KRPvKR under the rule, the first 5-piece table
      with a pawn. Promotion targets first: KQRvKR 830 s, KRRvKR 762 s,
      KRNvKR 539 s (peaks 1.5-1.9 GB). KRPvKR: 2,040 s, solver memory
      244 MB (whole-table would be ~5 GB), process peak 3.7 GB (mostly
      mapped files), 1.9 GB file. `dtz verify` on every 1009th position
      (479,339): consistent. Against Lichess: 30 positions over the six
      5-piece tables plus 12 more random KRPvKR, all agree
      (`scripts/syzygy-check-5piece.tsv`; `syzygy-check.py ... --rule`).
      `probe <fen> --rule` answers from the DTZ tables alone.
- [x] KRPvKR's mate distances (2026-09-28, after a hold). KQRvKR 816 s,
      KRRvKR 766 s, KRNvKR 539 s, KRPvKR 2,082 s by slices (solver 263 MB).
      Longest mates: KQRvKR 34 moves, KRRvKR 31, KRNvKR 41 (for the side
      with the lone rook!), **KRPvKR 74 moves (147 plies)**, all confirmed
      on Lichess. **No cursed wins or blessed losses in any of the four**:
      KRPvKR's 147-ply mate still has a pawn move or capture within every
      100 plies (Syzygy: DTZ 65). `verify` on every 1009th position of each:
      consistent. The hold note, for the record:
      **On hold (Matthew, 2026-09-28), part done:** KQRvKR's DTM is solved
      and saved (816 s, peak 1.95 GB; longest mate 34 moves / 67 plies with
      white to move, 35 / 70 plies with black; not yet checked on Lichess).
      Stopped during KRRvKR, nothing half-written. To carry on (Git Bash, in
      the repo; ~70 min, then a few minutes per comparison):

          export CHESS_TABLES=tables
          for m in KRRvKR KRNvKR KRPvKR; do dotnet src/ChessBruteforcer.Cli/bin/Release/net8.0/ChessBruteforcer.Cli.dll solve $m; done
          for m in KQRvKR KRRvKR KRNvKR KRPvKR; do dotnet src/ChessBruteforcer.Cli/bin/Release/net8.0/ChessBruteforcer.Cli.dll dtz $m; done

      The second loop prints each table's cursed wins and blessed losses.
- [ ] Later: 48-square pawns (25% less disk per pawn, but a new file
      format); a cap for 5-piece pawn tables (slices and the ladder
      together).
- [ ] Maybe: split a table with a bishop by the bishop's square colour
      (Matthew asked which pieces are tied to parts of the board). A bishop
      never changes colour, so each half is its own closed table of 32
      squares for that bishop: half the memory per solve, same disk. (A
      promotion to a bishop changes the material, so it's another table
      anyway, and there the new bishop's colour picks the half.)

### Session 4: the first 5-piece tables — done 2026-09-28
- [x] Windows (5.7 GB free once Matthew closed things). KQRRvK DTZ: 796 s,
      solver 2.06 GB, process peak 2.18 GB (projected 2.7), 484 MB file.
      All wins with white to move. (The plan said K+Q+R v K, but that's 4
      pieces.)
- [x] KRBvKR DTZ: 570 s, peak 1.5 GB. DTM up the ladder: capped at 100 in
      570 s (818 MB frontier), extended to the end in 9 s. Longest mate 65
      moves (129 plies); Lichess (Gaviota DTM) agrees. The "59 moves" in the
      literature isn't mate: it's distance to conversion (Syzygy puts that
      position ~116 plies from a capture). **The first cursed wins: 17,440
      (white to move), and 5,400 blessed losses (black to move).**
- [x] Checked against Lichess: both tables' longest cases, three random
      KRBvKR positions (mate distance and DTZ), and the cursed/blessed
      categories. `verify` / `dtz verify` on every 1000th position.
- [ ] The rest of the pawnless 5-piece tables: 60 material sets (20 of
      three pieces v a bare king, 40 of two v one). DTZ alone ~29 GB and
      ~11 h; with DTM ~58 GB and ~22 h. Ask Matthew before filling the disk.
      A script like dtz-tables.sh, restartable (skip tables on disk).

## Done: milestone 1, the foundation

- [x] 6-bit square code, 48-byte `PackedBoard`, FEN and hex in and out
- [x] Tiered checker: meaningless codes, king count, pawns on back ranks,
      promotion-aware material (bishops by square colour)
- [x] `.cbb` board files: add, import, list, export, check, sort + dedupe
- [x] Exact per-tier counter, cross-checked by brute force and by DP
- [x] C# port of the BinaryPossibility core, plus `SuperposedBoard`
- [x] CLI: `ranges`, `check`, `show`, `sample`, `file …`

## In progress: milestone 2, the minimum-spec endgame tree (thesis core)

The plan: take the smallest game that can actually be won, map its whole
tree, record win/loss, and sort it by favourable outcome. Endgames matter
most, so work **backwards from the end** (retrograde analysis, which is how
endgame tablebases are built) rather than forwards from the start.

- King vs king on its own is always a draw, so there is nothing to rank.
  The smallest sets with real wins are **three pieces**: K+Q v K and K+R v K
  (then K+P v K, the first one with promotion in it).
- 3 pieces × 64 squares × side to move ≈ 500k raw positions, small enough
  to solve completely and hold in memory.

Steps:

- [x] Position = board + side to move, castling rights, en passant, clocks
      (full FEN in and out, make / unmake in place)
- [x] Legal move generator (pseudo-legal moves, then filter out moves that
      leave your own king in check). Matches published perft counts on all
      six standard positions, including start position depth 6
      (119,060,324) and position 6 depth 5 (164,075,551)
- [x] Checkmate / stalemate detection
- [ ] Speed: ~13-16M nodes/s with the 64-square array. Bitboards (and
      magic or PEXT sliding attacks) should give 10x+ when search needs it
- [x] "Un-move" generator for going backwards (pawnless: any piece of the
      side that just moved steps back to an empty square)
- [x] Retrograde solver: mates are losses in 0, then ply by ply outward.
      A move into a lost position makes a win, and every move into a won
      position makes a loss. Captures are looked up in the smaller table they
      lead to (solved on demand)
- [x] Store results **indexed, not as boards**: one 16-bit value per
      index = squares of each piece + side to move. `.cbt` files, saved in
      `./tables` and reused
- [x] Commands: `solve KQvK`, `probe <fen>` (outcome + every move ranked),
      `line <fen>` (best play all the way to mate)
- [x] "Sort the tree by favourable outcome": `RankMoves` orders every move
      fastest win first, then draws, then slowest loss
- [x] Verified: every K+Q v K and K+R v K position equals the best of its
      moves (a unique solution given the mates), and the longest mates match
      the known values: KQvK 10, KRvK 16, KQvKR 35, KBNvK 33, KPvK 28
- [ ] Export the principal tree (not just one line) from a position, to a
      chosen depth, as a file the thesis can use
- [x] Symmetry: 8-fold for pawnless tables (the white king in the a1-d1-d4
      triangle, 462 king pairs), 2-fold with pawns (1,806 pairs); adjacent
      kings left out. KQvKR 64 MB -> 7.2 MB. All 36 tables re-solved
      byte-identical to the old solver's; all 36 in 8 min, 1.9 GB -> 427 MB
- [ ] Tighter still: identical pieces in any order are stored twice (KQQvK,
      KRRvK...: 2x), pawns index 64 squares where 48 are possible (1.33x a
      pawn), and the other pieces still get 64 squares each, holes included
- [ ] The 5-piece tables and the depth ladder: see **"Now: the session
      plan"** at the top. Four sessions, briefs there
- [x] Pawns on one side: pawn pushes and un-moves, promotions into the
      other tables, colour swap with the board flipped. KPvK (28 moves,
      76.5% of white-to-move positions won), KQvKP, KRvKP solved; KPvK
      passes the full consistency check plus textbook positions
- [x] Pawns on both sides, with en passant: a double push that allows en
      passant leads to an extra node worth the better of the table value and
      the capture. `probe` applies the same rule to FENs with an e.p. square
- [x] `verify <material> [stride]`: check a solved table against its moves
- [x] Pawn-table symmetry: mirror a-d / e-h files only (2-fold)
- [ ] The 50-move rule: these tables count distance to mate and ignore it.
      Nothing solved so far comes close (KQvKR's longest is 35), but some
      5-piece wins take more than 50 moves. Add DTZ (distance to a capture or
      pawn move) when that matters. Now session 3 of the session plan
- [ ] Grow the material set: KBBvK, KQvKQ, KRvKB, KRvKN … then 5 pieces
      after symmetry

## Next: storing games (ahead of the 5-piece tables)

A game is its start position plus one byte per move: the move's place in the
position's legal moves, **sorted by from-square, to-square, promotion piece**.
The order is set by the rules, not by the move generator, so the bitboard
rewrite can't silently change what old files mean. No position has more than
218 legal moves, so a byte always fits. Most games start from the standard
position and store no FEN. Going back is replay: position n is the first n
moves played from the start.

1. [x] `.cbg` game files: tags, result, move bytes. PGN import (reader: tags,
       comments, variations, NAGs, SAN parsing) and export; `game import`,
       `export`, `list`, `count`. Round trip: PGN in = PGN out
   - [ ] Tags cost more than the moves (590 KB v 165 KB over 5,000 games): a
         shared string table for tag names and repeated values
   - [ ] Speed: ~3,000 games/s, so a Lichess month (~100M games) would take
         ~9 h. Each move generates and sorts the legal moves twice (SAN in,
         code out); once would do
2. [x] Replay to any ply: `game show <file> <n> [ply]` (negative counts back
       from the end); game n is found by skipping the others' move bytes
3. [ ] Position index (Zobrist hash -> game, ply): from a position, every
       game that reached it, and where each came from and went
4. [ ] Repetition (Matthew's idea): repetition rules remove no *positions*,
       but they make the number of *games* finite (the 75-move and fivefold
       rules cap a game at roughly 8,849 moves). Measure it two ways:
   - [ ] A perft that refuses repeated positions: how much each depth loses
         (expect almost nothing early; the first threefold needs 8 plies)
   - [ ] On stored games, via the position index: every repetition, and
         whether "the same piece making the same move back and forth"
         predicts one (a detector, not the rule: FIDE counts positions)
   - [x] Within one game: `game grade` counts repeats (FIDE's notion of a
         position) and back-and-forth "shuffles" per game
   - [x] Tune the shuffle rule: Matthew chose "only while nothing is
         happening" (10+ plies without a capture or pawn move). Games flagged
         by shuffling alone went from 9,937 to 97; time wasters 14% to 6%
   - [x] Cycling (Matthew, 2026-09-27; measured 2026-09-28 with `game idle`,
         five months, 741k games). Idle moves: 19% straight back, 8%
         cycles, 73% fresh arrangements. Games with 3+ cycle moves: 1.1%,
         and 97% of them are already flagged (mostly by the 20-ply quiet
         stretch). A cycle rule would add 234 games of 741k: **not
         warranted**. The quiet-stretch rule doesn't care what shape the
         stalling takes, so an orderly walk can't slip past it, which is the
         50-move rule's own logic. (An orderly staller's moves read as
         "fresh" until its arrangements run out, so revisits can't measure
         how orderly a walk is; that would need a different measure.)
         The original note: a smart staller doesn't send one
         piece back and forth, it walks 3+ pieces round separate loops until
         the unused combinations run out. The shuffle rule only sees "straight
         back" (Nf3, Ng1), so a loop of 3+ squares never trips it. Estimate:
         3 pieces on 3-square loops = 27 arrangements; each may occur twice
         before threefold, so 54 moves, past the 50-move rule. Repetition
         alone can't end stalling; the 50-move rule does (why it's the ladder's
         cap). To measure: a cycle detector (a piece back on a square it left
         within the quiet stretch), and how many time wasters it adds.
         Matthew: such cycles can look random but be orderly. A Gray-code
         walk (one piece, one step, each move) visits all 27 arrangements
         without repeating one, so no single piece shows a pattern. Detect
         it by the side's whole arrangement recurring, not per piece.
         Matthew: "like the snake solver, but with a start and an end". The
         arrangements are cells, a move joins neighbours, and the longest
         stall without a repeat is a Hamiltonian path from the start
         arrangement to the end one (a snake AI follows a Hamiltonian cycle).
         Could be measured: for a real game's quiet stretch, how close its
         walk comes to covering the arrangements it had
5. [x] Real games: Lichess 2013-01 (121,332) imported, graded (early kill /
       efficient / time waster), and measured as a tree (89% of plies are
       distinct tree nodes, 86% distinct positions; median game new from
       ply 7). 2013-02 added on top: 87.7% new, median new from ply 8, so
       doubling saves ~1.3%.
   - [x] Five months (Jan-May 2013, 741k games): new tree nodes 89.0 ->
         86.6%, median split point ply 7 -> 9. Opening (first 12 plies) new
         39 -> 27%; <= 6-piece positions 93 -> 84% new, 0.3% of games arrive
         somewhere known; <= 4-piece 84 -> 64% new, 6% arrive known
   - [ ] A recent month (tens of millions of games) is the real test of the
         curve, but needs hours to import and several GB for the hash sets.
         A disk-backed or sorted-file version of GameTreeStats first
6. [ ] Later: where each game enters table territory, and whether it played
       perfectly from there (the ranked-move list scores each move); import
       a Lichess database month to see how it scales

## Then: milestone 3, compete (openly, as software)

Always play as a declared engine, only in software competition. The steps
build on each other: each stage has to be solid before the next.

### 3a. A playing engine (in progress)

- [x] Zobrist hashing (incremental, checked against recomputation)
- [x] Search: iterative deepening, alpha-beta / principal variation search,
      transposition table, null-move pruning, late-move reductions, check
      extension, quiescence search on captures, killer and history move
      ordering, mate-distance pruning, repetition and 50-move draws
- [x] Evaluation: material + piece-square tables (Michniewski's simplified
      evaluation), king table blended from middlegame to endgame, bishop pair
- [x] Our own endgame tables as perfect knowledge, probed only if already on
      disk (never solved mid-game). At the root with every reply covered,
      the engine plays the table's move outright
- [x] Time management: per-move budget from clock, increment and moves to go;
      no new depth after half the budget, hard stop at the limit
- [x] **UCI protocol**: `uci`, `isready`, `ucinewgame`, `setoption` (Hash,
      EndgameTables), `position`, `go` (depth, nodes, movetime, wtime/btime,
      winc/binc, movestogo, infinite), `stop`, `quit`, plus `d` and `eval`
- [x] Engine executable `ChessBruteforcer.Engine` with `bench [depth]` (speed)
      and `selfplay [games] [ms]` (robustness: plays itself to the end)
- [x] Tests: mates in 1, free / poisoned material, stalemate, UCI commands,
      movetime, and search mate distances checked against the K+R v K table
- [ ] Solve the remaining 4-piece tables (two pieces v bare king: KQPvK,
      KRPvK, KQQvK, …): `scripts/build-tables.sh` does all 30. Without them the engine may "simplify" into a table it
      has, e.g. give up its queen for a proven K+P v K win, which is correct
      but slow
- [ ] Speed: ~550k nodes/s. Bitboards plus generating only legal moves
      should give several times more
- [ ] Evaluation: pawn structure (passed, doubled, isolated), king safety,
      mobility, tapered values for every piece, then tune from match results
- [ ] Draw knowledge in search: insufficient material
- [ ] Pondering and multiple threads (later)

### 3b. Local matches

Plan change: cutechess isn't installable in the build environment, so
matches here use our own runner (built on `selfplay`). On your own machine,
cutechess-cli or fastchess work with the engine executable as it is.
Stockfish *is* installable, and at reduced strength it makes a good yardstick.

- [x] Match runner (`ChessBruteforcer.Match`): two UCI engines as separate
      processes, openings played in colour-reversed pairs, a real clock
      with time forfeits, every game ending (mate, stalemate, 50-move,
      threefold, insufficient material, illegal / missing move), optional
      adjudication by our tables, several games at once, PGN output
- [x] Elo difference with 95% interval, LOS, and SPRT early stopping
- [x] `scripts/match.sh`: snapshot a build, play it v Stockfish at a set Elo
      or v another snapshot
- [x] Match log: MATCHES.md
- [x] First yardstick: v0.1 beat Stockfish-1500 18-2; v0.2 v Stockfish-2000
      11-9. Roughly 2000 on Stockfish's UCI_Elo scale (wide error bars)
- [x] Time forfeits traced to first-time endgame-table reads from a slow disk
      mid-search; tables now preload on `isready`, plus a Move Overhead option
- [ ] More games per measurement (hundreds, not tens) once changes are small
- [ ] Gauntlet against other open-source engines of known CCRL rating

### 3c. Lichess bot account

- [ ] Create a separate account and upgrade it to a BOT account (permanent,
      labelled as a bot, only plays via the Bot API)
- [ ] Run it with the open-source lichess-bot bridge, which speaks UCI to
      the engine
- [ ] Decide what challenges to accept (time controls, bots only or humans
      too), and a machine to host it
- [ ] Profile page says plainly what it is: engine name, author, that it
      is software

### 3d. Engine rating lists and tournaments

- [ ] Public release: a versioned build plus source or binary, and a
      licence, since rating lists test engines they can download and run
- [ ] Submit to CCRL (and similar lists) once it is stable and doesn't crash
      or lose on time
- [ ] Later: engine tournaments (e.g. TCEC entry is by application once
      an engine is strong enough)

## Later: tighter null-space tiers

Each of these is a necessary condition for a legal position that is still
cheap to check:

- [ ] Kings on adjacent squares (impossible)
- [ ] Side to move cannot capture the enemy king (needs move generation, see above)
- [ ] Promotions need captures: a pawn has to get past the enemy pawn on its
      file, and a pawn that changes file captured something. Bound
      promotions and pawn file changes by the opponent's missing pieces
- [ ] Pawn structure: e.g. doubled pawns need a capture per extra pawn on a file
- [ ] Bishops trapped behind unmoved pawns (e.g. a bishop on c1 with b2 and
      d2 pawns at home cannot have left, and cannot be a promoted piece either)
- [ ] Extend `RangeCounter` to count each new tier exactly, or estimate it by
      sampling when an exact count is out of reach
- [ ] Castling rights and en passant in the spare bit, with their placement
      rules (a rook with rights must be on its corner, the king on e1/e8, an
      en passant pawn on rank 4/5 with empty squares behind it)

## Later: storage at scale

- [ ] External-sort `dedupe` (merge sorted chunks) for files bigger than RAM
- [ ] `file merge a.cbb b.cbb` for sorted-file union
- [ ] Benchmark compression of sorted vs unsorted `.cbb` (zstd, NTFS
      compression)
- [ ] Storing *games*: now its own section, "Next: storing games" above

## Later: superposition

- [ ] Count tier 2+ completions of a partially collapsed `SuperposedBoard`
      (at the moment only tier 1 is counted per square)
- [ ] Weighted superposition over *pieces* rather than bits (e.g. "this
      square is a knight or empty"), which fits the checker far better than
      raw bits
- [ ] Port the rest of PythonBinaryPossibility if it turns out to be useful
      (trees, glitch, entropy tools)
