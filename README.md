# ChessBruteforcer

It's called a bruteforcer, but the point is *not* to brute-force: it's to
work out how small the space of real chess positions is compared with the
space of bit patterns that could store one, and to throw away the "null
space" as cheaply as possible.

Every position is stored in **48 bytes**: 64 squares × 6 bits.

```
bit 5      bit 4     bits 3..1      bit 0
occupied   colour    piece type     spare
           0=white   1=P 2=N 3=B    reserved (castling / en passant, later)
                     4=R 5=Q 6=K
```

Only 13 of the 64 codes mean anything: the all-zero empty square plus the 12
pieces. Squares are indexed a1 = 0 … h8 = 63 and packed as one big-endian
bit stream, so two positions are equal exactly when their 48 bytes are equal.

## Quick start

Needs the .NET 8 SDK.

```
dotnet test
dotnet run --project src/ChessBruteforcer.Cli -- ranges
dotnet run --project src/ChessBruteforcer.Cli -- check "4k3/8/8/8/8/8/PPPPPPPP/QQ2K3"
```

## The ranges

`ranges` prints how many 48-byte boards survive each tier of checks. The
counts are **exact**, computed with combinatorics rather than enumeration,
and take about two seconds:

| tier | filter | boards left | keeps 1 in |
| --- | --- | --- | --- |
| 0 | any 384 bits | 3.9 × 10¹¹⁵ (2³⁸⁴) | |
| 1 | every square holds a real code | 2.0 × 10⁷¹ (13⁶⁴) | 2 × 10⁴⁴ |
| 2 | exactly one king per side | 1.5 × 10⁶⁸ | 1,320 |
| 3 | no pawns on rank 1 or 8 | 6.7 × 10⁶⁶ | 22 |
| 4 | material explainable | **1.44 × 10⁴⁹** | 4.6 × 10¹⁷ |

The exact tier 4 figure is 14,423,091,582,949,015,177,507,873,800,760,355,551,992,125,713,728.

For scale, published estimates put the number of *truly reachable* positions
at around 10⁴⁴–10⁴⁶. That estimate also counts side to move and castling
rights, so it isn't a like-for-like comparison. Closing the rest of the gap
is what the later tiers in [TODO.md](TODO.md) are for.

### Why "no two queens" is the wrong rule

Two white queens is perfectly legal: a pawn promoted. The real rule is that
**extra pieces have to be paid for with missing pawns**. For each side:

```
promoted pieces needed = (Q-1)⁺ + (R-2)⁺ + (N-2)⁺ + (light B-1)⁺ + (dark B-1)⁺
promoted pieces needed ≤ 8 − pawns on board
```

Bishops are counted **by square colour**: two light-squared bishops already
mean one was promoted. The naive rule, "never more than you started with",
would give 2.1 × 10⁴⁰ at tier 4. Promotions multiply the space by about
700 million.

## Commands

```
ranges                         exact survivors at every tier
check <board>...               say whether boards are possible, and why not
show <board>                   diagram, FEN and hex for a board
sample [count] [seed]          collapse random boards from superposition and check them

file add <file> <board>...     append boards to a board file
file import <file> <text>      append every FEN / hex line of a text file ('#' = comment)
file list <file>               print each board as FEN (hex if meaningless)
file export <file> <text>      write the boards out as FEN lines
file check <file>              check every board, tallying where each one failed
file dedupe <file> [out]       sort and remove duplicates (in place by default)
file count <file>              number of boards
```

A `<board>` is a FEN (only the placement field is used) or 96 hex digits.

Playing moves uses full positions (side to move, castling, en passant), with
the start position as the default:

```
moves [fen]                    legal moves, and whether it is check, mate or stalemate
perft <depth> [fen]            count every move sequence to <depth>
divide <depth> [fen]           perft split by first move, for tracking down bugs
```

Endgame tables (up to 4 pieces), saved in `./tables` and reused.
`scripts/build-tables.sh` solves all 30 four-piece tables in one go
(restartable, roughly 1-3 hours):

```
solve <material>               solve e.g. KQvK, KRvK, KQvKR and print what it found
probe <fen>                    the outcome, and every move ranked best first
line <fen>                     best play from here to mate
verify <material> [stride]     check every (or every n-th) position against its moves
```

Game files (`.cbg`), one byte per move:

```
game import <file> <pgn>...    append every game in the PGN files
game export <file> <pgn>       write every game out as PGN
game list <file>               one line per game: number, result, length, players
game count <file>              number of games
game show <file> <n> [ply]     game n replayed to a ply (0 = start, -1 = one before the end, default the end)
game grade <file> [examples]   early kill / efficient / time waster, with the sharpest examples of each
game tree <file>...            how much the games share (tree of moves, set of positions); what each file adds
```

## Move generation: `Game/`

`Position` holds the full game state, and `MoveGenerator` produces the legal
moves: it generates the candidate moves, then drops any that leave your own
king in check. The standard way to prove a move generator is **perft**: count
every move sequence to a fixed depth and compare with published numbers.
All six standard test positions match. They cover castling through check,
en passant pins, and underpromotion, and include start position depth 6
(119,060,324 sequences). A release build runs at about 13–16 million
positions a second.

## Board files (`.cbb`)

A board file is nothing but 48-byte boards back to back: no header and no
separators.

- Board *n* is at byte offset 48·*n*, and the count is the length ÷ 48.
- Files concatenate with `cat`.
- Identical positions are identical bytes, so `dedupe` is just sort + unique.
- Sorted files put similar boards next to each other, which is what folder
  compression (NTFS/btrfs/zstd) feeds on.

Meaningless boards round-trip too, so a file can hold raw samples from the
superposition as well as real positions.

## Game files (`.cbg`): `Records/`

A game is its tags, its result, and **one byte per move**. Each byte is the
move's place in the position's legal moves, sorted by from-square, then
to-square, then promotion piece. That order comes from the rules, not from
the move generator, so a faster generator can't change what an old file
means. No position has more than 218 legal moves (the test suite checks the
record holder), so a byte always fits. The start is the standard position
unless the tags carry a FEN. Going back to any earlier position is replay:
play the first *n* moves. `game show` does exactly that, printing the
board, the FEN, the move that led there, the one played next, and the whole
game with a `|` where the position sits. Fetching game *n* jumps over the
games before it without decoding their moves.

`game import` reads PGN as it turns up in the wild. It keeps the tags and
the main line, and drops comments, variations, NAGs and `%` lines. It
accepts `0-0`, `e8Q` for `e8=Q`, `e.p.`, and annotations like `!?`. What it
writes back is standard PGN with the tags in their original order, so a
file this program wrote reads back and writes out byte for byte. The tests
check that, and so did 5,000 games through the command line. Import runs
at about 3,000 games (100,000 moves) a second.

For scale: in those 5,000 games the moves took 165 KB and the tags 590 KB,
so the tags cost more than the game. A shared string table for tags is the
obvious next saving.

### Real games: Lichess, January 2013

The [Lichess database](https://database.lichess.org) publishes every rated
game under CC0. The first month, 121,332 games (92.8 MB of PGN), imports
with no errors in about a minute to 43.5 MB. Of that, 8.2 MB is moves and the
rest is tags. Exported and imported again, the file comes back byte for
byte. Downloads go in `games/`, which git ignores; Python 3.14's
`compression.zstd` unpacks the `.zst` without installing anything.

`game grade` sorts games into three styles (Matthew's): **early kill** (won
by mate or resignation within 25 moves each), **efficient** (won later
without marking time), and **time waster** (marked time on the way, won or
drawn). "Marking time" means any of:
- a 20-ply stretch with no capture or pawn move;
- 3+ moves that send a piece straight back where it came from *while nothing
  is happening*, meaning 10+ plies into such a stretch (Matthew's rule, so a
  piece retreating because something just happened doesn't count);
- a repeated position.

Lost-on-time games are set aside, because the clock decided them.

| style | Jan 2013 | share | avg plies | by mate | avg rating | Feb 2013 share |
| --- | --- | --- | --- | --- | --- | --- |
| early kill | 27,493 | 22.7% | 34.8 | 38% | 1570 | 23.0% |
| efficient | 47,253 | 38.9% | 78.2 | 45% | 1607 | 39.6% |
| time waster | 7,101 | 5.9% | 117.0 | 39% | 1607 | 6.0% |
| clean draw | 1,602 | 1.3% | 102.5 | 0% | 1599 | 1.2% |
| clock | 37,883 | 31.2% | 66.2 | 0% | 1615 | 30.1% |

74% of time wasters still won, in both months. Counting every shuffle,
instead of only idle ones, had flagged 16,941 games, 9,937 of them for
shuffling alone (mostly ordinary endgame king moves). With the idle rule
that's 97. Early kills come from players about 35-45 points weaker. The
two months agree to within a point on every share.

`game tree` measures how much games share. As a tree of moves (shared
openings stored once) January's 8.16 M plies need 7.26 M nodes, **89%**. As a
set of positions (transpositions merged too) they need 7.05 M, **86%**. Games
leave all earlier games' paths early: the median game is new from ply 7,
and 90% are new by ply 11.

Given several files, `game tree` adds each on top of the ones before, which
shows the returns diminishing. Five months, January to May 2013, 741,349
games:

| month, added on top of the earlier ones | games | new tree nodes | new positions | median game new from |
| --- | --- | --- | --- | --- |
| Jan | 121,332 | 89.0% of its plies | 86.4% | ply 7 |
| Feb | 123,961 | 87.7% | 84.5% | ply 8 |
| Mar | 158,635 | 87.4% | 83.9% | ply 8 |
| Apr | 157,871 | 86.9% | 83.2% | ply 9 |
| May | 179,550 | 86.6% | 82.8% | ply 9 |

Six times the games moved the split point two plies deeper, and saved each
new month only a couple of percent more. Most of a game is its own. The
report also looks at the two ends of the game:

| month | opening (first 12 plies) new | games reaching ≤ 6 pieces | their positions new | arrive where an earlier game had been | games reaching ≤ 4 pieces | their positions new | arrive known |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Jan | 39.2% | 9.1% | 92.6% | 0.0% | 4.3% | 84.2% | 1.0% |
| Feb | 32.4% | 9.0% | 89.0% | 0.1% | 4.3% | 74.8% | 2.1% |
| Mar | 30.0% | 9.2% | 87.1% | 0.1% | 4.2% | 69.9% | 3.1% |
| Apr | 28.3% | 8.9% | 85.1% | 0.2% | 4.2% | 66.4% | 4.4% |
| May | 27.1% | 9.0% | 84.2% | 0.3% | 4.1% | 63.7% | 6.2% |

- **The opening is solidifying.** By May, 73% of opening moves were already
  in the tree, and the new share falls every month.
- **Six-piece endgames are not.** 99.7% of games that get there arrive in a
  position no earlier game reached, and 84% of those positions are still
  new. The six-piece space is far too big for samples to cover.
- **Four-piece endgames are converging.** Only 64% of positions are new and
  6% of games arrive somewhere known, rising fast. That space (33.5 M slots
  a table) is small enough that games start meeting, and our tables already
  know all of it exactly.

So the beginning of a game can be learned from samples, while the end has
to be solved: sampling covers the first dozen plies, and tables cover the
last four or five pieces.

Repeat games (identical move for move) number 6,216 across the five months,
many of them the same traps and early resignations.
At this scale the tree is worth more as a map than as storage: a node needs
a link to its parent, which costs more than the 1 byte a move takes in a
flat file. Positions are compared as FIDE's repetition rule compares them:
the en passant square counts only when a capture there is legal.

## Solved endgames: `Endgame/`

This is the thesis core: take the smallest games that can actually be won,
solve every position, and sort each position's moves by how good they are.
King vs king is always a draw, so the first real ones are K+Q v K and
K+R v K.

The solver works **backwards from the end** (retrograde analysis, as
tablebases do). Checkmates are losses in 0 moves. Then, one ply at a time:

- a position with a move into a lost position is a **win**, as fast as possible;
- a position whose every move leads into a won position is a **loss**, as slow as possible;
- anything never reached is a **draw**.

Captures leave the table, so they are looked up in the smaller table they
lead to, which is solved first on demand (K+Q v K+R needs K+Q v K and
K+R v K, which need K v K).

Positions are stored **by index, not as boards**: the squares of each piece
plus the side to move make a number, and the table is one 16-bit result per
number. `probe` ranks every move fastest win first, then draws, then the
slowest loss. `line` follows the top move all the way to mate.

| table | positions | solve time | longest mate | known value |
| --- | --- | --- | --- | --- |
| K+Q v K | 0.5 M slots, 1 MB | ~2 s | 10 moves | 10 ✓ |
| K+R v K | 0.5 M slots, 1 MB | ~2 s | 16 moves | 16 ✓ |
| K+Q v K+R | 33.5 M slots, 64 MB | ~110 s | 35 moves | 35 ✓ |
| K+B+N v K | 33.5 M slots, 64 MB | ~85 s | 33 moves | 33 ✓ |
| K+P v K | 0.5 M slots, 1 MB | ~6 s | 28 moves | 28, from memory (76.5% of white-to-move positions won) |
| K+Q v K+P | 33.5 M slots, 64 MB | ~7.5 min* | 28 / 29 moves | |
| K+R v K+P | 33.5 M slots, 64 MB | ~4 min* | 43 moves (the pawn side, after promoting) | |
| K+P v K+P | 33.5 M slots, 64 MB | ~5 min** | 33 moves | all 14.9 M legal positions verified |

\* including the other 4-piece tables its promotions lead to (a black pawn
can become a queen, rook, bishop or knight).
\** once those tables exist; from nothing it also builds K+B v K+P, K+N v K+P
and their sub-tables.

**Pawns.** A pawn push stays in the table, while a promotion, like a
capture, changes the material and is looked up in the table for the new
piece. A pawn un-moves one square back, or two back to its starting rank.
When the stronger side is black, the position is looked up with colours
swapped *and the board flipped*, so pawns still run up the board.

**En passant.** Tables store positions *without* an en passant right,
because the index has nowhere to record the previous move. When a double
push hands the opponent an en passant capture, the solver gives the position
after it its own node, worth the better (for the side that may capture) of
the table's value and the capture, or the capture alone if it is the only
legal move. The parent's double push leads to that node instead.
`probe` applies the same rule to any FEN with an en passant square, and
`verify` cross-checks the two.

The tests also check every legal K+Q v K, K+R v K and K+P v K position against the
definition: its value must equal the best outcome over its moves. Given the
mates, that has only one solution, so it proves the whole table.

## The engine: `Engine/` and `ChessBruteforcer.Engine`

A UCI chess engine, so any chess GUI or match runner can play it:

```
dotnet run -c Release --project src/ChessBruteforcer.Engine                   # speaks UCI on stdin/stdout
dotnet run -c Release --project src/ChessBruteforcer.Engine -- --tables tables  # with endgame tables
dotnet run -c Release --project src/ChessBruteforcer.Engine -- bench 8          # speed check
dotnet run -c Release --project src/ChessBruteforcer.Engine -- selfplay 4 100   # 4 games against itself, 100 ms a move
```

- **Search:** iterative deepening alpha-beta (principal variation search)
  with a transposition table keyed by Zobrist hashes, null-move pruning,
  late-move reductions, check extension, quiescence search on captures,
  killer and history move ordering, and repetition and 50-move draws.
- **Evaluation:** material plus piece-square tables (Michniewski's
  simplified evaluation), the king moving from shelter to centre as pieces
  come off, and a bishop pair bonus. It's a baseline to improve on through
  matches.
- **Endgame tables:** with 4 or fewer pieces left, positions are looked up
  in our solved tables (only ones already on disk, never solved mid-game). At the
  root it just plays the table's best move.
- **Checked:** the search's mate distances match the K+R v K table exactly
  on positions it has never seen, which means two independent methods (search
  forwards, retrograde backwards) agree.

About 550k positions a second. In self-play, games run to their proper end
(mates, repetitions). Given the tables, it will happily give up its queen for a
*proven* K+P v K win, which is correct, but a table for K+Q+P v K would find
the faster mate. That's on the TODO.

## Matches: `Match/` and `ChessBruteforcer.Match`

A match runner, in the spirit of cutechess-cli, for measuring strength in games
rather than guessing:

```
scripts/match.sh snapshot v0.3            # freeze the current engine build as engines/v0.3
scripts/match.sh vs-stockfish v0.3 2000   # 20 games v Stockfish limited to 2000 Elo, 10+0.1
scripts/match.sh vs-engine v0.4 v0.3      # snapshot v snapshot, stops early by SPRT
```

- Both engines run as separate UCI processes, like in a real tournament.
- Each opening (16 built in, or `--openings file`) is played twice with
  colours reversed.
- The clock is real (base + increment) and flags do fall. Games end by
  mate, stalemate, the 50-move rule, threefold repetition, insufficient
  material or an illegal move, or are adjudicated by our endgame tables.
- The report gives wins/losses/draws, an Elo difference with its 95% interval,
  the likelihood of superiority, and an optional SPRT that stops once the
  answer is clear.
- Every game is saved as PGN in standard notation, for any chess viewer (or
  the thesis).

Results so far are in [MATCHES.md](MATCHES.md).

## Superposition: `Possibility/`

A C# port of the core of
[PythonBinaryPossibility](https://github.com/MatthewCarven/PythonBinaryPossibility):
`BinaryPossibility`, `BinaryRegister` (counts, entropy, lazy enumeration,
most-likely-first A\*, weighted collapse) and `BinaryRegisterGroup`. Counts
are `BigInteger`, because a board register has 2³⁸⁴ states.

`SuperposedBoard` is a 384-bit register laid out exactly like the packed
board. Collapse any bits and `CountValidCodeCompletions()` tells you how many
completions still put a real code on every square, working square by square
so it never enumerates. `sample` shows why the checks matter: 100% of boards
collapsed from raw superposition fail tier 1, and even among meaningful codes
only about 1 in 1,300 has the right kings.

## Layout

```
src/ChessBruteforcer.Core/
  Piece.cs, SquareCodec.cs      the 6-bit square code
  PackedBoard.cs, Fen.cs        48-byte boards, FEN and hex in and out
  BoardChecker.cs               tiered checks, reporting every violation
  MaterialRules.cs              starting armies and the promotion rule
  BoardGeometry.cs              back ranks and square colours (any size, for testing)
  BoardFile.cs                  .cbb read / write / append / dedupe
  RangeCounter.cs               exact per-tier counts
  Possibility/                  the BinaryPossibility port + SuperposedBoard
  Game/                         Position, Move, MoveGenerator, Perft
  Endgame/                      Material, Outcome, EndgameTable (retrograde solver), Tablebase
  Engine/                       Evaluation, Search, TranspositionTable, UciEngine
  Match/                        San, GameRecord (PGN), players, GamePlayer, MatchStats, MatchRunner
  Records/                      MoveCode (a move as a byte), StoredGame, Pgn (read / write), GameFile (.cbg),
                                GameAnalysis (metrics, styles, tree stats)
src/ChessBruteforcer.Engine/    the UCI engine executable (also bench, selfplay)
src/ChessBruteforcer.Match/     the match runner
scripts/match.sh                snapshots and matches
src/ChessBruteforcer.Cli/       the commands above
tests/ChessBruteforcer.Tests/   xunit
```

## How the counter is checked

The tier 4 count comes from a formula, so it's tested against code that does
the counting the slow way:

- **Brute force**: every one of the 13⁶ boards on 2×3 and 3×2 boards, run
  through the real checker, with army sizes small enough that the promotion
  rule actually matters.
- **Square-by-square dynamic programming** on 4×4 and 3×5 boards, which are
  too big to brute-force, tracking every running piece count.

Both agree with the counter exactly, at every tier, with and without
promotions.

See [TODO.md](TODO.md) for where this is going and
[WORKLOG.md](WORKLOG.md) for what has been done.
