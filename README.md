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

Endgame tables (all 36 up to 4 pieces, and 5-piece ones: KQRRvK, KRBvKR so
far; 5 with pawns by slices, to the end only), saved in `./tables` (or `$CHESS_TABLES`) and reused.
`scripts/build-tables.sh` solves all 30 four-piece tables in one go
(restartable, roughly 1-3 hours):

```
solve <material>               solve e.g. KQvK, KRvK, KQvKR and print what it found (and the solver's memory)
solve <material> --cap N       only to N plies; run it again with a bigger N (or none) to carry on
dtz <material>                 solve under the 50-move rule; wins, draws, losses, cursed wins, blessed losses
dtz <material> verify [stride] check the DTZ table against its moves
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
game idle <file>...            idle moves (nothing happening): sent straight back, cycled, or new?
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

`game idle` asks whether stallers walk pieces round cycles the shuffle rule
can't see (Matthew's question: several pieces in turn, orderly but random to
look at). Over five months (741,349 games), idle moves, meaning 10+ plies
into a stretch with no capture or pawn move, are 19% straight back, 8% back
to an arrangement already had in the stretch by another route, and 73%
arrangements new to the stretch. Games with 3+ such cycle moves are 1.1%,
and 97% of those are already flagged, mostly by the 20-ply quiet stretch. A
cycle rule would add 234 games, so it isn't worth one. The quiet-stretch
rule doesn't care what shape the stalling takes, which is the 50-move rule's
logic too.

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

**Symmetry** (`TableIndex`) gives each position one number up to the board's
symmetries. Without pawns there are 8 (rotations and reflections): the white
king lives in the a1-d1-d4 triangle, and the two kings have 462 placements.
With pawns only the left-right mirror is safe, so the white king lives on files
a-d (1,806 king placements). The images a symmetric position would otherwise
spell out twice are holes, and statistics count each position once per image,
so the totals are over real placements. The solver counts *distinct* children
and predecessors: a symmetric position can reach mirror images of one position
by two different moves, and that is one index.

It was checked the strongest way available: all 36 tables up to 4 pieces
re-solved with symmetry came out **byte for byte identical** to the tables the
old solver made without it, once those were renumbered. All 36 solve in 8
minutes on Matthew's laptop, down from hours, and take 427 MB of disk, down
from 1.9 GB. Tables in the old format ("CBT1") still load, and `upgrade`
rewrites them.

**Solver memory.** `solve` prints what the solver held: its per-slot arrays
(value, moves left, slowest loss: 5 bytes; two flags as bits) and its per-ply
queues at their largest (4 bytes an entry, en passant nodes marked by the top
bit). Flags as bits and 4-byte queue entries took the 36 tables from 2,571 MB
to 1,660 MB of solver memory (0.65x; K+Q v K+R 52.9 -> 32.8 MB, K+R+P v K
204 -> 127 MB), with the same tables byte for byte and no slowdown (479 s ->
481 s for all 36). Projected to a 5-piece table without pawns (242 M slots,
64x a 4-piece one): ~1.2 GB arrays + ~1 GB queues, ~2.7 GB for the process
at its peak (was ~3.5 GB). So `solve` now takes 5 pieces without pawns;
5 with pawns (947 M slots) came later, by slices (below).

**The depth ladder.** `solve <material> --cap N` stops after N plies. Every
win or loss within N is then exact, and everything else reads as *beyond N*:
a longer win or loss, or a draw (draws are only proven once nothing is left
to do). The solver's working state goes to a frontier file (`.cbf`) beside
the table, so `solve <material> --cap M` later carries on from ply N + 1
instead of starting again; plain `solve` carries it to the end. More disk
buys more depth, and nothing below is redone.

The edges are the hard part. A capture into a smaller table that is itself
capped can answer "beyond": that exit might be a draw, so the position can't
be proven lost through it, but it can still be proven won by another move.
That is exact as long as the smaller table reaches the same depth, and the
smaller tables are always extended first. The engine never takes "beyond"
for a draw: to it, the position simply isn't covered.

The check: all 36 tables were solved capped at 10 plies, then extended to
20, 40, 80, 160 and the end (`scripts/ladder-tables.sh`), and came out byte
for byte identical to the tables solved in one go. How much of each table is
settled at each rung (wins and losses within the cap, plus stalemates;
100% means the table finished, so its draws are proven too):

| table | draws at the end | within 10 | within 20 | within 40 | within 80 |
| --- | --- | --- | --- | --- | --- |
| K+Q v K | 6.3% | 31.3% | 100% | | |
| K+R v K | 5.6% | 6.4% | 38.4% | 100% | |
| K+P v K | 32.8% | 1.4% | 30.7% | 65.2% | 100% |
| K+Q v K+R | 3.5% | 4.3% | 33.9% | 58.8% | 100% |
| K+R v K+R | 70.2% | 1.2% | 7.1% | 100% | |
| K+R v K+P | 13.4% | 2.3% | 14.1% | 81.6% | 86.6% |
| K+P v K+P | 33.4% | 1.4% | 22.3% | 64.9% | 66.6% |
| K+Q+Q v K | 1.5% | 94.3% | 100% | | |
| K+B+N v K | 10.3% | 0.4% | 1.7% | 10.5% | 100% |
| K+R+P v K | 1.4% | 24.3% | 82.5% | 98.5% | 100% |

Averaged over the tables that have wins, 23% of the decisive positions are
settled within 10 plies, 59% within 20, 94% within 40 and all within 80.
K+R v K+P is still open at 80 because its longest mate (43 moves, the pawn
side after promoting) runs past 80 plies. K+P v K+P is open at 80 although
its own longest mate is 66 plies: 35,174 of its positions can promote to a
rook into K+R v K+P, and until that table is deeper they can't be ruled out.
That is the edge rule doing its job.

**The 50-move rule (DTZ).** A game is drawn once 100 plies go by without a
capture or a pawn move. So a win only counts if the winner can force a
capture, a pawn move or mate within 100 plies, and again from there, until
mate. `dtz <material>` solves each table that way (a `.cbz` beside the
`.cbt`, same numbering): its distances count to the next capture or pawn move
(a "zeroing" move), not to mate. A zeroing move starts the count again, so it
is worth only win, draw or loss; between them the solve counts plies as
before and stops at 100, and whatever is left is a draw. The rule is the
ladder's natural cap. A DTZ table needs only the smaller tables' DTZ, not its
own mate distances.

Pawn moves stay inside a table, so a table with pawns is solved in slices, one
placement of the pawns at a time, the most advanced first: pawns only go
forward, so every pawn move leads into a slice that is already done.

**Slices with their own memory.** Every table with pawns is solved that way
now, the mate distances too. A slice numbers its own positions (the pawns
fixed, every other piece anywhere, no symmetry: a pawn placement is never its
own mirror image, so one of each mirror pair covers the table exactly once),
and the solver's arrays hold just that slice. A pawn move is an exit into a
slice already solved, read from the whole table: the 50-move count starts
again there, while a mate distance carries on. The whole table is in memory,
or for a big one written straight into its file. For a 5-piece table with one
pawn that is 33.5 M slots of working memory instead of 947 M. On the 4-piece
tables, the pawn tables' solver memory went from ~127 MB to ~5 MB in the same
time, and all 36 tables, both kinds, came out byte for byte the same as
before.

Wins the rule turns into draws are "cursed wins", and losses it saves are
"blessed losses". **Up to 4 pieces there are none**: every table has the same
wins, draws and losses under the rule as without it. The longest stretch
between zeroing moves is K+B+N v K's 65 plies, against the 100 allowed.

| table | longest DTZ (plies) | longest mate (plies) |
| --- | --- | --- |
| K+B+N v K | 65 | 65 |
| K+Q v K+R | 61 | 69 |
| K+R v K+N | 53 | 79 |
| K+Q v K+P | 52 | 57 |
| K+B+B v K | 37 | 37 |
| K+Q v K+N | 37 | 41 |
| K+R v K | 31 | 31 |
| K+R v K+P | 25 | 85 |

(Mate distances from the table of solved endgames below, in plies: a win in
n moves is 2n - 1 plies.) Where a pawn or a capture is on the way, DTZ is
much shorter than the mate; K+R v K+P's mate takes 85 plies, but a capture or
pawn move comes within 25.

All 36 DTZ tables pass `dtz <material> verify` (every value is the best over
its moves), and 178 positions were checked against Syzygy, the standard
published DTZ tables, through the Lichess tablebase: every table's longest
case for each side, three random positions each, and four en passant cases.
All agree. (Lichess reports a checkmated position as dtz -1; we store 0.)
The list is in `scripts/syzygy-check-results.tsv`.

**The first 5-piece tables** (Windows laptop, 2026-09-28). Each is 242 M
slots and a 484 MB file per metric:

| table | metric | time | solver memory | process peak | result |
| --- | --- | --- | --- | --- | --- |
| K+Q+R+R v K | DTZ | 796 s | 2.06 GB | 2.18 GB | every white-to-move position won; longest DTZ 7 plies |
| K+R+B v K+R | DTZ | 570 s | 1.41 GB | 1.51 GB | 41.2% won with white to move; longest DTZ 99 / 100 plies |
| K+R+B v K+R | DTM, capped at 100 | 570 s | 1.44 GB | 1.47 GB | 818 MB frontier file |
| K+R+B v K+R | DTM, extended to the end | 9 s | 1.24 GB | 1.28 GB | longest mate 65 moves (129 plies) |

K+R+B v K+R is where **the 50-move rule first changes results: 17,440
cursed wins** (white to move: won with best play, drawn under the rule) **and
5,400 blessed losses** (black to move). Its longest DTZ sits right at the
edge, 99 plies to win and 100 to lose, as it must: anything longer is a draw.

Its longest mate, 65 moves, is often quoted as 59. Lichess, which serves the
Gaviota mate-distance tables, agrees with 65 (129 plies) for our position.
The 59 is most likely the older "distance to conversion" (to the next
capture), not to mate: Syzygy puts that position ~116 plies, about 58 moves,
from its next capture. Checked against Lichess as well: both tables' longest cases, three
random positions for mate distance and DTZ, and which positions are cursed
or blessed. `verify` and `dtz verify` on every 1000th position: all
consistent.

| table | slots | size (was) | solve | legal positions | white to move: win / draw / loss | longest mate | known |
| --- | --- | --- | --- | --- | --- | --- | --- |
| K+Q v K | 59,136 | 0.1 MB (1 MB) | < 1 s | 368,452 | 100 / 0 / 0% | 10 | 10 ✓ |
| K+R v K | 59,136 | 0.1 MB (1 MB) | < 1 s | 399,112 | 100 / 0 / 0% | 16 | 16 ✓ |
| K+P v K | 231,168 | 0.4 MB (1 MB) | ~2 s | 331,352 | 76.5 / 23.5 / 0% | 28 | 28 ✓ |
| K+Q v K+Q | 3.8 M | 7.2 MB (64 MB) | 11 s | 17.9 M | 41.7 / 57.8 / 0.5% | 13 | 13 ✓ |
| K+Q v K+R | 3.8 M | 7.2 MB (64 MB) | 15 s (was ~110 s) | 19.7 M | 99.0 / 0.8 / 0.2% | 35 | 35 ✓ |
| K+Q v K+N | 3.8 M | 7.2 MB (64 MB) | 14 s | 21.5 M | 99.3 / 0.7 / 0% | 21 | 21 ✓ |
| K+Q v K+B | 3.8 M | 7.2 MB (64 MB) | 14 s | 20.8 M | 99.7 / 0.3 / 0% | 17 | 17 ✓ |
| K+R v K+R | 3.8 M | 7.2 MB (64 MB) | 9 s | 21.6 M | 29.1 / 70.2 / 0.7% | 19 | 19 ✓ |
| K+R v K+N | 3.8 M | 7.2 MB (64 MB) | 9 s | 23.3 M | 48.3 / 51.7 / 0% | 40 | 40 ✓ |
| K+R v K+B | 3.8 M | 7.2 MB (64 MB) | 9 s | 22.6 M | 35.1 / 64.9 / 0% | 29 | 29 ✓ |
| K+B+N v K | 3.8 M | 7.2 MB (64 MB) | 11 s (was ~85 s) | 24.5 M | 99.5 / 0.5 / 0% | 33 | 33 ✓ |
| K+B+B v K | 3.8 M | 7.2 MB (64 MB) | 9 s | 23.8 M | 49.3 / 50.7 / 0%* | 19 | 19 ✓ |
| K+Q v K+P | 14.8 M | 28.2 MB (64 MB) | 41 s | 16.7 M | 99.4 / 0.6 / 0% | 29 | |
| K+R v K+P | 14.8 M | 28.2 MB (64 MB) | 37 s | 18.1 M | 91.4 / 8.4 / 0.2% | 43** | |
| K+P v K+P | 14.8 M | 28.2 MB (64 MB) | 24 s | 14.9 M | 43.2 / 33.4 / 23.4% | 33 | |

Solve times are with the smaller tables each one leads to already on disk.
"Legal positions" counts both sides to move.
\* Half the time the two bishops stand on squares of the same colour, and
that can't mate.
\** The pawn side, after promoting.

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
