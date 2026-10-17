# TODO / roadmap

If you're asking "where are we?", this is the answer. The plan comes first,
in order. Finished work is logged in [WORKLOG.md](WORKLOG.md); the older
sections further down are the record of how we got here, and their open
items are gathered into the plan.

## The plan (consolidated 2026-10-15)

Where we are: every table of 5 pieces or fewer is solved, both kinds
(FIVE-PIECE.md, FIVE-PIECE-PAWNS.md), and the engine plays them by the
50-move rule. It plays on Lichess as matthewcarven (blitz ~2026) on a
Texel-tuned evaluation (+208 Elo), then the network nnue1 (+742 over that),
and from its next start nnue1-h512 (+76 over nnue1, 2026-10-17), with
opening books of its own. Real games
set against the tables (all of September 2026, 5.23 M games reaching them):
26.6% end otherwise than perfect play from there would, and 1 move in 31
that could go wrong does.

### Running in the background (check them, don't start them)

- **The bot, the book learner and the month queue**: `start.cmd` starts all
  three, each in its own window, and skips any already running. Logs:
  bot/logs/bot-<date>.log, bot/logs/restarts.log (the network guard),
  bot/logs/learn-<date>.log, games/month-queue.log. Restart the bot only
  between games. The laptop must stay awake (plugged in).
- **The month queue** (`scripts\month-queue.cmd`; Matthew 2026-10-13: "queue
  the lot up"): newest first, 2026-09 back to 2022-12 (46 months, ~1.4 TB,
  ~4.3 B games, ~16 days at ~1 MB/s) onto the desktop share (Z:\lichess,
  each with Lichess's SHA-256), stopping before the share has < 100 GB free;
  each month counted into the books as it lands (Jul-Sep 2026 in: 497,607
  main moves, median line 15 plies). Restartable. Restarted 17:38 on
  2026-10-15 with June at 23.96 of 28.24 GB. **Capped at ~500 KB/s** from 2026-10-15
  (`-LimitRate`, so the house keeps its internet): the rest takes ~28 days
  rather than ~16; `scripts\month-queue.cmd -LimitRate 0` is full speed. Later: `-Parallel 2` (two downloads ran 1.30
  MB/s together v ~0.85 alone: ~16 days -> ~11); his spill-over drive (`-Dir`
  a list, filled in turn); recount the book deeper (16 moves) once ~12
  months are in. The whole archive is 165 months, 2.6 TB, 8.22 B games.

### Handoff: start here (2026-10-17, 23:25; NNUE updated 2026-10-18 03:00)

**Since 23:25**: Matthew rebooted and fast-forwarded compression; the bot,
the learner and the queue restarted at 23:26, the bot on nnue1-h512 (its log:
"768 -> 512 x2 -> 1, sha256 8a6cd3aff627"). The desktop trains two levers
overnight (NNUE, below).

**Running right now, each in its own window (check them, don't start them):**
- **The bot** on nnue1-h512 (restarted 23:26 on 2026-10-17 by `start.cmd`
  after a reboot), with the **rejoin fix**. Its first ~30 rated games against
  nnue1's are the next check (it searches ~9% slower; the match says it's
  worth it).
- **The month queue** (restarted 23:26): June and May 2026 are counted into
  the books (1,695,521 moves in 1,179,279 positions from 7,959,778 games, five
  months); it is downloading April (29.3 GB, resumed from 3.1 GB) at 500
  KB/s. Log games\month-queue.log; counts logs
  bot\books\month-book-<month>.log. Share reads are capped at 8 MB/s.
- The book learner (bot\learn.py, restarted 23:26).
- On the desktop: task "nnue h512-blend25 then h512-mates" (NNUE, below).

**Tonight's two matches, both with the bot off:**

| match | games | +W -L =D | score | Elo [95%] |
|---|---|---|---|---|
| the bot as it plays (nnue1, 8 threads, book, tables) v Stockfish-2500, 3+2 | 10 | +10 -0 =0, all mates | 100% | at least ~+183 (one-sided, binomial) |
| nnue1-h512 v nnue1, 10+0.1, one thread each | 285 | +114 -53 =118 | 60.7% | **+76 [+45, +107]**, SPRT [0, 10] accepted H1 |

**The bot switched to nnue1-h512** (Matthew's G(a), ~23:20): branch
`claude/nnue-desktop-8d5f2a` fast-forwarded into compression, then `start.cmd`
after the reboot; make-config.py tries bot\eval\nnue1-h512.nnue first
(delete it to go back to nnue1).

**NNUE, where it stands** (session 2b):
- **The desktop's GTX 1660 is the second trainer**, driven from here over
  SSH: `scripts\nnue-desktop.ps1 sync | train <run> [options] | status |
  stop | fetch` (a scheduled task per run on the desktop, September's records
  on its own D:, nothing downloaded). 0.65x the laptop's card: a 256 net
  takes ~1.9 h there, a 512 net ~3.4 h. With the heat rule below, new nets
  train there, not here. Its card showed no brownouts (Matthew asked).
- **nnue1-h512** (512 hidden, Matthew's F(b)): held-back error 0.013664
  against nnue1's 0.014851 (-8.0%), +76 Elo over it. NNUE.md's session 2b
  and MATCHES.md have it all.
- **Training on the desktop now** (from 02:43 on 2026-10-18, Matthew's (a)):
  task "nnue h512-blend25 then h512-mates", two session-5 levers at 512
  hidden, one after the other: the result blend (0.25), done ~06:30, then
  mates included, done ~10:00 (the card ran at 1,200 MHz, not 1,920, until
  he closed his PySynthRack at ~04:20: close it before training). `scripts\nnue-desktop.ps1
  status h512-blend25` (or h512-mates); `stop <run>` ends the task if he
  needs the GPU (`train <run> ... --resume` carries on from the last pass).
  Then: `fetch` each, and a match against nnue1-h512 each
  (`scripts\nnue-match.cmd h512-blend25 nnue1-h512`, ~30 min with the bot
  stopped, or longer beside it: his call). Other levers waiting: more data
  (July and August), the export's rounding.

**Watch out:**
- **Memory**: commit was down to 2.8 GB free at 23:20, with the Claude app's
  processes taking most of it. Check `FreeVirtualMemory` before anything
  heavy. A test run beside June's first count killed it (MemoryError, 11:16).
- **Heat**: no NNUE training beside the bot's games (Matthew, until he has a
  notebook cooler): the desktop trains.
- **The share**: all traffic to it at or under ~8 MB/s in total, or the
  laptop falls off the internet (scripts\capped-copy.py for big copies).
- **Pushing**: Matthew is holding back pushes for a few days on purpose, so
  rivals can't copy the bot. Don't nag.

**Waiting on Matthew:** the merge and the bot's start above; delete the
laptop's copy of May (games\lichess_db_standard_rated_2026-05.pgn.zst, 29.7
GB; it's on the share and checked); July's lines.u16 and old plain columns
(4.1 GB); where the whole index lives (a proposal card exists).

### Now (2026-10-15)

**State at 13:30 on 2026-10-15** (after a reboot at 05:16): August's
SHA-256 matches Lichess's (checked by hand 05:19-06:03 with
`scripts\verify-month.cmd 2026-08`, and again by the queue at 09:55), so
the "DAMAGED" at 04:54 was the share going away. `start.cmd` ran at 09:12:
the bot, the learner and the queue are up; the queue marked August and
July done and is downloading June (28.2 GB). **July's index run finished
at 14:54, every check passing** (results in WORKLOG, "July's index run").
**Written up 18:14** as [scripts/index-2026-07.txt](scripts/index-2026-07.txt).
It found **13 games short** of Lichess's counts.txt (89,288,408 v 421): a
frame's end falling inside an `[Event` line glued that game onto the one
before, and `check` couldn't see it because `get` joined the same way. Fixed
in both, with scripts/test-index-month.py. The long duplicates are bots
(96.8% BOT v BOT). Format: Parquet, which `rows` now writes directly, with
the count against counts.txt printed every run (evening). **July ran again
with the fix, 18:39-20:58: 89,288,421 games, Lichess's count exactly**, every
check passing (the write-up has both runs). **And again with the new `rows`,
2026-10-16 03:33-04:56: Parquet rows, count MATCHES, every check passing**, so
session 2 can read July. Pondering merged too (ddc54c8). **Matthew decides where the
whole index lives** (~1.27 GB of rows a month, ~58 GB for 46 months; laptop
~71 GB free, share ~1.4 TB).

1. **Done: the Texel fit on September, no gain.** sf2 (13.58 M positions)
   v sf1 (2.86 M): +668 -662 =670 over 2,000 games, Elo +1 [-11, +13]; the
   SPRT never decided. Five times the data taught nothing more: the bot
   keeps sf1, and the next gains need new evaluation terms (step 2b).
2. **Done: the build plan for the index**, [INDEX.md](INDEX.md): what a
   sample of July says, the files and formats, session 1 in full, sessions
   2-5 outlined.
3. **Index session 1 and 1b: done.** July ran 13:33-14:54, and again with
   the 13-game fix 18:39-20:58 (89,288,421 games, Lichess's count exactly);
   [scripts/index-2026-07.txt](scripts/index-2026-07.txt) has both. Left:
   - [x] Run July again with the 13-game fix: from a snapshot of the
         scripts at ee66600 (an 18:18 start died at 18:35 for memory, from
         another session's probes; WORKLOG). 89,288,421 games, SHA-256
         matching, check 4,000 of 4,000, dups 2.05% as before.
   - [x] **May 2026 is on the share**, its SHA-256 matching Lichess's there
         too (2026-10-16 13:14; copied 09:07 on, then checked at the 8 MB/s
         cap). The laptop's copy (games\lichess_db_standard_rated_2026-05.pgn.zst,
         29.7 GB) can go now: Matthew's delete. June's .part waits on the
         share; the month queue isn't running. Not counted into the books yet.
   - [ ] **The bot is off** (Matthew, 2026-10-17): the laptop ran warm with the
         NNUE training and the bot's games together. Back on once training is
         done, or with a notebook cooler; not beside a training run.
   - [x] **Share traffic at or under ~8 MB/s** (Matthew, 2026-10-16: faster
         knocks the laptop off the internet). make-book.py's Steady now
         paces reads of a network path at 8,000,000 bytes/s (CB_SHARE_RATE
         sets another; 0 = none): month counts, verify, index-month rows.
         Measured at a 4 MB/s setting: 4.06 MB/s. Left to do:
      - [ ] A copy to the share needs its own cap (robocopy /IPG, or a
            Python copy through the same pacing): May's /Z copy happened to
            run at ~2.7 MB/s.
      - [ ] index-month's `check`/`get` read frames with plain open(): ~5.7
            MB/s on average over July's check, but in 4.7 MB bursts.
      - [ ] The cap is per reader, so one big share job at a time (the
            month queue's download writes only ~0.5 MB/s).
   - [x] **lichess-bot gave up on a game whose stream dropped** (2026-10-16
         07:52, TroutBot; 2026-10-17 11:04, PeachFruit, lost on time). Fixed by
         bot/lichess-bot-rejoin.patch (start-bot.cmd applies it beside the
         chat patch): a stream that ends or fails while the game isn't over
         asks Lichess whether the game is on, and if so opens the stream
         again (up to 10 tries) and plays on from the state Lichess sends.
         bot/test-rejoin.py drops a pretend game's stream mid-game: the
         original lichess-bot leaves and the test fails at 60 s; patched, it
         rejoins and wins. After a lichess-bot update, run the test again.
   - [x] **July's rows in the new layout** (2026-10-16, 03:33-04:56, the
         repo's index-month.cmd): 6,177 frames, **89,288,421 games, count
         MATCHES**, SHA-256 matching, 1,267.1 MB of Parquet; check 2,000 of
         2,000 games byte for byte and every row right in all 11 columns
         against the game parsed again; dups as before (2.05%). The read ran
         at 11.4 MB/s beside the bot on 8 threads.
   - [ ] Where the whole index lives: Matthew's call.
   - [x] `rows` writes Parquet directly (10 M games a part, zstd, through
         DuckDB) and the plain .zst columns are gone; `check`, `dups`,
         `trial` read the Parquet, and `check` also compares every sampled
         game's row with the game parsed again. Every month's count is
         compared with counts.txt automatically: the download saves
         `<name>.count` beside the `.sha256`, `rows` prints MATCHES or SHORT
         BY n. Done 2026-10-15 evening (WORKLOG); July-September's `.count`
         are on the share.
   - [ ] **June's `.count`**: its download started before the change, so it
         has none. Once June has landed: `pwsh -File
         scripts\download-lichess-month.ps1 2026-06 -Dir
         \\id10terror-desktop\chessbruteforcer\lichess` saves it and
         downloads nothing (never while June's .part is still growing: it
         would start a second download). May onwards get theirs as they land.
   - [ ] Can go now (Matthew's delete), 4.1 GB in games/index/2026-07:
         lines.u16 (2.86 GB; the lines trial is done) and the old plain
         columns in games/ (1.27 GB; scripts/clean-up.ps1 offers them now the
         rows are Parquet). The new `rows` replaced parquet/ and cleared tmp/.
   - [ ] For the thesis: BOT v BOT games replay whole games (96.8% of the
         duplicates of 41+ plies; 0.46% of all games). Set them apart when
         counting how often positions happen.

### 1. The index over the games (the main line)

Matthew's idea (2026-10-13, extended 2026-10-15): index files pointing into
the big files, so that games and positions can be found and counted
without reading everything; duplicates found by fingerprints and run
lengths; a map from beginning to endgame. He took all three additions
(fingerprints per game, the bow-tie map, Parquet rows) and left the sort
order to us. Numbers and reasoning: WORKLOG, 2026-10-13 late and 2026-10-15.
**The build plan, session by session, is [INDEX.md](INDEX.md)**; what's
below is the summary.

- **Where the data is**: Lichess's months are pzstd files of independent
  32 MiB frames (~4.7 MB packed, ~14,400 games starting in each, each
  unpacks alone in ~90 ms), so pointers go straight into Lichess's own
  files (no second copy) and a month can be worked a frame per thread.
- **Two kinds of record, not one file per sort order.** *Per game, a row of
  columns* (Parquet via DuckDB, which does the run-length, gap and
  dictionary coding and sorts or groups by any column on demand): month,
  frame, game number, plies, result, ratings, speed, termination, the
  game's fingerprint, where it enters the tables (ply, material, verdict),
  and later where it leaves the shared opening. Moves played and material
  are columns, not sort orders. *Positions, sorted by fingerprint* (64-bit
  hash; runs of equal ones become counts, the gaps between sorted ones take
  ~4 bytes against 8), kept only where positions repeat: the opening and
  the last few pieces (2013: 84% of positions seen once).
- **Collisions**: ~1 pair of different positions sharing a 64-bit
  fingerprint a month (~6 B positions), ~2,300 over 46 months, so an exact
  lookup replays the game it points to and confirms.
- **The shape (the bow-tie)**: openings shared (the median game is new from
  ply ~8, a ply deeper per doubling of the games), middles almost never,
  endings converging by material at 5 pieces and exactly at 4.

Sessions, in order:
- [ ] **1. Game pointers** (July 2026, on the share): per month a frame
      table (offset, first game starting in it: ~6,200 rows, ~75 KB) and
      each game's row; fetch random games by number and check them against
      a straight read. Each game's fingerprint (its moves hashed), so
      duplicate games are a sort and a count (2013: 0.8% exact repeats). On
      trial: the rows in Parquet through DuckDB (`pip install duckdb`)
      against a plain binary file, for size and speed; keep whichever wins.
      *Run and written up 2026-10-15 (scripts/index-2026-07.txt); Parquet
      chosen; July to run again for 13 games a frame cut lost (item 3 above).*
- [ ] **2. The measurement**, on 16 threads (a frame each; C#, since
      replaying ~6 B moves a month needs it): per ply, positions, distinct,
      seen 2+ / 10+ times; materials passed through; positions with <= 5 /
      6 / 7 pieces and their repeats. Then pick the cut-offs (12-24 plies;
      5-7 pieces) from the sizes.
- [ ] **3. The opening layer**: positions sorted by fingerprint in 64 KB
      compressed blocks with an index up front (like the CBC1 tables) and a
      block cache; counts, results, rating bands, next moves; a book from it
      for the bot. Each game's opening exit (the last position at least 2 /
      10 games share) goes into its row. Try Matthew's CSharp-Lookuptables
      (`..\Lookuptables`, .NET 10; github MatthewCarven/CSharp-Lookuptables)
      for the by-moves side: its burst trie over each game's moves as bytes
      makes "every game that began like this" one prefix query, and its path
      compression suits the long unique middles. Measure it against sorted
      blocks. (It targets .NET 10 and we target .NET 8: it gains a net8.0
      target, or we move up.) Its disk result, a small index in RAM pointing
      into one flat file, is already the shape of the game pointers.
- [ ] **4. The endgame layer**: material -> games and the ply they reach
      it; exact positions with few pieces, joined to the tables (the
      takeover's verdicts, already batched, go into the rows).
- [ ] **5. The bow-tie map** (Matthew's "a map from beginning to endgame"):
      each game stitched at the two knots, its opening exit x its table
      entry x its result. With both in the rows it's a group-by: from this
      opening, which endings, and how they score.
- Then the queue's count step becomes count + index: one read a month.

### 2. The engine's strength

- [ ] **Texel tuning** (WORKLOG has it all; step 1, human results, gained
      nothing; step 2a, Stockfish's verdicts, +208 Elo [+130, +311], the bot
      uses bot/eval/eval-sf1.txt via EvalFile):
      - [x] The September fit and match: no gain (sf2 = sf1, Elo +1
        [-11, +13] over 2,000 games); the bot keeps sf1.
      - [ ] Confirm at a slower clock (60+0.6; the bot's speed is 3+2), and
        a result/eval blend (`--lambda 0.2`).
      - [ ] Bake the winner into the engine's built-in numbers (`takeover`,
        `hard` and the match commands use the built-in ones).
      - [ ] Step 2b: new terms (passed / doubled / isolated pawns, mobility,
        king safety), tuned the same way towards Stockfish. "Where our
        losses turn" says which matter.
- [ ] **An NNUE evaluation, trained on the laptop's GPU** (2026-10-16; the
      build plan is [NNUE.md](NNUE.md)): the next step for the evaluation,
      since sf2 showed the Texel shape is the limit. Measured: today's
      evaluation is 5-11% of search time, so a 768 -> 256 x2 -> 1 network in
      its place costs 9-18% of the speed (upper bound); September alone
      holds ~224 M labelled quiet positions (sf2 used 13.6 M). Sessions: 1
      data, 2 trainer (PyTorch, the 2.0 GB wheel, capped), 2b the desktop's
      GTX 1660 (6 GB) as a second trainer, driven over SSH from 2026-10-17
      (nothing downloaded in the end), 3 the network in C# (1 and 3 can run side by side), 4
      the match against sf1. **Matthew chose (2026-10-16): A(a) PyTorch,
      B(a) 768 -> 256 x2 -> 1, C(a) September, all speeds, D a + c** (the
      laptop alongside the bot, then the desktop).
      - [x] **Session 1, the data** (2026-10-16, branch
            `claude/nnue-data-7d1bea`): `nnue data`, September in
            games\nnue\2026-09\ on this laptop: 236,940,450 records (223.4 M
            scored in centipawns + 13.6 M mates), 7.58 GB, 41.2 min; 39 of 39
            mutations red. [scripts/nnue-data-2026-09.txt](scripts/nnue-data-2026-09.txt).
      - [x] Swiss tournament games (1.07% of analysed games) are rated but
            their Event lacks "Rated": Matthew's (a), they count now in both
            `tune positions` and `nnue data` (`StoredGame.IsRated`), and
            September was run again with them (+2.7 M records).
      - [x] Merged into compression on Matthew's word (2026-10-16, 23:25,
            f977646), with session 3's branch after it.
      - [x] **Session 2, the trainer, and nnue1** (2026-10-16/17, branch
            `claude/nnue-trainer-f0d60e`): PyTorch on the laptop's GPU
            (`scripts/nnue/train.py`, `checks.py`, `bot_speed.py`,
            `setup-venv.cmd`, `scripts\nnue-train.cmd`). Every check passed.
            **nnue1's held-back error is 0.0149 against sf1's 0.0304** (-51%,
            as the engine computes it). `games\nnue\nets\nnue1.nnue`, network
            SHA-256 4a18e09a...9cf497. WORKLOG and NNUE.md have the run.
      - [ ] Merging the branch is Matthew's. After it, run
            `scripts\nnue\setup-venv.cmd` once in the main checkout (the venv
            was made in the session's worktree; pip fetches ~9 MB, the wheel
            is local).
      - [x] **Session 4, the match** (2026-10-17, branch
            `claude/heuristic-poincare-2d9fec`): **nnue1 +742 Elo [+645, +970]
            over sf1**, +392 -3 =5 in 400 games at 10+0.1, after SPRT [0, 10]
            accepted H1 at game 113 (+114 -0 =3). Clean games, a normal
            search (depth 9.4 v 9.1, 13% more nodes a second).
            `scripts\nnue-match.cmd`; MATCHES.md, WORKLOG and NNUE.md have it.
      - [x] **The bot onto nnue1**: Matthew chose E(a) (2026-10-17).
            `bot\eval\nnue1.nnue` (in git, 395 KB), and make-config.py prefers
            it over eval-sf1.txt (delete it to go back). Checked: make-config.py
            on a copy of bot\ picks the net, then sf1 without it, then nothing;
            the engine loads it from that path (network 4a18e09ae654) with 8
            threads and the tables.
      - [x] **Merged and restarted** on Matthew's word (2026-10-17): the
            branch into compression (7b2af26, suite 410 passed), the bot
            started 04:57:57 by bot\start-bot.cmd in its own window (it had
            been stopped since 03:31, so no game was cut). Its engine said
            "evaluation: network ...\bot\eval\nnue1.nnue (... sha256
            4a18e09ae654)", the match's net; 292 tables; online 04:58:05.
            The learner and the month queue weren't running and weren't
            started.
      - [ ] **nnue1's first ~30 rated games** against sf1's record (NNUE.md
            step 4): the bot's own games in bot\games from 04:58 on 2026-10-17.
      - [ ] **The SPRT can't stop on a perfect score**: `MatchStats.
            LogLikelihoodRatio` returns 0 when the variance is 0, which it
            is while every game is a win (nnue1's first 112 games: LLR 0.00,
            then 362 at the first draw). A small floor on the variance, or
            the trinomial form, with a test of 30 straight wins.
      - [ ] **Training beside the bot**: at full speed the GPU (~99 W) and the
            bot's 8 threads hit the 180 W charger and the bot loses ~22%; at
            half duty a bench loses ~10% and the bot's own speed nothing
            measurable. `nnue-train.cmd` runs at full speed unless told:
            beside the bot, pass `--duty 0.5` (or write 0.5 into
            games\nnue\nets\<run>.duty). Runs wait for AC by themselves.
      - [x] **Session 2b, the desktop's GTX 1660 as a second trainer**
            (2026-10-17, branch `claude/nnue-desktop-8d5f2a`): the wheel,
            torch's dependencies (from the laptop's venv) and September's
            records copied to the share at <= 7.5 MB/s
            (`scripts\capped-copy.py`), SHA-256 checked on the desktop's
            disk; a venv there with nothing downloaded; runs as scheduled
            tasks (`scripts\nnue-desktop.ps1 sync | train | status | stop |
            fetch`). nnue1's first two passes replayed there: held-back
            0.016734 / 0.016159 against 0.016731 / 0.016233, gaps the size a
            new seed makes; the desktop repeats itself to the byte. **634-641
            k positions a second** (0.65x the laptop's card; nnue1's recipe
            ~1.9 h), 362 k at 512 hidden. NNUE.md and WORKLOG have it.
      - [x] **nnue1-h512** (Matthew's F(b), 2026-10-17, 07:46-11:10 on the
            desktop): nnue1's recipe at 512 hidden. **Held-back error
            0.013664 against nnue1's 0.014851 (-8.0%)**; in
            games\nnue\nets\, loads through EvalFile; `bench 9` -4.0% a node
            against nnue1, a 2.8% bigger tree.
      - [x] **nnue1-h512's match against nnue1** (2026-10-17, 22:48-23:17,
            the bot off): **+76 Elo [+45, +107]**, +114 -53 =118 in 285
            games, SPRT [0, 10] accepted H1; 8.9% slower in play. MATCHES.md.
            Matthew's G(a): the bot switches to it at its next start
            (bot\eval\nnue1-h512.nnue, make-config.py tries it first).
      - [ ] **nnue1-h512's first ~30 rated games** against nnue1's (the
            bot's own games in bot\games, from its next start).
      - [ ] `scripts\nnue-train.cmd` passes only `%2`-`%9` to train.py, so a
            ninth option token is dropped silently (train-desktop.cmd lost
            `--progress 2` that way before it was fixed with `for /f
            "tokens=1,*"` over `%*`). The same fix there.
      - [ ] A full-length reproducibility check (decision F's (a)): nnue1's
            recipe on the desktop, ~1.9 h, for the end-of-run spread between
            two runs of one recipe, the noise floor for judging levers by
            held-back error. When the desktop is free.
      - [ ] A small lever (session 5): the export's rounding leaves nnue1 a
            constant -5 cp for the side to move (QB 64, forced by one output
            weight at the clamp). Refit the output bias after rounding, or
            train the output weights with their rounding.
      - [x] Session 3, the network in C# (2026-10-16, branch
        `claude/nnue-csharp-8117d8`, merged into compression the same night): EvalFile
        takes a .nnue; 38 tests, 31 of 31 mutations red; `bench 9` -3.8% at
        256 hidden against sf1 with bounds-checked slots and columns (-2.3%
        without; the probe's bound was -9 to -18%). The 08:02 access
        violation was a battery mutant (WORKLOG). The bot stays on sf1
        until session 4's match.
      - [ ] Later speed steps, each measured with scripts/nnue-bench: AVX-512
        (this CPU has it), 64-byte-aligned weights and stack, a lazy update
        (only nodes that evaluate pay for their accumulators).
- [ ] **Where our losses turn** (Matthew, 2026-10-15): Stockfish 16 over the
      bot's lost games (bot/games), finding in each the move where the game
      turned (e.g. the first of ours that loses 150 cp or more by
      Stockfish). Then sort them: missed tactics (Stockfish sees the
      refutation a few plies past where our search reached: depth) against
      misjudged positions (at our own depth our evaluation disagreed with
      Stockfish's: the evaluation, new Texel terms). By opponent rating and
      time control; the bot's log has our evaluation and depth for every
      move. First examples, 2026-10-14 night: latrunculi-bot 2329
      (SrwnQEj5: +1.1 at move 17, turned at moves 21-24), Musashi-thebot
      2309, TroutBot 2211, JenniferX 3037 (rapid). That night: blitz v 2200+
      lost 3 of 3, v 1780-1915 won 5 of 5.
- [ ] **The book's worth, measured** (Matthew asked for it): an overnight
      `scripts/book-match.py` run, e.g. `--games 400 --tc 180+2
      --concurrency 3` (~1.2 GB per engine copy: check commit first; not
      alongside other big jobs). Smoke-tested. Against UCI_LimitStrength
      Stockfish the book lasted only ~4 of our moves (it plays odd moves),
      against Lichess bots ~8; if that dilutes it, give Stockfish the book
      too, or a full-strength Stockfish at a short movetime. The bot's own
      rated games: 34% in 19 before the book, 46% in 42 with it (+-10 each).
- [ ] **The book learner** (bot/learn.py, from 2026-10-14; 162 positions by
      21:47 that night): measure after a week, ~2026-10-21 (`learn.py
      --summary`): how often its book answers, and moves of the wide book it
      vetoed. It covers our first 12 moves only; middlegame losses don't
      teach it anything.
- [ ] **The bot on sf1**: its rated record after ~30 games (5 of 5 won at
      first; the book era 53% v ~1990). Check `!info` / `!help` from his own
      account in a casual game.
- [ ] **Threads for the bot** (2026-10-15, branch
      claude/nostalgic-greider-09538a, not merged; WORKLOG 2026-10-15
      21:25): UCI `Threads` (default 1) and `ThreadSplit` (LazySMP, or Ranges,
      Matthew's split). Against one thread at 10+0.1, 40 games each: LazySMP
      on 8 +215 Elo [+124, +344], Ranges on 8 +168 [+95, +259]. Both ~2x to
      a fixed depth; the narrow windows themselves are worth ~1.3x at most
      here (Baudet's 5-6x doesn't hold), so LazySMP. Matthew's call:
      - [ ] The bot's clock: one thread v LazySMP 8 at 300+2, 20 games one at
        a time, ~4-4.5 h (not alongside an index run: they'd skew each
        other). Or head-to-head, LazySMP 8 v Ranges 8, to settle the 47 Elo.
      - [x] 8 threads with the 5-piece tables loaded (2026-10-16, branch
        claude/practical-cerf-df4df1 on top of the threads branch; WORKLOG
        2026-10-16 00:16): they did queue. Under one lock a compressed table,
        LazySMP 8 reached depth 12 in 20 endings 1.09x as fast as one
        thread, its threads waiting on the lock 70% of the time. The cache
        is read without a lock now (a version a slot): 2.08x and 6.90x the
        nodes a second, like the middlegame's 1.95x and 6.96x.
      - [x] The block cache itself (2026-10-16, branch
        claude/angry-kirch-aa4068 on top of practical-cerf; WORKLOG
        2026-10-16 01:11): every read traced and replayed. DTZ reads were a
        third of the misses and 99% of them changed nothing (a mate within
        100 plies of the count settles the rule), so DTZ is read only where
        it can matter; and one cache for every table, 512 MB (UCI
        `TableCache`), sets of 16 with a clock, read without a lock. Blocks
        a search 20,731 -> 6,358; every arm ~2x faster (one thread 1.96x,
        LazySMP 8 2.20x), 8 threads 2.40x one thread and 6.98x its nodes a
        second; peak 2.9 -> 1.9 GB, and memory now has a ceiling.
      - [ ] Then merge: all of it is on claude/threads-and-tables (from main,
        2026-10-16), with `"Threads": 8` in the bot's uci_options
        (bot/make-config.py) on the same branch, so the setting arrives
        with the engine that has it (python-chess refuses an option the
        engine doesn't list, and start-bot.cmd builds the engine from the
        main checkout). Merge into main, bring compression up to it, then
        restart the bot between games. Hash 256 is shared by all the
        threads; the table cache (512 MB) by all the tables, so the bot
        needs ~0.5 GB more than its hash and plain tables at most, not up
        to 14 GB.
      - [ ] Smaller blocks (a step of its own, a night; WORKLOG 2026-10-16
        01:11): recompress the 220 compressed tables in 8 KB blocks (16 KB
        the cautious choice). Estimated from sampled blocks: files 1.19x
        (~12 -> ~14 GB), a block 0.15x the time, decompression a search
        ~790 -> ~180 ms, a one-thread ending ~1.9 -> ~1.3 s, and 128 MB of
        cache would do. Needs the reader to take the block size from the
        header (it holds it; only the 64 KB check goes), cache places of
        that size, and 4-byte block offsets (~55 MB in memory at 8 KB).
        Recompress from the compressed files, one at a time, each checked
        against the old before it replaces it.
- [ ] **Pondering** (2026-10-15, branch claude/vigilant-murdock-d223f5, built on
      the threads branch; WORKLOG "pondering, and what it's worth"): the engine
      thinks on the opponent's time (`go ponder`, `ponderhit`, `bestmove ...
      ponder`). Against itself at 10+0.1, ponder on v off: 119 games, +48 -20
      =51, **Elo +83 [+37, +133]**, the reply right 71.5% of the time (on
      Lichess 51%, so expect less there). Merged into compression 2026-10-16
      (ddc54c8; 341 tests pass).
      - [x] Turned on in the bot, on Matthew's word (2026-10-16, 06:00):
            `engine["ponder"] = True` in bot/make-config.py, which
            start-bot.cmd writes into config.yml on its next start (with the
            engine rebuilt from this checkout). Watch its first games: moves
            on time, the bot's log free of ponder errors, the clock it banks.
      - [ ] A clean re-run (the harness lost one game to a script bug, since
            fixed), or longer (~400 games, ~1 h), once no index run is going.
      - [ ] Spend the banked clock: the pondering side ended games with 4.8 s
            of 10 left, the other 2.1. Stockfish plans 25% more time a move
            when Ponder is on; try that, measured the same way.
      - [ ] A Lichess-like hit rate: ponder on and off each against a third
            engine (Stockfish, as book-match.py does), not each other.
- [ ] Later: speed (bitboards and legal-only move generation; the bot
      searches ~1.7 M nodes/s, depth 12-14 in blitz), pondering (a session
      on it from 2026-10-15, building on the threads branch);
      the table holds 16-byte slots but is sized at 24 a slot (Hash 256 uses
      ~171 MB; sizing it at 16 changes the one-thread search, so measure);
      the hash keying en passant only when a capture is legal (now it keys
      it after every double push, so a repetition through one can be missed:
      WORKLOG 2026-10-15);
      the evaluation scaled down as the 50-move count rises; a server near
      Lichess (Europe) if it should run all day (from Australia each move
      costs ~1.2 s of connection).

### 3. Findings for the thesis

- [ ] New examples for "Endgames Thrown Away" from the month
      (scripts/takeover-moves-2026-09-month.txt; ratings of exactly 3000 are
      likely bots at their provisional start), if Matthew wants them.
- [ ] How much slower the engine's rule-safe play mates than the fastest
      mate.
- [ ] Export the principal tree from a position, to a chosen depth, as a
      file the thesis can use.
- [ ] 6 pieces for human games: the Syzygy 6-piece set (~150 GB) to judge
      them (8.2% of games reach 6, 29.5% of those endings go wrong); our own
      6-piece build would need ~128 GB of memory. Not for the bot: its games
      ended as perfect play says in 38 of 38 at 6 pieces.
- [ ] Repetition, measured: a perft that refuses repeated positions, and
      every repetition in stored games once the index has positions.

### 4. Matthew's (flagged, his to do)

- [x] Push `compression` (GitHub matched d13b55c at 00:47 on 2026-10-15).
- [ ] A second pull request from `compression` into `main` (PR #1 took only
      the first commit).
- [ ] Pondering for the bot, if wanted (section 2): merge
      claude/vigilant-murdock-d223f5 (it carries the threads branch too), build
      the engine into bot/lichess-bot/engines/bruteforcer, and set `ponder:
      true` in bot/lichess-bot/config.yml; restart the bot between games. The
      ponder search takes a core for the whole game, from the learner and any
      index run.
- [x] Push `prefix-range` in ..\Lookuptables and merge it: done 2026-10-15,
      CSharp-Lookuptables PR #1 (b9ca823); his local main brought up to it.
- [ ] games/2026-09-part.cbg (1.28 GB) can go now the whole month is done;
      keep games/2026-09-part-tables.cbg (64 MB) as the takeover check file.
- [ ] Keep the laptop awake on long runs (a Windows power setting; it slept
      48 h of the pawn-table build).
- [ ] A second memory stick, due in about a week (from 2026-10-15). The laptop
      (Acer Nitro AN515-57) has one Samsung M471A2K43EB1-CWE, 16 GB DDR4-3200
      SO-DIMM, in channel A, with the second slot free: a matching 16 GB
      makes 32 GB, in both channels. Fit it between long runs (a full
      shutdown stops the bot, the learner, the queue and any index run).
      Until then commit headroom stays ~2.4-5 GB: one heavy job at a time.

### Done lately (details in WORKLOG)

- 2026-10-17 (night): **nnue1-h512 +76 Elo [+45, +107] over nnue1** (285
  games, SPRT accepted H1), after the bot beat Stockfish-2500 10-0 at 3+2;
  Matthew chose to switch the bot to it at its next start.
- 2026-10-17: NNUE session 2b: the desktop's GTX 1660 trains over SSH
  (scheduled tasks, records on its own disk, nothing downloaded), matching
  the laptop within the size of a seed change at 0.65x its speed. Its first
  full run, nnue1-h512 (Matthew's F(b)): held-back error -8.0% against
  nnue1, -4.0% a node in the engine; its match is session 5's.
- 2026-10-17: NNUE session 4, the match: **nnue1 +742 Elo [+645, +970]
  over sf1** (+392 -3 =5 in 400 games; SPRT accepted H1). Matthew chose to
  switch the bot (E(a)); it plays on nnue1 once the branch is merged and
  the bot restarted.
- 2026-10-16/17: NNUE sessions 1-3: September's 237 M labelled positions,
  the network in C#, the trainer on the GPU, and nnue1 (held-back error
  half sf1's).
- 2026-10-14/15: `game takeover` reads in batches; the whole of September
  move by move: 1 in 31 held, classical 2.69 mistakes per 100, 2400 and up
  2.38, two minor pieces v a queen ~1 move in 5.
- 2026-10-14: Texel step 2a (+208 Elo from Stockfish's verdicts); the book
  learner; chat commands (`!info`, `!help`); the bot's network guard
  (run-bot.ps1); start.cmd; what 6-piece tables would be worth.
- 2026-10-13: the Lichess bot live (matthewcarven); our own opening books
  (main, wide), August and July counted; the month queue and the desktop
  share; the clock and the kinds of mistake.
- 2026-10-12: move by move inside the tables (1 in 31); the hard positions
  at 1 s a move.
- 2026-10-05: all 50 pawn tables, so every 5-piece table is done.

## Then: compression, then the pawn tables (agreed 2026-09-29/30, all done)

All 60 pawnless 5-piece tables are done (FIVE-PIECE.md). Next are the 50
5-piece tables with pawns, but they don't fit: 115 GB as stored (81.5 GB
with 48-square pawns) against ~103 GB free. Matthew chose compression first
over buying a drive (a drive would have to be an SSD: solving writes and
reads all over the files).

### Step A: compression (done 2026-09-30, branch `compression`)
Measured on our tables, compressed in independent 64 KB blocks (so a probe
still jumps straight to its position): zlib 9.5x (KRPvKR.cbz) to 12x
(KRBvKR.cbt); lzma on a 64 MB sample 17-35x. So every 5-piece table, pawns
included, ~112 GB plain, would be ~11 GB.
- [x] `CompressedTable` (CompressedTable.cs): format "CBC1", same `.cbt` /
      `.cbz` names; values in 64 KB blocks, each Brotli-compressed on its
      own, block offsets up front; reads a block per probe through a
      direct-mapped cache of 1024 blocks (64 MB a table at most), locked.
      `EndgameTable.Load` reads it (mapped, or `intoMemory` decompresses
      all); `SaveCompressed(file, quality)` writes it, compressing blocks in
      parallel and decompressing each again to check it before writing.
      Builds; the 266 tests still pass.
- [x] `compress [dir|file] [--quality N]` in the CLI (2026-09-30): writes a
      temporary file, reads it back and compares every value with the plain
      one, then moves it over; capped tables stay plain. On the four 5-piece
      DTZ tables KRPvKR promotes into: 6.5-17.5x, ~25 s each.
- [x] Tests (13, in CompressedTableTests): every value back (DTM and DTZ,
      a pawn table, identical pieces, and into memory), cache eviction,
      probes and move rankings through compressed tables, solving from
      compressed smaller tables, capped and cut-short files refused, Preload.
- [x] Brotli quality: **10** (2026-09-30). Per 64 KB block, sampled:

      | quality | KRBvKR.cbt | KRPvKR.cbz | time, whole file, 16 threads |
      |---|---|---|---|
      | 1 | 7.4x | 7.5x | ~1 s |
      | 5 | 13.1x | 11.0x | 1 s / 3 s |
      | 9 | 14.6x | 11.4x | 4 s / 13 s |
      | 10 | 18.0x | 13.1x | 23 s / 36 s |
      | 11 | 19.7x | 14.2x | 97 s / 113 s |

      Reading costs the same at every quality: 70-100 us to decompress a
      block. Low and high bytes in two planes: no gain (13.2x v 13.1x at 5).
- [x] Solve speed with compressed smaller tables (2026-09-30): KRPvKR's
      DTZ solved twice at once, from its promotion tables plain and
      compressed: **1,990 s plain, 2,055 s compressed (3% slower)**, process
      peak 1.93 GB v 1.62 GB; both byte for byte the table already on
      disk. So no unpacking needed. A DTZ `verify` of
      KRBvKR (every 10,007th) took 0.9 s plain, 2.2 s compressed.
- [x] Compress the 5-piece tables in `tables/` (Matthew ran
      build-five-piece.cmd, 2026-09-30 02:30-03:18): all 120 files, 28.7 GB
      -> 3.15 GB (9.1x; DTM 10.1x, DTZ 7.7x; 3.6x to 78x). Free space 96 ->
      122 GB. The 36 small tables stay plain.
- [x] KRPvKR's two files compressed too (found CBC1 on 2026-10-15:
      185 MB and 75 MB).
- [x] `build-five-piece.ps1`: compresses both files of each finished table
      (and any finished one it skips), unless `-Plain`; `-List` shows which
      are compressed. Run as it is, it would compress the 60 pawnless tables.
- [x] The engine: `Tablebase.Preload` reads plain tables into memory,
      smallest first, within 1 GB (the 36 up to 4 pieces are 427 MB), and
      leaves compressed ones, and plain ones past that, on disk.

### Step B: which pawn endings real games reach
- [x] `game endings` (2026-09-30; WORKLOG has the full table): of 741,349
      games only 48,138 (6.5%) get down to 5 pieces or fewer. Of those,
      14.3% stay in tables we have; 85.7% pass through a pawn table we don't
      (two-thirds of them are back in our tables by the end, a pawn promoted
      or taken). The most reached of all is K+P+P v K+P (8,719 games, 18% of
      them), and it needs almost every other pawn table first.
- [x] Found on the way: the Windows `tables/` lacked 8 of the 36 small
      tables (the 4-piece ones with pawns, solved only in WSL before). Solved
      in 9 min; 7 byte for byte the baseline, KPPvK equal value for value.

### Step C: rule-aware engine play (Matthew's "wanderer")
Matthew: a brute-forced game is weakest where the opponent (a) flukes, or
(b) knows exactly what they're doing and wanders pieces about to draw
moves out of us. Today the engine plays table positions by mate distance
only and ignores the 50-move clock (`Search.TablebaseMove`, `TryProbe`),
so (b) can run the clock up and then a shortest mate longer than what's
left is a draw.
- [x] At the root of a table position (2026-09-30, `Search.TablebaseMove`):
      with the DTZ tables on hand, winning, the win quickest to its next
      capture / pawn move / mate that still fits the clock (ties by mate
      distance); losing, the one furthest from it; a win the clock would run
      out on is a draw. Without DTZ tables, mate distance as before.
- [x] In the search, `Tablebase.TryProbeWithClock`: a table result counts
      only if the DTZ table says it fits the node's clock.
- [x] Match adjudication by the rule: a cursed win (or a win too slow for the
      clock) is "tablebase draw (50-move rule; ...)".
- [x] The engine's Preload opens the `.cbz` tables too (within its 1 GB:
      both kinds up to 4 pieces are ~850 MB in memory), and a DTZ lookup no
      longer reads the disk mid-search when LoadOnDemand is off.
- [x] Tests: K+R v K at the last clock that still wins, and one ply later
      (both sides); K+P v K at 99, where only a pawn move keeps the win;
      DTZ-optimal play move after move; adjudication; a cursed win in
      K+B+B v K+N (slow test, on the real tables). Three deliberate breaks
      each caught.
- [ ] Maybe: scale the evaluation down as the clock rises (as strong
      engines do), so it prefers progress outside the tables too.

### Step D: the 50 pawn tables
- [x] **Done 2026-10-05** (results in FIVE-PIECE-PAWNS.md): all 50, 38.8
      billion positions, 8.8 GB compressed, 72.6 h of solving over five days
      (the laptop slept 48 h of them). Every table's sample verified; Lichess
      agrees on all 343 positions checked, mate distances and the rule. The
      plan as it was:
- [x] 49 left (KRPvKR is done): 35 with one pawn, 12 with two, 2 with three.
      A table needs the tables its promotions lead to first. Order from step
      B (`game endings`: most games wholly in the tables per position solved):
      first the 15 "three against a bare king" (KQRPvK, KQQPvK, KQBPvK,
      KRBPvK, KQNPvK, KRNPvK, KRRPvK, KBNPvK, KBBPvK, KNNPvK, KRPPvK, KQPPvK,
      KBPPvK, KNPPvK, KPPPvK): 25.5% of the work, and the games wholly in the
      tables go from 14.3% to 41.9%. Then the 25 one-pawn "two against one"
      (54.3% at 83.7% of the work), then the 8 two-pawn ones (81.9%), then
      K+P+P v K+P last (100%). ~49 h of solving; ~115 GB plain, ~13 GB
      compressed at the pawnless tables' 9x (disk holds one plain table at
      a time). **Running since 2026-09-30 03:27** (`build-five-piece.cmd
      -Pawns`, its own window; progress in tables\five-piece.log, `-Pawns
      -List` for what's done). Restartable: close the window, run it again.
      It verifies a sample of each table (every 1009th position, both kinds)
      and stops on a mismatch. Lichess samples and a summary like
      FIVE-PIECE.md after.
- [ ] 48-square pawns (25% per pawn): with compression it matters little
      for disk (the holes compress to nothing); do it only if it helps.
- [ ] Keep the laptop awake for long runs: it slept 48 h of this one (System
      log, Kernel-Power 42). A Windows power setting, so Matthew's to change.
- [x] Next with every 5-piece table in: against Stockfish from the takeover
      positions (WSL), done 2026-10-09 (see "Now" at the top for the rest).

## Done: the session plan (5-piece tables and the depth ladder)

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
- [x] Before the lot, less disk (Matthew, 2026-09-28: "sort them and
      deduplicate"): identical pieces stored once (a pair halves a table,
      three alike a sixth; the counts had doubled them), DTZ in one byte.
      60 tables both kinds: ~58 GB -> ~31 GB. 31 of 36 small tables byte-
      identical, the 5 with a pair and all 36 DTZ equal value for value, the
      ladder too; KRRvKR re-solved fresh = upgraded, 533 s (was 762), 0.95 GB
      (was 1.91). `upgrade` took `tables/` from 8.39 GB to 5.67 GB.
- [x] Done 2026-09-29: all 60 pawnless 5-piece tables, both kinds, 30.8 GB,
      20.9 h of solving (one restart after the window closed). Results in
      FIVE-PIECE.md. Was: 60 material sets (20 of
      three pieces v a bare king, 40 of two v one), both kinds, ~31 GB and
      ~18 h. Matthew said go (option a); he runs
      `scripts\build-five-piece.cmd` (restartable, logs to
      `tables\five-piece.log`, stops under 10 GB free).

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
       game that reached it, and where each came from and went. Now the
       index plan at the top (section 1)
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

### 3c. Lichess bot account (done 2026-10-13: matthewcarven; see the plan at the top)

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
