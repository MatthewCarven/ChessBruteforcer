# The index over the games: the build plan

Matthew's idea (2026-10-13, extended 2026-10-15): index files that point
into Lichess's own month files, so games and positions can be found and
counted without reading everything; duplicates found by fingerprints and
run lengths; a map from the beginning of a game to its ending. TODO.md
section 1 is the summary and WORKLOG has the reasoning. This file turns the
five sessions into steps, files, formats and checks. Session 1 is written
out in full; the others are outlined, and each gets its full form at the
start of its own session, once the session before has measured what it
needs.

## What a sample says (2026-10-15)

24 frames spread over July 2026 (on the share), read and parsed by a scratch
script: 346,962 games.

| | |
|---|---|
| July 2026 | 29.05 GB, **6,177 frames**, every one with pzstd's note of its size |
| games | 14,457 starting in each frame: **~89.3 M** in the month |
| one frame | 4.7 MB packed, 32 MiB of PGN; unpacked in 90-275 ms (the fit was using every thread) |
| parsing a frame's games in Python | **1.7 s** on one thread |
| walking the frame notes over the share | 165 s (27 ms a read: the share's latency, not its speed) |
| plies a game | median 62, mean 66, longest 580; 99.99% under 256 |
| speeds | blitz 42%, bullet 33%, rapid 16%, classical 0.6%, ultrabullet 0.5% |
| endings | normal 67.3%, on time 31.9%, abandoned 0.7% |
| analysed by Lichess ([%eval]) / with clocks | 9.9% / 99.5% |
| games sharing their exact moves with another | 1.19% of the sample |

What it changes in the plan:

- **A game's row needs no location.** Its number in the file (0, 1, 2, ...)
  is its row number, and the frame table (first game number in each frame)
  says which frame it starts in. Inside the frame, counting `[Event` lines
  finds it in milliseconds, against ~90 ms to unpack the frame. That saves
  the 4-6 bytes a game an offset would cost.
- **Python is enough for session 1.** One read of a month over the share
  takes ~50 minutes (29 GB at ~10 MB/s), and 4 Python processes parse the
  frames in about that time (6,177 x 1.7 s / 4 = 44 minutes), so the share
  is the limit, not the language. Replaying moves (session 2) is different: that needs C#.
- **The frame table is built during the read**, not by a separate walk
  (165 s of small reads over the share).
- **The fingerprint is most of the row.** Per game, packed (each column a
  zstd-compressed array, a stand-in for Parquet):

  | column | bytes a game, raw | packed |
  |---|---|---|
  | plies | 2 | 1.04 |
  | result, termination | 2 | 0.41 |
  | ratings, white and black | 4 | 3.14 |
  | clock base, increment | 3 | 0.36 |
  | start time | 4 | 0.09 |
  | ECO | 2 | 1.05 |
  | flags (evals, clocks) | 1 | 0.13 |
  | **fingerprint** | 8 | **7.97** |
  | **total** | 26 | **14.2** |

  So a month's rows are **~1.27 GB** (0.55 GB without fingerprints), and
  46 months **~58 GB**. The fingerprint is random, so nothing packs it; the
  rest packs to about a quarter.

## Where it lives

`games/index/<month>/` on the laptop while it is built and tried (git
ignores `games/`). The laptop has 68 GB free; the share will be down to the
last 100 GB the month queue leaves when the queue finishes. So where the
whole index lives is decided at the end of session 1 with its measured
size, and before the month queue starts building it (the step after
session 5).

## Session 1 in full: game pointers (July 2026)

**One script, `scripts/index-month.py`**, run with the global Python 3.14
(zstd in its standard library, numpy installed). Subcommands:

```
python -I scripts/index-month.py rows  <month .zst> <dir> [--workers 4] [--frames N] [--hash-all 1]
python -I scripts/index-month.py get   <month .zst> <dir> <game number>...
python -I scripts/index-month.py check <month .zst> <dir> [--sample 1000]
python -I scripts/index-month.py dups  <month .zst> <dir> [--examples 20]
python -I scripts/index-month.py trial <dir>
```

### Step 1: one read of the month (`rows`)

The main process reads the file from the share in order, frame by frame,
feeding every byte to SHA-256 on the way (checked against the `.sha256`
beside the month at the end, as make-book.py does), and hands each frame's
packed bytes to a pool of 4 worker processes (below-normal priority, so the
bot's games come first). A worker unpacks its frame, splits it at `[Event`
lines and parses each game that starts and ends in it; it hands back the
text before its first game and its last game whole. The main process joins
the one to the other, so a game cut by a frame's end is finished from the
next frame's start, and parses it itself (one game a frame). Results are
taken in frame order, so game numbers are the file's order.

The frame walk follows pzstd's notes (a skippable frame holding the next
frame's packed size). 2013-01 and 2026-07 both have them before every
frame; a file without them stops the read with a message rather than
falling back to walking block headers (code no file needs yet). If the
share drops mid-read, wait and carry on from the same byte, as
make-book.py does.

Memory: ~150 MB a worker; the rows go to disk a million games at a time
(~26 MB), as ~2.3 GB raw for July wouldn't fit in memory beside everything
else. Watch the commit headroom first: on 2026-10-15 a test with 8 workers,
started while the sf2 match ran, met a Windows low-memory event (01:17:37)
and failed to unpack a frame; the bot and the match were unharmed. The
Claude app's own processes held 4.8 GB then, and 3.9 GB was free.

### Step 2: the files

**`frames.cbf`**, the frame table, ~150 KB a month:

| field | type | meaning |
|---|---|---|
| magic | 8 bytes | `CBFRAME1` |
| frames | u32 | how many |
| file size | u64 | of the .zst, so a table can't be used with another file |
| file SHA-256 | 32 bytes | the same |
| then per frame: offset | u64 | where its Zstandard frame starts in the .zst |
| packed size | u32 | its length there |
| plain size | u32 | its length unpacked (32 MiB, the last one less) |
| first game | u32 | the number of the first game starting in it |
| first game at | u32 | where that game starts in the unpacked text |

Games starting in frame f: first game of f+1 minus first game of f. A frame
with no game starting in it (possible with a very long game) has the next
frame's first game number and is skipped by lookups.

**The rows**, one per game, in file order. The trial (step 5) decides the
format; the columns are the same either way:

| column | type | from the PGN |
|---|---|---|
| plies | u16 | moves played, counted from the movetext |
| result | u8 | 0 white won, 1 black won, 2 drawn, 3 unfinished (`*`) |
| termination | u8 | a number for each `Termination` value, with the values listed once |
| white elo, black elo | u16 each | `WhiteElo`, `BlackElo` (0 if missing) |
| clock base | u16 | seconds, from `TimeControl` (0 for correspondence `-`) |
| increment | u8 | seconds |
| start | u32 | seconds since the month began, from `UTCDate` and `UTCTime` |
| ECO | u16 | a number for each code, listed once |
| flags | u8 | bit 0 has [%eval]; bit 1 has [%clk]; bit 2 white is a BOT; bit 3 black is a BOT; bit 4 from a tournament (`Event` names one) |
| fingerprint | u64 | see below |

The BOT flags come from `WhiteTitle` / `BlackTitle "BOT"`: the takeover
found ratings of exactly 3000 that are likely new bots, and this sorts them
out properly. Lichess's game IDs are left out (6 random bytes a game, ~0.5
GB a month): fetching a game by number gives its ID from the `Site` tag.

**The game fingerprint**: SHA-256 of the moves exactly as Lichess writes
them in SAN (`e4 e5 Nf3 ... Qxf7#`), joined by single spaces, with no move
numbers, comments, annotations or result; the first 8 bytes, read
little-endian. Lichess writes its own SAN, so the same game always gives
the same text. Two games with the same moves and different endings (one
resigned, one lost on time at the same point) share a fingerprint: same
path, different result. Over all 46 months (~4.1 B games) we'd expect about
half a pair of different games sharing one, in total, so counts can trust
it; a duplicate shown as an example is still confirmed by comparing the
moves.

### Step 3: fetching a game by number (`get`, `check`)

`get`: binary search of the frame table for the frame where game g starts;
read that frame (one ranged read over the share, ~0.5 s) and unpack it;
count `[Event` lines from "first game at" to game g; if the game runs past
the frame's end, read and unpack the next frame too. Print its PGN.

`check`: during the read (step 1), a reservoir of 1,000 game numbers chosen
at random, each with the SHA-256 of its full PGN text. `check` fetches each
by number through the frame table and compares. All 1,000 must match.

### Step 4: duplicate games (`dups`)

Sort the fingerprints (89 M x 8 bytes, 0.7 GB: sorted in chunks and merged
if memory is short), count the runs. Report:

- games in a group of 2+, groups, the biggest groups;
- by plies (how long are duplicated games?) and by ending (abandoned games
  of a few moves are expected to be most of it);
- 20 examples: the group's size and its moves, each confirmed by fetching
  two of its games and comparing the moves.

### Step 5: the trial, Parquet against plain columns (`trial`)

- **(a) Plain columns**: one zstd-compressed numpy array per column plus a
  small JSON note of the columns, the dictionaries (terminations, ECO
  codes) and the row count. Nothing to install.
- **(b) Parquet through DuckDB**: the same rows in one `games.parquet`.
  Needs `pip install duckdb` (1.5.6, a 13.7 MB wheel from PyPI, published
  2026-09-28): **Matthew's yes first.**

Measured on both: size on disk, time to write, and three queries with
their answers compared:

1. games by speed and result (a group-by over all rows);
2. games of 20 plies or fewer with both players 2000+ (a filter);
3. duplicate groups by size (a group-by on the fingerprint).

Keep whichever is smaller and fast enough. DuckDB's pull is that later
questions (the bow-tie map in session 5) become one SQL line each; plain
columns' pull is no new dependency and C# can read them directly.

### Step 6: the report, and done when

`scripts/index-2026-07.txt` (in git, like the other reports): the month's
numbers (games, frames, how long the read took, the parse's speed), the
columns' statistics, duplicates, the trial's table, and the checks.

Session 1 is done when:

- [x] 2013-01 (3 frames) goes through `rows`, and **every** game fetched
      by number matches the straight read; the game count equals Lichess's
      for that month. Done 2026-10-15: 121,332 games, SHA-256 matching,
      121,332 of 121,332 fetched byte for byte; duplicates 0.83% (none
      longer than 20 plies; the biggest group, 80 games, is 1.e4 e5 and
      nothing more); Parquet and plain columns agree on all three queries.
- [ ] July 2026: the frame table covers the file to its last byte; the
      SHA-256 matches; the game count equals counts.txt.
- [ ] `check` passes on 1,000 random games.
- [ ] Duplicate examples confirmed by their moves.
- [ ] The trial's table written up, the format chosen, and where the whole
      index will live decided with Matthew.

Time: writing and testing on 2013-01 first (minutes a run), then one July
read of ~50-60 minutes in its own window (slower while the queue downloads
onto the same share). Disk: ~1.3 GB of rows on the laptop, twice during the
trial.

## Session 2 (outline): the measurement

What the cut-offs should be, measured on July: how many positions each ply
and each piece count holds, and how many of them repeat.

- **Frames into C#.** Python reads and unpacks the frames (as in session 1)
  and streams them to a C# command (`index measure`) over a pipe: a frame
  number, a length, the text (plus the next frame's head, for the game cut
  in two). C# hands frames to 12 worker threads. No new dependency. If the
  pipe turns out to be the bottleneck, the alternative is the ZstdSharp
  package in C# (one NuGet download, Matthew's yes).
- **The position fingerprint** for the index: the engine's Zobrist hash,
  except that the en passant key goes in **only when an en passant capture
  is legal** (as the repetition rule and Polyglot count positions). The
  engine's own hash keys the en passant square after every double pawn
  push, which would split 1.e4 e5 2.Nf3 from 1.Nf3 e5 2.e4, the same
  position. Test: those two lines meet in one fingerprint; a position with
  a legal en passant capture does not meet its twin without one.
- **Counting what repeats needs everything first**, and ~5.9 B positions
  (47 GB as 8-byte keys) don't fit in memory. So: every position up to ply
  30, and every one with 7 pieces or fewer, goes to 256 bucket files by its
  fingerprint's top byte (fingerprint + ply, 9 bytes); then each bucket
  (~100 MB) is sorted and its runs counted on its own. ~27 GB of temporary
  files on the laptop (68 GB free), deleted at the end. A quarter of the
  frames first, to check the shape and the time, then the month.
- **Materials passed through**: per game, the material signatures it
  passes through, counted in a small table.
- **Report** `scripts/index-measure-2026-07.txt`: per ply 0-30 (positions,
  distinct, seen once, 2+, 10+); per piece count 3-7 (the same); the
  materials most passed through. Then the cut-offs: the opening layer's
  depth (12-24 plies) and the endgame layer's piece count (5-7), chosen
  from the sizes.
- **Checks**: positions per ply sum to games + plies; ply 0 is one position
  seen 89.3 M times; ply 1 has at most 20; a few counts redone by a
  straight scan of a few frames; the fingerprint test above.

## Session 3 (outline): the opening layer

- **`opening.cbp`** per month: positions sorted by fingerprint, only those
  within the cut-off depth seen at least twice. Records in 64 KB blocks,
  each Brotli-compressed on its own (as the CBC1 tables are), with the
  blocks' first fingerprints up front and a block cache. A record: the gap
  from the last fingerprint (variable length; ~4 bytes against 8), games,
  white wins / draws / black wins, and the moves played next with their
  counts.
- **Months merged** a record at a time (as make-book.py merges counts), so
  memory stays flat over any number of months.
- **Queries**: `index position <FEN>` and `index line <moves>`: games,
  results, next moves.
- **Each game's opening exit** (the last ply whose position at least 2, and
  at least 10, games share) goes into its row as two columns.
- **A book for the bot** from the layer, in Polyglot format, set against
  make-book.py's book for the same filter on the same month.
- **On trial: Matthew's CSharp-Lookuptables** (`..\Lookuptables`) for the
  by-moves side: a burst trie over each game's moves as bytes makes "every
  game that began like this" one prefix query, and its path compression
  suits the long unique middles. Measured against the sorted blocks on
  size and on 1,000 random lines. What its own results say about disk
  (README section 4): the library's `DiskIndexer` is the Python edition's
  directory tree (a file per 3-byte prefix, fixed-size records), and
  Matthew's 64 GB run found the better design to be a small index in RAM
  pointing into one flat file (0.15 ms a lookup against 0.36 ms, no second
  copy of the data, built in 0.4 s against 24 minutes). That is the shape
  used here throughout: the frame table into Lichess's file, and for this
  trial the trie in RAM over the distinct opening lines, each pointing at
  its run of game numbers in a flat file sorted by line. The trie took ~73
  bytes a key in that run, so a key for every game of July (89 M) would be
  ~6.5 GB, more than fits beside the bot; distinct lines are fewer, and
  session 2 says how many. The trial needs no upgrade: a .NET 10 trial
  program can reference both the library (net10.0) and our Core (net8.0).
  Only if the trie wins and goes into our own commands: (a) the library
  gains a net8.0 target (his repo, his call), or (b) we move up to .NET 10
  (WSL would need it too). The Python edition (nested dictionaries and the
  directory tree; the burst trie is C# only) suits quick experiments on
  samples, not 89 M keys (~10 GB as Python dictionaries).
- **Checks**: counts for a few positions equal a straight count over the
  frames; the book's lines agree with make-book.py's.

## Session 4 (outline): the endgame layer

- **Rows gain** where each game enters the tables: the entry ply, the
  material, the tables' verdict under the 50-move rule, and whether the
  game ended as perfect play says. `game takeover` already batches a month;
  what's missing is the join: table-games.py (the filter that keeps games
  reaching 5 pieces) adds each game's number in the month as a tag, and
  takeover writes one line per game keyed by it.
- **Exact positions with few pieces** (the cut-off from session 2), sorted
  by fingerprint in the same format as the opening layer, each joined to
  its table value.
- **Material to games**: a group-by on the rows' entry material.
- **Checks**: entry counts agree with the takeover's report for the same
  month; a sample of games re-walked by hand through the tables.

## Session 5 (outline): the bow-tie map

Matthew's "a map from beginning to endgame": each game stitched at its two
knots, its opening exit and its table entry, with its result.

- A group-by over the rows: (opening exit, entry material, entry verdict,
  result). From this opening, which endings, and how they score; from this
  ending, which openings feed it.
- Report, and a page like the endgames page if he wants one.
- **Checks**: the totals agree with each layer's own totals.

## Then: the month queue builds it

The queue's count step becomes count + index: one read a month does the
SHA-256 check, the book counts, the frame table and rows, and streams the
frames to C# for the positions. Where the index lives, and how it spills
onto another drive, is settled by then (session 1's last check).
