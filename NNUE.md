# A neural-network evaluation (NNUE): the build plan

Matthew asked (2026-10-16) whether his GPU could help the engine. The
honest answer is that it can **train** an evaluation, not run one: the
search asks for an evaluation about once a microsecond on each thread (0.65
a node, 1.2-1.6 million nodes a second), and today's takes a tenth of that,
while a single call to a GPU typically costs several microseconds just to
start. The network is trained on the GPU and played on the CPU.

Why now: Texel tuning has stopped giving Elo. sf2, fitted on 5x the
positions of sf1 (13.58 M against 2.86 M), scored **+1 Elo [-11, +13]**
over 2,000 games against it (WORKLOG 2026-10-15). The data did its work in
step 2a (+208); what limits us now is the evaluation's *shape*: a value for
each piece on each square, blended by phase, plus the bishop pair. An NNUE
is a small neural network that reads the same thing (which piece stands on
which square) but learns its own combinations of them instead of us naming
terms one by one (step 2b). Its first layer is updated cheaply move by
move, the "efficiently updatable" part, which is what makes it affordable
inside a search.

This file is the plan, in the shape of INDEX.md: what was measured, the
network, what the C# engine needs, training, how it is judged, then the
sessions in order, each with "done when", the risks, and Matthew's decisions.
Nothing has been downloaded or installed for it.

## What was measured (2026-10-16)

### The hardware

Matthew: *"This machine has a 3070 laptop gpu."* So the GPU is in the same
laptop as the repo, the data and the bot: scripts get there by being in the
repo, and nothing crosses the share but the month files.

| | |
|---|---|
| machine | this laptop (ID10TError-Laptop1) |
| GPU | NVIDIA GeForce RTX 3070 Laptop GPU: **8,192 MiB**, compute capability 8.6 (Ampere), up to 65 W, SM clock up to 2,100 MHz |
| driver | 595.79, **CUDA 13.2** (nvidia-smi). Windows' own WMI reports 4 GB, which is its 32-bit field's limit, not the card |
| CPU | i9-11900H, 8 cores / 16 threads; .NET 8.0.31 accelerates AVX2 **and AVX-512** here (`Avx512BW`, `Vector512.IsHardwareAccelerated` both true) |
| memory | 15.8 GB RAM, commit limit 20.8 GB; 10.1 GB of commit free at 06:40 with the bot mid-game (the usual headroom, with the Claude app's processes up, is nearer 4 GB) |
| disk | C: 41 GB free; the share (Z:) 1,305 GB free; the link to the desktop 9 MB/s at best (Matthew) |
| installed | Python 3.14.4 with numpy and DuckDB; **no PyTorch, no CUDA toolkit, no Rust**; .NET SDK 10.0.401 (the engine targets net8.0); WSL Ubuntu 24.04 sees the GPU (`/usr/lib/wsl/lib/libcuda.so`), Python 3.12.3, 7 GB |
| PyTorch for it | `torch-2.14.1+cu132-cp314-cp314-win_amd64.whl`: one wheel for the Python already here, **2,001,146,779 bytes** (HEAD request; published 2026-09-30). It carries its own CUDA runtime, so no toolkit is needed. Plus ~15 MB of small dependencies from PyPI. At the household cap (500 KB/s) it takes **~67 minutes** |

**The desktop** (id10terror-desktop, which holds the share). Matthew
remembered a GTX 1650 (2026-10-16); nvidia-smi over SSH (2026-10-17) says
a **GTX 1660, 6 GB**, driver 617.42 (CUDA 13.4, so the cu132 wheel runs
there), up to 130 W. The rest is from the card's published specifications,
not measured here:

| | laptop: RTX 3070 Laptop | desktop: GTX 1660 |
|---|---|---|
| memory | 8 GB | 6 GB |
| memory bandwidth | 448 GB/s | 192 GB/s |
| CUDA cores | 5,120 | 1,408 |
| compute capability | 8.6 (Ampere) | 7.5 (Turing; CUDA 13 still supports it) |
| likely speed on this net | 1x | about 0.4x (the work is mostly memory traffic) |
| **measured on this net** (session 2b) | 0.96-1.17 M positions a second | **634-641 k (0.65x)**; 362 k at 512 hidden |

6 GB is plenty for a 768 x 256 net (the trainer holds ~1 GB of it). nnue1's
20 passes take **~1.9 h** on the desktop at full speed, against ~70 min on
the laptop at full speed or ~2 h at the half duty the bot needs. How the
two are used together is decision D, below. SSH from the laptop to the
desktop works from 2026-10-17 (key login as Admin; WORKLOG). The desktop:
i7-3770, 16 GB, the share on a 2 TB HDD (D:).

### What today's evaluation costs, and what a network would

A cost probe (`scripts/nnue-probe/`, scratch only: the engine's own source,
with hooks added in a copy) runs the engine's six bench positions on one
thread and adds work beside the real evaluation. Each network arm keeps
its accumulators up to date on every change to the board and runs a full
forward pass, with random weights, at every evaluation. Every arm searches
the identical tree (checked: same node count), and the arms take turns
round by round, because the laptop's speed drifts and the bot was playing
an 8-thread game alongside. Two runs: depth 7, one round, the built-in
numbers; depth 9, five rounds, the bot's sf1 (medians).

| | depth 7 | depth 9 |
|---|---|---|
| nodes / evaluations | 934,689 / 629,771 | 4,767,261 / 3,103,771 (**0.65 a node**) |
| today's speed, one thread | 1,168 knps | 1,374 knps |
| **today's evaluation** | **68 ns, 5.4% of search time** | **120 ns, 10.7%** |

Speed with a network **in place of** today's evaluation (its probe cost
added, today's taken away):

| network | weights (int16) | depth 7 | depth 9 |
|---|---|---|---|
| 768 -> 128 x2 -> 1 | 197 KB | -4% | -12% |
| **768 -> 256 x2 -> 1** | **395 KB** | **-9%** | **-18%** |
| 768 -> 512 x2 -> 1 | 790 KB | -30% | -36% |
| HalfKP 40,960 -> 256 x2 -> 32 -> 32 -> 1 (Stockfish 12's shape) | 21 MB | -49% | -55% |

These are **upper bounds**. The probe updates the accumulators on unmake as
well as make (13.2 column updates a node; a real engine keeps a stack and
pops it, about half that), calls its hooks through every board change, and
sums HalfKP's hidden layer the slow way. The engine is cheap to evaluate
because everything else is expensive: a 64-square board with move lists,
so the evaluation is a tenth of the time or less. A network that costs
three times as much as today's evaluation still loses only a sixth of the
speed. (If bitboards come later and make the rest of the search faster,
the network's share grows with them.)

The bot itself: 12.9 M nodes a second on 8 threads (its log, 06:37), about
1.6 M a thread.

### The data

Lichess's analysed games carry Stockfish's score after every move (its own
server analysis; how deep isn't recorded in the file). These are the
labels.

| what | games | positions | where |
|---|---|---|---|
| sf1 (step 2a) | first 3 GB of July: 948,918 analysed, 721,358 used | **2,855,253** (4 a game) | games/tune/positions-2026-07-sf.tsv, 216 MB |
| sf2 | all of September: 8,896,948 analysed, 6,811,130 used (blitz or slower) | **13,580,216** (2 a game) | games/tune/positions-2026-09-sf.tsv, 1.04 GB |
| September's analysed games, imported | 8,896,948 (9.9% of 89,616,462) | 587,847,024 plies | games/tune/eval-games-2026-09.cbg, 4.18 GB |

How many positions a game can give (the first 100,000 of September's
analysed games, the tuner's own rules, `scripts/nnue-probe/count.cs`):

| per analysed game | |
|---|---|
| plies | 65.7 |
| with a numeric score | 61.3 |
| with a mate score instead | 3.4 |
| after move 8, not in check | 42.9 |
| **the tuner's quiet rule** (after move 8, not in check, no capture or promotion just before or as the move played, more than 5 pieces) | **25.2** (25.8 a blitz-or-slower game) |

So **September alone holds ~224 M quiet labelled positions** (all speeds;
~176 M blitz or slower; ~545 M with every numeric score), against the
13.6 M sf2 used. sf2 took 2 a game.

What the game index adds: July's rows (`flags` bit 0 = has [%eval]) count it
in seconds, no read of the month needed:

| July 2026 (index) | |
|---|---|
| games | 89,288,421 |
| analysed | **9,005,321** (10.1%), 595,554,594 plies |
| analysed, blitz or slower | 7,065,134 |
| analysed, both players 2000+ | 1,430,570 |

So **~227 M quiet positions in July** too, and August (91.9 M games) is on
the share as well: **~670 M over the three whole months**. The positions
still need the moves and scores from the PGN, so each further month costs
the same pipeline September went through (tune-data.cmd: the analysed
games filtered over the share in 83 min, imported in 142 min; ~4 h a month,
its own window, 7.6 GB of .pgn that can go and 4.2 GB of .cbg that stays).
The index's part is choosing (by speed, rating, bots) and counting.

## The network

**Proposed first net: 768 -> 256 x2 -> 1, SCReLU, int16.** In words:

- **Inputs, 768**: for each of the 12 pieces (6 kinds x 2 colours) on each
  of the 64 squares, a 1 or a 0. About 32 are 1 at a time.
- **Two perspectives**: the same 256 hidden numbers computed twice, once with
  the board as white sees it and once as black sees it (colours swapped,
  board flipped top to bottom). The side to move's 256 go first. One set of
  weights serves both, so the network is colour-symmetric by construction.
- **The accumulator**: the 256 hidden numbers before the activation are just
  a sum of one column of weights per piece on the board. A move changes 2-4
  inputs, so the accumulator changes by 2-4 column adds and subtracts, not a
  recount. That is the whole trick.
- **Output**: each of the 512 numbers clamped to [0, 1] and squared
  (SCReLU), times a weight, summed: one score, in Lichess's centipawns (the
  same scale as the labels; sf1's fitted K of 1.12 says today's numbers are
  close to it already).
- **197,377 numbers** to learn: ~1,100 positions each from September alone.
  HalfKP's 10.5 M would get ~21 each, spread over 64 king squares, which is
  why Stockfish trained its first nets on billions of positions.

The first net leaves out king buckets and output buckets, the layers between,
and a result blend. Each is a lever for session 5, measured one at a time.

## What the C# engine needs

1. **The file format** (`.nnue`, ours): magic `CBNNUE01`, then u32 version,
   inputs (768), hidden (H), i32 QA (255), QB (64), scale (400); then
   little-endian int16 arrays: feature weights [768][H] (feature-major, so
   a column is contiguous), feature biases [H], output weights [2H]; i32
   output bias; then the SHA-256 of all of it, checked on load. (As built,
   session 3: H a multiple of 16 from 16 to 1024, QA 1-255; the output bias
   is in QA x QB units, so the trainer writes round(b x QA x QB); the loader
   also refuses any |output weight| x QA over 32,767 and any hidden unit
   whose bias plus its 32 largest weights of one sign leaves int16, which a
   net clamped to +-1.98 never does: 127 x 255 = 32,385, 32 x 505 + 505 =
   16,665. `scripts/nnue-reference/nnue_reference.py` writes and reads the
   format in numpy.)
2. **The feature index**, perspective p (0 white, 1 black), square s with
   a1 = 0 as in Position: `((piece colour == p ? 0 : 6) + kind - 1) * 64 +
   (p == 0 ? s : s ^ 56)`. Written down once, in C# and in the Python
   trainer, and tested against each other.
3. **Accumulators on a stack, in Position.** `Position` owns an optional
   network state: a stack of accumulator pairs (2 x H int16), growing as
   needed. `MakeMove` collects its 2-4 piece changes (mover off, mover or
   promotion on, a capture off, the rook for castling, the pawn for en
   passant) and writes child = parent + changes in one pass; `UnmakeMove`
   pops; the null move pushes an unchanged copy (or shares the slot: as
   built, it shares it). `FromFen` and `SetPiece` refresh from the board
   (as built: `Position.FromFen(fen, network)` and `UseNetwork(network)`
   attach and refresh; a take-back past where the stack starts refreshes). Every Lazy SMP helper
   builds its own Position from the root's FEN (Search.cs 355, 432), so each
   thread has its own stack, and the weights (395 KB) are shared read-only.
4. **SIMD** through `System.Runtime.Intrinsics`: `Vector256<short>` adds
   and subtracts for the columns (16 lanes, AVX2); the output as clamp, then
   (v x w) x v with `Avx2.MultiplyAddAdjacent` into int32. The (v x w) step
   fits in 16 bits because v <= 255 and |w| <= 127. This is what the probe
   ran. A plain scalar version beside it is the tests' reference. AVX-512 is
   accelerated on this CPU too: a later speed step, measured.
5. **Quantisation**: trained in float32. The trainer clamps weights to
   +-1.98 throughout, so the feature layer becomes int16 at x255 (QA) and
   the output weights at x64 (QB) with no overflow. The export checks the
   worst case per hidden unit (bias + the 32 largest weights) against
   32,767 and refuses a file that could overflow. Score = (the sum of
   clamp(a)^2 x w, divided by QA, plus the output bias) x scale / (QA x QB).
   (As built, session 2: the export picks QB per net, the largest with every
   |output weight| x QB <= 128, so x QA it still fits 16 bits; never below
   64. Trained output weights sit far inside the clamp (largest ~0.5), and
   at QB 64 their roundings added up to a near-constant -9.5 cp for the side
   to move; at the fitted QB the int16 net is within ~5 cp of the float one.
   The file carries QB and the loader takes any, so no C# changed.)
6. **Plugging in**: `Evaluation.Evaluate(position)` uses the network when
   one is loaded, today's numbers otherwise. The UCI option `EvalFile`
   takes either kind of file, told apart by the magic, so make-config.py,
   tune-match.cmd and the match tool need no new option (python-chess
   refuses options the engine doesn't list). Texel tuning keeps
   `Evaluate(position, params)`. The endgame tables still settle 5 pieces
   or fewer before any evaluation. (As built: "loaded" means the position
   carries a network. A .nnue in EvalFile is that UCI engine's own, not
   process-wide like the numbers, so tests running side by side and two
   engines in one process can't meet each other's net.)

## Training

- **Data out of the .cbg** (C#, `nnue data`): the tuner's quiet rule by
  default, a record per position of **32 bytes**: occupancy u64 (a1 = bit
  0), the 32 pieces as 4-bit codes in square order (16 bytes), side to move
  u8, white's score i16 (mates as +-32000, kept and flagged; the trainer
  leaves them out at first, as the tuner did), the result u8, the game
  number u32 (for the split, and to keep a game's positions together). They
  are written to 64 bucket files chosen at random, so the trainer can read
  buckets in a random order and shuffle each one (~110 MB) in memory: one
  copy on disk, well mixed. September: **~224 M x 32 bytes = ~7.2 GB** on
  C:. Held back: every game whose number is 0 mod 50 (2%).

  **As built (session 1)**, every field on its own alignment, so a batch can
  be viewed as int64 / int16 / int32 with no copy (the side to move and the
  result swap places with the score to get there):

  | offset | size | field |
  |---|---|---|
  | 0 | 8 | occupancy, little-endian: bit s set when square s holds a piece (a1 = 0, h8 = 63) |
  | 8 | 16 | the pieces, 4 bits each in square order: the i-th occupied square's code is in byte 8 + i/2, low half for even i; unused halves 0 |
  | 24 | 1 | flags: bit 0 black to move, bit 1 the score is a mate |
  | 25 | 1 | result for white: 0 lost, 1 drawn, 2 won |
  | 26 | 2 | white's score, int16 centipawns; mates +-32000 (flagged), every other score held to +-31999 |
  | 28 | 4 | the game's number, uint32, counting every game in the sources |

  A piece's code is colour x 8 + type: white pawn 1, knight 2, bishop 3,
  rook 4, queen 5, king 6; black 9-14. The score is Lichess's after the move
  before, i.e. this position's; a quiet position without one is left out.
  Files: `bucket-00.bin` ... `bucket-63.bin`, `held-back.bin`, and
  `data.json` (written last, so no data.json means no finished run).
  `scripts/nnue/records.py` reads them with numpy (`read`, `boards`,
  `is_mate`, `black_to_move`, `to_fen`, `check`); the C# side is
  `Core/Records/NnueRecord.cs`.
- **The trainer** (`scripts/nnue/train.py`, PyTorch on Windows, in a venv at
  `scripts/nnue/.venv`, ignored by git): numpy reads a bucket and the raw
  records go to the GPU as they are, ~512 KB a batch of 16,384, where torch
  unpacks them into feature indices, so the CPU does almost nothing. The
  feature layer is a sum of embedding rows. Loss: (sigmoid(out / 400) -
  sigmoid(cp / 400))^2, the tuner's target. AdamW, the learning rate stepped
  down over ~20 passes, the clamp of +-1.98 after every step, held-back
  error every pass. It exports the `.nnue` file and a float checkpoint.
  (As built: the sigmoid is the tuner's, s(x) = 1 / (1 + 10^(-x / 400)), so
  the errors here and Tuner's are the same measure.)
- **How long** (measured, session 2): **0.96-1.17 M positions a second** at
  full speed, 190-228 s a pass over September's 218.9 M (mates out), so 20
  passes is ~70 min. But see "the GPU and the bot" below: beside the bot it
  runs at half duty, ~2 h a run.
- **Host memory** (measured): ~2.8 GB of commit for the trainer, of which
  ~2.1 GB is torch and its CUDA kernels; ~740 MiB on the GPU. Each bucket is
  read whole (116 MB) and the next one prefetched.

## How it is judged

1. **Offline first, on held-back positions.** Held-back error computed the
   same way for sf1, sf2 and the net (fit K, then mean squared error against
   sigmoid(cp / 400)) on the same 2% of September's games. The trainer has
   sf1's 770 numbers in numpy for this. The net must beat sf1 clearly here
   before any games are played. (For scale: sf2 went from 0.0388 to 0.0327
   on its own held-back positions.)
2. **Speed**: `bench 9`, today against the net, alternated A B A B. The probe
   says -9% to -18% at 256 hidden, as an upper bound. Worse than -25% means
   the code needs work before the match.
3. **The match, as before**: `scripts\nnue-match.cmd <net> sf1`, built like
   tune-match.cmd: the engine and both files copied to `%TEMP%\cb-match`
   (the match tool splits its command at the first space), then
   `ChessBruteforcer.Match` at **10+0.1**, 5 at a time, the 300 book
   openings in colour-reversed pairs, our tables adjudicating, **SPRT [0,
   10]**, at most 2,000 games. sf2's 2,000 took ~3 h; a big difference
   settles in tens of games, as sf1's +208 did in 69. Logged in MATCHES.md
   and WORKLOG.
4. **Then the bot**, on Matthew's word: EvalFile in make-config.py points at
   the net. Then its first ~30 rated games, against sf1's record.

What to expect: other engines' first nets over a tuned hand evaluation
commonly gain hundreds of Elo, but those were trained on their own search's
positions. Ours learns from human games scored by Stockfish, so anything from
nothing to +200 is plausible here. The match decides.

**It decided (2026-10-17): nnue1 +742 Elo [+645, +970] over sf1**, +392 -3
=5 in 400 games, after the SPRT had accepted H1 (session 4).

## The sessions

Session 0 is this one: the plan and the measurements above. **Sessions 1
and 3 need nothing from each other** (the formats are fixed here) and can
run side by side. Session 2 needs 1's data (the download is agreed: A(a));
2b needs 2's trainer (and, as it turned out, only SSH to the desktop); 4
needs 2 and 3.

### Session 1: the data (no downloads)

- `nnue data <out dir> <eval-games .cbg>... [--per-game all|N] [--speeds
  blitz,rapid,classical] [--buckets 64]`: the record above, the quiet rule
  as the tuner applies it (one shared function, so the two can't drift),
  bucket files plus `data.json` (counts, rule, source files, held-back
  share).
- A reader in Python (`scripts/nnue/records.py`, numpy only), which the
  trainer will use.
- Tests: FEN -> record -> FEN round trips for 10,000 positions (castling
  rights and en passant aren't stored: the network doesn't see them, and
  the test says so); with `--speeds blitz,rapid,classical` the count equals
  `tune positions ... --min-elo 0 --per-game 1000 --lambda 0` on the same
  games; held-back games never in the training buckets; the Python reader
  decodes C#'s records to the same pieces.
- Run on September, its own window, below-normal priority (~45 min, like
  `tune positions` was).
- Report `scripts/nnue-data-2026-09.txt`.

**Done 2026-10-16** (branch `claude/nnue-data-7d1bea`): `nnue data` and
`nnue show` in the Cli, the shared rule in `Core/Records/QuietPositions.cs`,
the record in `Core/Records/NnueRecord.cs`, `scripts/nnue/records.py` and
its test, `scripts\nnue-data.cmd <month>` for a month in its own window.
September is in `games\nnue\2026-09\` on this laptop.

Done when:
- [x] the round trip, the count and the split tests pass (28 tests, 39
      mutations all red);
- [x] September's buckets are written: ~224 M positions (25.2 a game
      predicts it; a big miss means the rule differs from the tuner's).
      **223,352,776 scored in centipawns (25.1 a game, -0.3%) plus
      13,587,674 mates: 236,940,450 records, 7.58 GB, in 41.2 min.** The
      first run came in 1.4% under: Lichess's Swiss games (1.07%) are
      rated but their Event lacks "Rated". On Matthew's word they count
      now, in `tune positions` too (`StoredGame.IsRated`), and on the first
      100,000 games nnue data, tune positions and count.cs agree exactly;
- [x] the report is in git: [scripts/nnue-data-2026-09.txt](scripts/nnue-data-2026-09.txt).

### Session 2: the trainer, on the laptop (Matthew chose A(a): PyTorch)

- The wheel through curl with the household cap (`--limit-rate 500k`, ~67
  min; pip has no cap), then `pip install` from the file into
  `scripts/nnue/.venv`. The script is written meanwhile. Keep the wheel
  file: the desktop takes the same one (session 2b), so the house downloads
  it once.
- `train.py` as above; `--max-positions` for trials.
- Checks: the net learns 10,000 positions almost perfectly (proof the
  pipeline is right); the float and int16 nets agree to a few centipawns on
  1,000 positions; held-back error falls pass by pass. Measured: positions a
  second, GPU memory, host memory, the bot's nodes a second while it trains.
- The full run on September in its own window, logging to a file.

Done when:
- [x] the checks pass, the speed and memory are written down;
- [x] `nnue1.nnue` is exported, and its held-back error is below sf1's on
      the same positions: **0.014851 against 0.030396** (-51%), as the
      engine computes it, on September's 4,465,944 held-back positions;
- [x] WORKLOG has the run.

**nnue1** (2026-10-17, 00:53-03:46): `games\nnue\nets\nnue1.nnue`, 394,820
bytes, QA 255, QB 64; the network's SHA-256 (what the engine prints)
4a18e09ae6544ece43cbac5bc45ac5adf0a8dc9bff973e3916b7de95da9cf497, the
file's 666edbe590a0c5218fe852b078e71a4c28cf5721a7d72cf5dff358df9d7890af.
20 passes over 218.9 M positions, AdamW at 0.001 then x0.3 after passes 8,
14 and 18; passes 1-5 at full speed (with pauses for measuring and the
battery), 6-20 at half duty. Held-back error (float, K 1) by pass:

| pass | 1 | 2 | 5 | 8 | 9 | 14 | 15 | 18 | 19 | 20 | exported |
|---|---|---|---|---|---|---|---|---|---|---|---|
| error | 0.01673 | 0.01623 | 0.01562 | 0.01542 | 0.01506 | 0.01496 | 0.01482 | 0.01481 | 0.01477 | 0.01476 | **0.01485** |

The three steps of the learning rate bought 0.00036, 0.00014 and 0.00004,
and the passes between them less each time: more passes would add almost
nothing; more data or a bigger net might (session 5). On nnue1 itself: the engine equals
nnue_reference on 1,000 positions; `bench 9` 3,830,535 nodes (0.80x
sf1's), sensible moves; float against int16 7.5 cp mean (6.4 median, 29
max), of which -5.1 cp is a constant offset. One output weight reached the
+-1.98 clamp (five more are over 1.5), so the fitted QB came out at 64,
the plan's own; the rounding costs 0.6% of the held-back error. A later
lever: refit the output bias over a sample after rounding (removes the
constant part), or let the output weights train with their rounding.

**Built 2026-10-16/17** (branch `claude/nnue-trainer-f0d60e`; WORKLOG has
the run). What sessions 2b, 4 and 5 should know:
- **The wheel** is in `games\nnue\wheels\` (2,001,146,779 bytes, SHA-256
  bfdad379...e7520a4, the server's own checksum; 65 min at 511 KB/s).
  `scripts\nnue\setup-venv.cmd [wheel]` makes `scripts\nnue\.venv` from it,
  with the system Python's numpy (`--system-site-packages`); pip fetched only
  torch's small dependencies (9.4 MB). The venv was made in the session's
  worktree, so after the merge run setup-venv.cmd once in the main checkout.
- **The scripts**: `scripts/nnue/train.py` (`train <run>`, `export`,
  `evaluate <.nnue>...`, `numbers <numbers .txt>...`), `scripts/nnue/checks.py`
  (the checks below), `scripts/nnue/bot_speed.py` (the bot's Speed lines by
  window), and `scripts\nnue-train.cmd <run> [options]`: its own window,
  below-normal priority, a copy of the scripts, `games\nnue\nets\<run>.log`
  and `.err`, a checkpoint `<run>.pt` after every pass (`--resume`).
- **Steering a run**: `<run>.pause` in games\nnue\nets makes it wait between
  buckets; a number in `<run>.duty` sets the share of the time the GPU works
  (`--duty`); it waits for AC by itself while the laptop is on battery.
- **The checks**:

  | check | result |
  |---|---|
  | the GPU's unpacking against nnue_reference.feature, 10,000 records (591 mates, 217,672 pieces) | all equal, both perspectives, side to move, score, mate flag |
  | sf1's and sf2's numbers in numpy against the engine's eval, 1,000 positions each | all equal |
  | 10,000 positions learnt (600 passes of 10) | training error 0.103 -> 0.000011 |
  | float against the int16 export, 1,000 positions (trial net, QB 238) | 5.2 cp mean, 4.5 median, 25 max (10.5 mean at QB 64) |
  | the GPU's integer pass against nnue_reference, 1,000 | all equal (so held-back error is the engine's own) |
  | the engine (EvalFile) against nnue_reference, 1,000 | all equal |
  | `bench 9` on the trial net | 4,913,912 nodes, 1.03x sf1's 4,767,261: a normal tree |

- **Speed and memory** (this laptop, the bot playing alongside):

  | | without training | training at full speed | at half duty |
  |---|---|---|---|
  | positions a second | | 0.96-1.17 M | ~0.58 M |
  | GPU (nvidia-smi) | 0-340 MiB, ~17 W | ~740 MiB, ~99 W, 99% busy | ~740 MiB, 57-68 W |
  | trainer's host memory | | 1.5 GB working set, 2.8 GB commit | the same |
  | free commit (whole laptop) | 6.66 GB | 3.82 GB | |
  | single-thread `bench 9` (sf1), arms alternated, the bot searching | 983-1,080 knps | 761-832 (**-24%**) | median 974 against 1,080 (**-10%**) |
  | the same, the bot idle | 1,519-1,556 | 1,431-1,553 (0 to -7%) | 1,545 against 1,550 (0%) |
  | the bot's own Speed lines (long searches) | 10.8 / 9.8 Mnps | 8.4 (**-22%**) | 10.0 (+2%, within its noise) |

  Full speed: eight alternated pairs of about a minute each. Half duty:
  three alternated blocks of 6 minutes each way, a bench every 30 s logged
  with the CPU's load (16 and 17 benches with the bot searching, 50 and 63
  of its moves). Short arms flattered half duty (-2 to -7%); the 6-minute
  blocks are the figure to use. The bot's speed halves within a game as it
  reaches the endgame, so only alternated blocks compare.

- **The GPU and the bot** (D(a)'s 10% line): the charger gives 180 W
  (Matthew: 19.5 V x 9.23 A). The GPU at full speed takes ~99 W, and with
  the bot's 8 threads the CPU falls from ~125% to ~100% of its base clock.
  With the bot idle the same training costs a bench 0-7%, so it is the sum
  that hits the limit. Half duty sits at the 10% line by the bench and shows
  no loss in the bot's own speed; Matthew chose it for nnue1's last 15
  passes (2026-10-17, 01:45). Duty 0.25 and 0.1 cost 0-2% in short arms, but
  take 4x and 10x as long. For a margin, 0.4.
- **On battery** (the laptop off its charger, 01:02-01:10) everything
  throttles, training or not: the bot 4.3 Mnps, a bench ~560 knps.

### Session 2b: the desktop's GTX 1660 as a second trainer (D(c))

**Over SSH** (from 2026-10-17): OpenSSH Server on the desktop, and the
laptop logs in by key as `Admin@id10terror-desktop`, with no password. So a
session here runs commands there, as `ssh -o BatchMode=yes
Admin@id10terror-desktop "<command>"`. Matthew's hands turned out not to be
needed at all: Python 3.14.4 with numpy was already on the desktop, and
torch's small dependencies were copied from the laptop's venv, so nothing
was downloaded. The desktop's driver is already fine (CUDA 13.4). One thing
to settle first: on Windows, programs started over SSH
die when the connection closes, so long runs go through a scheduled task
(`schtasks`) that logs to the share, not straight from the SSH command.

- **On the share** (`\\id10terror-desktop\chessbruteforcer\nnue\`, which is
  the desktop's own disk when seen from the desktop): the trainer scripts,
  the wheel file (no second download), a copy of the records, and
  `train-desktop.cmd <run name> <options>`, which logs to the share, so its
  runs can be followed from the laptop.
- **What crosses the link**: Matthew's figure is 9 MB/s between the two
  machines at 100% efficiency, so these times are the best case:

  | what | size | at 9 MB/s |
  |---|---|---|
  | the wheel | 2.0 GB | 3.7 min |
  | September's records | 7.2 GB | 13 min, once |
  | three months' records (C(b), later) | 21 GB | 40 min, once |
  | a trained net coming back | 395 KB | under a second |
  | the trainer reading its records over the link instead | 7.2 GB a pass x 20 | 4.4 h a run, more than the training: **each machine keeps its own copy** |
  | one run split over both GPUs | 0.8 MB of gradients each way, every step | ~0.18 s a step for the swap against 8-33 ms of work: 5-20x slower than the laptop alone |
  Matthew's later rule (2026-10-16): **all share traffic at or under ~8 MB/s
  in total**, since more knocks the laptop off the internet and the bot with
  it. So copies are capped (September's 7.58 GB records: ~16 min), and only
  one big share job runs at a time.
- **Set up over SSH**: the driver is checked (617.42, CUDA 13.4; the wheel
  needs 13.2). Python 3.14 was already on the desktop; a venv with the
  wheel from the share, and the records copied to the desktop's own disk.
- Checks: `torch.cuda.get_arch_list()` includes sm_75; the same short run
  on both machines gives the same held-back error to within noise (same
  data, same seed); the 1660's positions a second, written down.
- **Not one run split over both machines**: the swap of gradients every
  step (the table's last line) costs far more than the ~0.4 of the
  laptop's speed that the 1660 adds. Instead, each trains its own net: the
  levers of session 5 two at a time, or the desktop takes the long runs
  while the bot has the laptop.

Done when:
- [x] a short run on the desktop matches the laptop's: nnue1's first two
      passes replayed, held-back error 0.016734 / 0.016159 against nnue1's
      0.016731 / 0.016233: gaps of the size a change of seed makes (below);
- [x] the desktop's speed is written down: **634-641 k positions a second**
      at 256 hidden (0.65x the laptop's card), 362 k at 512;
- [x] a full run's `.nnue` comes back over the share and loads in the
      engine: **nnue1-h512** (Matthew's (b), below), 3 h 21 min on the
      desktop, in games\nnue\nets\; EvalFile loads it (768 -> 512 x2 -> 1,
      sha256 8a6cd3aff627, the trainer's own), held-back error **0.013664**
      against nnue1's 0.014851.

**Built 2026-10-17, 06:15-07:50** (branch `claude/nnue-desktop-8d5f2a`;
WORKLOG has the story). What later sessions need:

- **On the share**, `\\id10terror-desktop\chessbruteforcer\nnue\`, which is
  `D:\-=Chess Bruteforcer=-\nnue` on the desktop (a 2 TB HDD, 1.37 TB free):

  | there | what |
  |---|---|
  | `2026-09\` | September's records, all 66 files, each one's SHA-256 the laptop's |
  | `wheels\` | the torch wheel, and `torch-deps-cp314-win_amd64.zip`: torch's seven small dependencies (filelock, fsspec, jinja2, markupsafe, mpmath, networkx, sympy) zipped from the laptop's venv, 9.5 MB |
  | `.venv\` | torch 2.14.1+cu132 on the desktop's own Python 3.14.4 (numpy 2.4.6, typing-extensions, setuptools from it), made by `setup-desktop.cmd` with `pip --no-index`; `pip check` clean; `get_arch_list()` has sm_75 |
  | `scripts\nnue\`, `scripts\nnue-reference\` | the trainer, as `sync` last copied it |
  | `train-desktop.cmd`, `setup-desktop.cmd` | from `scripts\nnue\` |
  | `nets\` | each run's `.log`, `.err`, `.pt` and `.nnue`; `<run>.pause` and `<run>.duty` steer it as on the laptop |
  | `runs\<run>\` | the scripts each run started with |

- **Driving it from the laptop**: `scripts\nnue-desktop.ps1`, over SSH.
  `sync` puts the scripts on the share; `train <run> [options]` registers
  and starts the task `\ChessBruteforcer\nnue <run>` on the desktop (as
  Admin, S4U: no stored password and no window on Matthew's screen;
  below-normal priority; a 24 h limit), which runs `train-desktop.cmd`;
  `status [<run>]`, `stop <run>`, and `fetch <run>` (the `.nnue` and `.log`
  into the main checkout's games\nnue\nets\). `--data` and `--out` are the
  desktop's own folders, so training never reads across the link. CUDA
  works from the task's non-interactive session (checked first with a
  test task).
- **Big copies**: `scripts\capped-copy.py` flushes each 1 MiB to the share
  and holds the running average to the rate it is given, then writes the
  SHA-256 of every file it read; the desktop hashes its copies on its own
  disk (`Get-FileHash` over SSH), so checking costs no second pass over the
  link. At 7,500,000 B/s the laptop's Wi-Fi carried 7.32 MB/s.

  | copy | bytes | time | average |
  |---|---|---|---|
  | torch's dependencies (the zip) | 9,509,801 | 1 s | |
  | the wheel | 2,001,146,779 | 280 s | 7.14 MB/s |
  | September's records | 7,582,099,358 | 1,173 s | 6.46 MB/s (4.4 while pip unpacked torch onto the same disk: each chunk waits for its flush) |

- **The check**: September, seed 20261016, nnue1's settings, two passes.
  The laptop's half is nnue1's own first two passes from its log (the
  training code is the same apart from the battery wait, and passes 1-5
  ran from a clean start), since the laptop had ~1 GB of commit free beside
  the bot and the Claude app, too little for torch (~2.8 GB):

  | | laptop (nnue1) | desktop replay (vs the laptop) | desktop again | desktop, seed 20261017 (vs the replay) |
  |---|---|---|---|---|
  | held back after pass 1 | 0.016731 | 0.016734 (+0.000003) | 0.016734 | 0.016834 (+0.000100) |
  | held back after pass 2 | 0.016233 | 0.016159 (-0.000074) | 0.016159 | 0.016146 (-0.000013) |
  | training error, pass 1 / 2 | 0.018348 / 0.016386 | 0.018342 / 0.016353 | the same | 0.018433 / 0.016361 |

  Every one of the 16 progress lines saw the same number of positions on
  both machines (the same buckets in the same order, the same mates left
  out), and the running losses stayed within +-0.000033 of each other. The
  desktop repeats itself to the byte (the replay and the repeat made one
  net, sha256 e6840ae8...). The two cards don't compute identically, and
  the difference grows through training the way a change of seed does: the
  machines are 0.000003 and 0.000074 apart, a new seed 0.000100 and
  0.000013. Both kinds of gap are under 0.0001, against a gain of 0.0005
  from pass 1 to pass 2 and nnue1's 0.0155 below sf1. One seed pair is a
  thin yardstick; a full run of one recipe on both machines (option (a)
  below) would measure the end-of-run spread properly.
- **Speed and memory**:

  | | laptop: RTX 3070 Laptop | desktop: GTX 1660 |
  |---|---|---|
  | positions a second, 256 hidden | 0.96-1.17 M at full speed | **634-641 k** (0.65x) |
  | a pass, 256 hidden | 190-228 s | 342-345 s |
  | positions a second, 512 hidden | | **362 k** (0.57x its own 256) |
  | torch's GPU memory, peak allocated / reserved | 615 / 994 MB | 615 / 996 MB at 256; ~700 MB allocated at 512 |
  | the card under training | ~99 W | 75-84 W of 130 W, 1,920 MHz steady, 62-70 °C, fan ~66%, PCIe 3.0 x16 |
  | the trainer's host commit | 2.8-3.2 GB | 2.6-3.2 GB (the desktop has 16 GB, ~11 GB free) |

  The driver's slowdown counters showed no power capping, power braking or
  thermal slowdown at all during these runs (Matthew asked about
  brownouts), and the desktop's System log has no driver resets, hardware
  errors or unexpected power losses in 30 days.
- Gotchas: in an SSH session `%USERDOMAIN%` is WORKGROUP, so the task's user
  is the current Windows identity (ID10TERROR-DESK\Admin); an encoded
  PowerShell command sends its progress records back as CLIXML unless
  `$ProgressPreference` is off. The desktop's clock runs ~3 min ahead of the
  laptop's, so its log times do too. `train-desktop.cmd` passes every option
  on; `nnue-train.cmd` still passes only `%2`-`%9` and drops a ninth token
  (TODO).

**The first full run, nnue1-h512** (Matthew's (b), 2026-10-17 07:45, over
(a) nnue1's recipe again as a full-length reproducibility check, ~1.9 h, or
(c) both, ~5.3 h): nnue1's recipe (September, 20 passes, the same seed and
learning-rate steps) with `--hidden 512`, ~3.4 h at 362 k positions a
second. The engine reads any hidden size from 16 to 1,024. Its match
against nnue1 is session 5's.

**nnue1-h512** (2026-10-17, 07:49-11:10 by the desktop's clock, ~3 min
ahead of the laptop's): `games\nnue\nets\nnue1-h512.nnue`, 789,572 bytes,
QA 255, QB 64; the network's SHA-256 (what the engine prints)
8a6cd3aff627c93347a8b8caddaa29c3c10e0a08b7166240298eb2fcd55b796a, the
file's 38eeba1feabc74589acec8fde8405bed2f3de3ad917b4b9b2f6b738f62fbc4c4.
On the share beside it: `nnue1-h512.pt` (the float checkpoint) and the log.
593.7-598.8 s a pass (366-369 k positions a second), no pauses; torch's GPU
memory 1,003 MB allocated / 2,052 reserved at peak, host commit 4.1 GB.
Held-back error (float, K 1) by pass, against nnue1's at the same pass:

| pass | 1 | 2 | 5 | 8 | 9 | 14 | 15 | 18 | 19 | 20 | exported |
|---|---|---|---|---|---|---|---|---|---|---|---|
| nnue1-h512 | 0.01591 | 0.01521 | 0.01458 | 0.01436 | 0.01394 | 0.01383 | 0.01370 | 0.01368 | 0.01363 | 0.01363 | **0.01366** |
| nnue1 (256) | 0.01673 | 0.01623 | 0.01562 | 0.01542 | 0.01506 | 0.01496 | 0.01482 | 0.01481 | 0.01477 | 0.01476 | 0.01485 |

- **-8.0% held-back error against nnue1** as the engine computes both
  (0.013664 against 0.014851), the same measure as nnue1's -51% against sf1.
  By pass 4 (0.01472) it was already below nnue1's final pass (0.01476). The
  rounding to int16 costs it 0.28% (nnue1: 0.6%), though QB is 64 again,
  so an output weight sits at the clamp here too.
- **In the engine** (the laptop, beside the bot, on AC; `bench 9`, one
  thread, 6 rounds alternated with nnue1): **-4.0% a node** (median 1,090
  against 1,135 knps), and a 2.8% bigger tree (3,936,636 nodes against
  3,830,535), so ~7% longer to the same depth. Session 3's estimate was
  ~-10%. Through UCI's EvalFile, the way the bot loads it, it searched
  1. e4 e5 2. Nf3 Nc6 3. Bb5 to depth 10 and chose a6, the main line.
- More passes would add little here too: passes 15-20 bought 0.00007.
  Whether -8% in error is worth -4% in speed was the match's to say.
- **The match (2026-10-17, 22:48-23:17, the bot stopped): nnue1-h512 +76 Elo
  [+45, +107] over nnue1**, +114 -53 =118 in 285 games at 10+0.1 after SPRT
  [0, 10] accepted H1 at game 284 (MATCHES.md). In play it searched 8.9%
  slower (2,131 against 2,339 knps), depth 10.3 against 10.5: the better
  evaluation more than paid for it. **The bot plays on it** from its next
  start (Matthew's G(a), below): `bot\eval\nnue1-h512.nnue`, which
  make-config.py tries before nnue1.nnue.

### Session 3: the network in C# (no downloads; can run beside session 1)

- `Core/Engine/Nnue/`: the loader, the accumulator stack in Position, the
  SIMD forward pass, and a scalar reference.
- Tests: **incremental equals a refresh** at every node of perft walks to
  depth 3-4 from positions with castling, en passant and promotions, and over
  random games, the null move included; SIMD equals scalar; the net is
  colour-symmetric (a position and its mirror score the same, exactly);
  **C# equals a numpy reference exactly** on 1,000 positions (a test file
  with random weights until session 2's net exists); the loader refuses a
  bad checksum or magic. Then the usual mutation battery (forget the
  castling rook, the en passant pawn, the promotion, swap the
  perspectives): every one must go red.
- `bench` speed against today's, alternated.
- `EvalFile` takes the `.nnue`; make-config.py untouched (the bot stays on
  sf1 until the match says otherwise).

Done when:
- [x] every test passes and every mutation goes red (NnueTests, 36; 30
      mutations, 30 red);
- [x] the bench is within the probe's range (-9% to -18% at 256): better
      than it, **-2.1%** at 256 (`bench 9`, 8 rounds alternated; the
      tree-free costs predict about -5%), +5.3% at 128, -11.7% at 512;
      -3.8% at 256 after the bounds checks below;
- [x] the full suite passes (377 passed, 2 skipped; 379 after the
      fixes below).

**An access violation, answered (2026-10-16, commit e4ffd61).** A
"testhost.exe - System Error, 0xc0000005" dialog at 08:02:52 was the
mutation battery's "the stack never grows" mutant writing past the array,
as that mutant does. The event log, the popup at the same second and the
battery's own process times agree (WORKLOG has the evidence). The kernels'
unchecked writes would have let a real capacity bug do the same, so every
slot and weight column now reaches them as a bounds-checked span: a bug of
that kind throws instead. Cost: 1.6% on an identical tree; `bench 9` is now
**-3.8%** against sf1. Checked further by the random-games walk over 300
seeds and 300 deep games, in Debug and Release: all green, no crash events.
The battery re-run gave 31 of 31 red. An empty path in `bench --eval` (the
08:16 and 08:18 crashes, from my bench script) is now a clean error; UCI's
empty EvalFile was always the built-in evaluation.

**Built 2026-10-16** (branch `claude/nnue-csharp-8117d8`; WORKLOG has the
details, scripts/nnue-bench/results-2026-10-16.txt the numbers). What
session 2 and 4 should know:
- The trainer writes the output bias as round(b x QA x QB), and can take
  `write_nnue` and `feature` from `scripts/nnue-reference/nnue_reference.py`
  (numpy, written from this file alone; the C# equals it exactly).
- **Random weights make the capture search explode** (Kiwipete, depth 1:
  1.37 M nodes against 3,422): an untrained or badly scaled net looks like
  a hang. `Network.FromPieceSquares` builds a net that computes a numbers
  file's material and piece-square values exactly; `scripts/nnue-bench/`
  benches with it (`pst-net.cs`, `bench.ps1`, `costs.cs`).
- Per operation, net 256: 52 ns to update a move, 55 ns an evaluation
  (today's: 104 ns). 512 hidden costs ~10% against 256 on the identical
  tree, so session 5's 512 lever is cheaper than the probe said (-30 to
  -36%).

### Session 4: the match

- `scripts\nnue-match.cmd nnue1 sf1` (above), its own window, ~1-3 h; the
  bot keeps playing alongside, as it did during sf2's match.

Done when:
- [x] SPRT decides, or 2,000 games are played: **SPRT [0, 10] accepted H1**
      at game 113;
- [x] MATCHES.md and WORKLOG have it;
- [x] if the net is better, Matthew decides whether the bot switches:
      **yes, E(a)**: `bot\eval\nnue1.nnue`, which make-config.py prefers
      over eval-sf1.txt. It takes effect when this branch is in the main
      checkout and the bot restarts (start.cmd rebuilds the engine copy and
      rewrites config.yml). Then its first ~30 rated games against sf1's.

**Played 2026-10-17, 04:00-04:44** (branch `claude/heuristic-poincare-2d9fec`;
MATCHES.md has the tables, WORKLOG the story). The bot was already stopped,
so the match had the laptop to itself, on the charger, at below-normal
priority. 10+0.1, 5 at a time, one thread each, the 300 book openings.

| run | games | nnue1 +W -L =D | score | Elo [95%] | time |
|---|---|---|---|---|---|
| smoke test | 20 | +19 -1 =0 | 95.0% | +512 [+308, +∞] | 1.8 min |
| SPRT [0, 10] | 117 | +114 -0 =3 | 98.7% | +755 [+622, +∞] | 9.4 min, H1 at game 113 |
| fixed length (Matthew's (b)) | 400 | +392 -3 =5 | 98.6% | **+742 [+645, +970]** | 30.3 min |
| all three pooled | 537 | +525 -4 =8 | 98.5% | +728 [+646, +886] | |

- **Clean games**: no time losses, crashes or illegal moves in 537. Of the
  400, 392 wins by mate (355 on the board, 37 by the tables).
- **The search looks normal**: nnue1 depth 9.4 at 2,137 knps against sf1's
  9.1 at 1,887 (the 400 games, every move's last info line). The net is
  13% faster in play, where `bench 9` had it 3.8% slower a node.
- **Far past the plan's guess** (nothing to +200): over three times its top.
  Halving the held-back error bought this; sf2's 0.3% bought +1.
- **The SPRT took 117 games, not ~22**: its normal approximation divides by
  the variance of the scores, which is zero while every game is a win, and
  `MatchStats.LogLikelihoodRatio` returns 0 then. The first draw (game 113)
  took the LLR from 0 to 362. A fix is in TODO.
- **For session 5**: `scripts\nnue-match.cmd <net> <old> [games]
  [concurrency] [fixed]` takes a net as `<old>` too, and runs from a
  worktree (the nets, openings, tables and logs are the main checkout's).
  Against sf1 the score is near 99%, where Elo resolves poorly: measure each
  lever against the net before it, as planned, not against sf1.

### Session 5 (outline): the levers, one match each

More data (July and August: ~670 M); 512 hidden (-30 to -36% speed by the
probe; ~-10% against 256 as built, session 3; **trained in session 2b as
nnue1-h512: -8.0% held-back error, -8.9% speed in play, **+76 Elo [+45,
+107] over nnue1**, and the bot's net from 2026-10-17**); a
result blend (lambda); king buckets or output buckets by material; mate
scores in; the AVX-512 path; labels from our own search on these positions,
which only the CPU can make. Each lever is a match against the net before.
With the desktop training too (2b), two levers train at once.
"Where our losses turn" (TODO section 2) says which matters most.

**Begun 2026-10-18 (02:43), on the desktop, Matthew's (a)**: two levers at
512 hidden, nnue1-h512's recipe otherwise, one task, one after the other:
**h512-blend25** (`--result-blend 0.25`: the target is 0.75 s(cp) + 0.25 x
the game's result for the side to move) and **h512-mates** (`--mates`: the
13.6 M mate-scored positions too, target exactly 0 or 1; ~6% more positions a
pass). Both new train.py options, checked by `checks.py targets` (10,000
held-back records, mates included: equal to the rule restated in numpy to
1e-7 at blends 0, 0.25 and 1; mates 0 or 1) and a one-bucket smoke run each
on the desktop. Held-back error stays the old measure (s(cp), no mates), so
the blend net looks a little worse on it by design: its match decides. Each
then plays nnue1-h512 as nnue1-h512 played nnue1.
- The options weighed (02:30): (b) 1024 hidden, no code: 158 k positions a
  second on the 1660 (7.7 h), and `bench 9` **23% slower a node** than
  nnue1-h512 (a probe net; 1,063 against 1,386 knps): too much speed for its
  evaluation to win back, probably. (c) the blend alone.
- **The 1660 ran at 1,200 MHz and 40 W** at the start of this run, where
  nnue1-h512's run had 1,920 MHz and 75-84 W: 284 k positions a second
  instead of 362 k. No slowdown reason in the driver, no clock lock, the
  full 130 W limit. The difference in the room: Matthew's PySynthRack had a
  graphics context on the card (since 19:19). **He closed it at ~04:20 and
  the card went straight back to 1,935-1,950 MHz and 73-77 W** (the only
  change to the GPU's process list, Discord aside). So close PySynthRack
  before a training run. The blend run ends ~06:30, the mates run ~10:00.
- **h512-blend25 done** (02:46-06:25 by the desktop's clock, exit 0; passes
  1-8 at ~305 k positions a second, 9-20 at ~363 k): `games\nnue\nets\
  h512-blend25.nnue`, 789,572 bytes, QB 65, network sha256 8b73e98ad091...;
  EvalFile loads it, and it chose a6 against the Ruy Lopez at depth 10.
  Held-back error as the engine computes it **0.015230 at K 1, 0.014320 at
  its fitted K 1.22**, against nnue1-h512's 0.013664 at K 1.00. Worse on
  this measure by design; and the fitted K says its scores run ~18%
  smaller than Stockfish's scale (the results pull towards the middle).
  The search's margins (null move, futility) were tuned on the other
  scale, which is one more reason only the match can judge it.
- **Its match: h512-blend25 -11 Elo [-25, +3] against nnue1-h512**, +311
  -349 =592 in 1,252 games, SPRT [0, 10] accepted H0 (2026-10-18, 08:22-10:25,
  the bot stopped; MATCHES.md). **The result blend doesn't help** at 0.25,
  on these human games: their results come from blunders far later in the
  game as often as from the position. A smaller blend (0.1) is the only
  version left worth a try, and not soon.
- **h512-mates done** (06:25-09:59 by the desktop's clock, exit 0; 232.2 M
  positions a pass, 6.1% more; 362-372 k a second): `games\nnue\nets\
  h512-mates.nnue`, 789,572 bytes, QB 64, network sha256 0cf690d690a3...;
  EvalFile loads it, a6 against the Ruy Lopez. Held-back error as the
  engine computes it **0.013813** against nnue1-h512's 0.013664 (+1.1%) on
  positions without mates: the cost of the capacity it spent on mates. What
  it gains is where the measure doesn't look, so the match decides.
- **Its match: h512-mates -11 Elo [-26, +3] against nnue1-h512**, +310 -349
  =553 in 1,212 games, SPRT [0, 10] accepted H0 (2026-10-18, 14:03-16:03, the
  bot stopped). **Mates in training don't help** either: the search finds
  mates by itself, and the tables cover the endings, so the net's capacity
  is better spent on ordinary positions.
- **So far in session 5**: size helped (+76), the two target changes didn't
  (-11 each, both within 3 Elo of zero at the top of their range). Left:
  **more data** (July and August, ~3x the positions; the 512 net's gain
  says capacity was short, and more positions is what a bigger net wants),
  the export's rounding (small), and labels from our own search (a long
  job for the CPU). The desktop trains any of them.
- **More data, begun 2026-10-18 17:34 (Matthew's yes)**, with a tempered
  expectation: nnue1-h512's error on its training positions is only 2.1%
  below its held-back error (nnue1's 0.9%), so it isn't memorising
  September yet; more data should help a little at 512, and matters most for
  a bigger net later. **Records made on the desktop**, from the month files on
  its own disk (`data-desktop.cmd` via `nnue-desktop.ps1 data`: eval-games.py,
  game import, nnue data, records.py check), so nothing crosses the link and
  the laptop keeps the bot: eval-games.py on the laptop reads the share
  uncapped (~9 MB/s, over Matthew's rule). On the i7-3770 the filter reads
  ~1.8 MB/s of a month (one core; a probe of 20 MB took 10.3 s), ~4.3 h for
  27 GB; July and August run side by side at that pace each (the D: disk 94%
  idle), records due ~00:30-01:30. Then one net on all three months:
  `train <run> --hidden 512 --add-data 2026-07 2026-08` (new: buckets of the
  months beside --data, held-back error still September's alone).
- **The records, done 2026-10-19 04:51 and 05:22** (desktop clock), every
  file checked ("all well"):

  | month | records | mates | training games | held-back games | filter | import | nnue data | check |
  |---|---|---|---|---|---|---|---|---|
  | July | 240,433,752 | 13,178,690 | 8,735,386 | 178,260 | 4.1 h | 4.4 h | 2.3 h | 21 min |
  | August | 253,407,598 | 13,198,270 | 9,194,056 | 187,670 | 4.3 h | 4.6 h | 2.5 h | 22 min |
  | September (laptop, for scale) | 236,940,450 | 13,587,674 | | | ~50 min | ~1 h | 41 min | |

  The desktop was ~4x slower than the laptop at the import and records (the
  i7-3770 and a hard disk, two months at once): ~11.5 h a month there, side
  by side. The work folder keeps each month's .pgn (7.7-8.1 GB) and .cbg
  (4.2-4.5 GB) on its D:.
- **h512-3m started 05:26** (laptop clock): 512 hidden on all three months,
  10 passes of ~676 M positions (1.5x nnue1-h512's training steps), the rate
  stepped down after passes 4, 7 and 9 (`--lr-drops 4/7/9`: PowerShell reads
  an unquoted 4,7,9 as three arguments, which cost two false starts). 1,935
  MHz, 77 W; ~31 min a pass, done ~10:45.
- **h512-3m done** (05:32-10:42 by the desktop's clock, exit 0, no pauses;
  362-370 k positions a second, 677,005,066 a pass): `games\nnue\nets\
  h512-3m.nnue`, 789,572 bytes, QB 64, network sha256 9da33208a6f5...;
  EvalFile loads it, a6 against the Ruy Lopez. Held-back error on
  September's positions, as the engine computes it:

  | net | at K 1 (Stockfish's scale) | at its own fitted K |
  |---|---|---|
  | nnue1-h512 (September, 20 passes) | 0.013664 | 0.013664 (K 1.00) |
  | **h512-3m** (three months, 10 passes) | 0.013640 (-0.2%) | **0.013437 (-1.7%, K 1.10)** |

  A small gain, as expected. The fitted K of 1.10 says its scores come out
  ~10% smaller than September's Stockfish scale (every pass fitted 1.06 to
  1.13): July's and August's labels may sit on a slightly different scale,
  which would also explain why its K 1 figure wobbled at the end (0.013503,
  0.013460, 0.013585 over passes 8-10) while the fitted one fell steadily
  (0.013393, 0.013383, 0.013356). Its training error, 0.012119 over all
  three months, isn't comparable with September's alone. The match decides.

## Risks

| risk | what it would look like | what limits it |
|---|---|---|
| The labels' positions aren't the search's | Human middlegames, quiet ones only, while quiescence evaluates lopsided positions mid-capture: wild scores there | Humans hang pieces too, so lopsided positions are in the data. The match is the judge. Labels from our own search in session 5 |
| Lichess's analysis varies in depth | Noisy targets | 224 M positions average a lot out; held-back error shows it |
| Slow C# | Bounds checks, allocation, a virtual call per update: -30% instead of -10% | The probe as a yardstick; the bench gate before the match |
| Silent update bugs | A missed rook or en passant pawn: scores slightly wrong, no crash | Incremental-equals-refresh at every node of the test walks; the mutation battery |
| int16 overflow | Garbage scores in crowded positions | Clamping in training; the export's worst-case check |
| Memory on the laptop | Torch and CUDA beside the bot and the Claude app (~4 GB headroom): a Windows low-memory event, as in October | Measured first; buckets read in pieces; one job at a time beside the bot |
| Heat and the bot | GPU (65 W) and CPU share one laptop's cooling: the bot's nodes a second drop while training runs | Measured in session 2 from the bot's log (Speed line); decision D. **It happened, through power rather than heat**: the GPU draws ~99 W, the charger gives 180, and with the bot's 8 threads the CPU loses ~20% of its clock (bot -22%, bench -24%). Half duty costs the bench ~10%, the bot's own speed nothing measurable |
| The laptop off its charger | Matthew moves it about the house (up to ~10 min): on battery the CPU and GPU both throttle (bot 12.7 -> 4.3 Mnps) | train.py waits for AC by itself; check the power line before reading any timing |
| The household's internet | A 2.0 GB download | curl capped at 500 KB/s (~67 min), as the month queue is |
| The score's scale moves | Null-move pruning compares the evaluation with beta; resigning is off, so nothing else reads it | Trained on Lichess's centipawn scale, which today's numbers already sit near |
| Bitboards later | The rest of the search speeds up, so the net's share grows | Re-run the probe after any big speed change |

## Decisions for Matthew

**Taken 2026-10-16: A(a), B(a), C(a), and D "a + c"**: PyTorch; 768 -> 256
x2 -> 1; September, every quiet position, all speeds; the laptop first,
alongside the bot, then the desktop's GPU as a second trainer
(session 2b; a GTX 1660, it turned out). **2026-10-17, 01:45**: beside the bot at half duty (the GPU's
full ~99 W plus the bot's 8 threads hit the 180 W charger: the bot -22%),
over full speed or stopping at pass 5. The options as they were put:

**A. The trainer** (chosen: a)
- **(a) PyTorch on Windows** (recommended): one 2.0 GB wheel for the Python
  already here, capped download (~67 min), nothing else to install. Our own
  ~300-line script, which we can read end to end.
- (b) bullet, the Rust trainer many hobby engines use: faster, made for
  exactly these nets, but needs Rust and NVIDIA's CUDA toolkit (several GB;
  sizes checked before asking).
- (c) No download: a trainer in C# on the CPU. It would share the 16
  threads with the bot, which the GPU route leaves alone, and run several
  times slower.

**B. The first network** (chosen: a)
- **(a) 768 -> 256 x2 -> 1** (recommended): -9% to -18% speed, 197 k
  numbers.
- (b) 768 -> 128 x2 -> 1: -4% to -12%, half the numbers; the safer
  first try if speed matters more.
- (c) HalfKP 256 x2 -> 32 -> 32 (Stockfish 12's shape): -49% to -55% as
  probed, 10.5 M numbers, wants billions of positions. Not first.

**C. The first data** (chosen: a)
- **(a) September, every quiet position, all speeds** (recommended): ~224
  M, already imported, ~7.2 GB on C:.
- (b) July + August + September: ~670 M, after ~8 h of filtering and
  importing (two months through tune-data.cmd). On C: (41 GB free) that is
  ~8.4 GB more .cbg plus ~21 GB of records instead of 7.2: it fits, just.
  The records have to be local: twenty passes over the share would spend
  ~13 h reading (at 9 MB/s).
- (c) September, blitz or slower only, as the tuner chose: ~176 M.

**D. Where and when it trains** (chosen: a + c, the desktop having a GTX
1660 (remembered as a 1650): two trainers, each on its own net, as session 2b says; not one run
split over both)
- **(a) This laptop, alongside the bot**, below-normal priority (recommended),
  watching the bot's speed in its log and stopping if it drops more than
  ~10%.
- (b) This laptop, only while the bot is stopped (overnight, say).
- (c) The desktop, if it has an NVIDIA card. Tell me which card and it gets
  measured. It would free the laptop entirely, with the data copied over
  the share (13 min for 7.2 GB at 9 MB/s).

**E. The bot and nnue1** (chosen: a, 2026-10-17, after nnue1 +742 [+645, +970]
over sf1 in 400 games). The match was one thread each with no tables in the engines;
the bot plays on 8 threads with the tables and pondering. Nothing there
should take most of +742 away, but it is unmeasured until the bot plays.
- **(a) Switch** (recommended): nnue1.nnue copied into bot\eval\ (in git
  beside eval-sf1.txt, 395 KB; `*.nnue` is already binary in
  .gitattributes), make-config.py preferring it over eval-sf1.txt when
  it's there (delete it to go back), the bot restarted between games. Then
  its first ~30 rated games against sf1's record (step 4 above).
- (b) Switch, reading the net from games\nnue\nets\ where it is: nothing
  new in git, but the bot then rests on a gitignored file.
- (c) Not yet: first an outside yardstick, nnue1 and sf1 each against
  Stockfish at a fixed UCI_Elo (WSL), ~100 games each, ~1 h with the bot
  stopped, so the gain is also on a scale other than sf1's.

**F. The desktop's first full run** (chosen: b, 2026-10-17 07:45, after
session 2b's check)
- **(b) 512 hidden** (recommended): nnue1's recipe with twice the hidden
  numbers, ~3.4 h on the 1660; the first session-5 lever. Run as nnue1-h512.
- (a) nnue1's recipe again, ~1.9 h: a full-length reproducibility check, the
  end-of-run spread between two runs of one recipe.
- (c) Both, 512 first, ~5.3 h.

**G. The bot and nnue1-h512** (chosen: a, 2026-10-17 ~23:20, at game 275 of
its match, which the SPRT then decided at game 284: +76 [+45, +107] over
nnue1). As with E, the match was one thread each at 10+0.1; the bot plays 8
threads at 3+2 with pondering, the tables and its book.
- **(a) Switch** (recommended): nnue1-h512.nnue into bot\eval\ (in git, 790
  KB), make-config.py trying it before nnue1.nnue (delete it to go back);
  then its first ~30 rated games against nnue1's.
- (b) Stay on nnue1 for now; the switch after a check nearer the bot's own
  conditions, where the 9% speed cost may weigh differently.
- (c) As (a), stopping the match at game 275 rather than letting the SPRT
  finish its last few games.
