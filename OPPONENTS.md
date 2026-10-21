# Opponents: choosing the setup per bot (P8, decision O)

Matthew's idea (2026-10-22, P8), in his words: "player data logging and comparison, we will pick our best 3
engines and select the best engine with the least usage for 3 to 10 games and switch before the beginning of
the match at the matchmaking point, some engines may be confused by some of our reasoning and that might lead
to wins, if we find a player that has a particular weakness to an engine we keep using it, if we loose badly
with another choice we can count that as bias against".

His steer after a first draft (the same day): **1. log first**, and test with that history whether
opponent-specific weaknesses exist at all; **2. explore cheaply**, so that each new bot gets the best setup most of
the time, the others are tried now and then, and the bot switches for good only once one is clearly ahead
(Thompson sampling with priors from each setup's overall strength), with his own rule kept beside it for
comparison; **3. vary the opening too**, since bots replay their book lines, which may be the better thing to vary
per bot. This file follows those three steps (a side session; scripts in [scripts/opponents/](scripts/opponents/),
outputs in scripts/opponents/results/). Nothing in src or bot/lichess-bot changed. The options are at the end.

## The short answer

1. **Log first: done.** [collect.py](scripts/opponents/collect.py) records, per game, the setup that played it
   (net, engine build, threads, hash, pondering), the opponent, the ratings, the colour, the time control, the
   opening and the result. It is backfilled for all 617 games. Switching it on for new games takes one line in
   start-bot.cmd (O1). What the history says:
   - **The bot rarely meets the same bot twice**: 196 bots, 68 met once, 36 twice, three 10 times or more.
   - **No bot is weak or strong against us beyond chance.** In the network era (240 games), the best estimate
     of a bot-specific offset is 0 Elo, at most ~100. One bot stands out where luck alone gives 1.5.
   - **No bot's result depends on the net**, nor on the opening: 0 Elo for each, at most ~100. One blitz game
     measures a bot to ±320 Elo, so with a few games a bot, nothing much under ~100 Elo could show.
2. **Explore cheaply: even the cheapest way costs, and never pays.** Simulated over the next 3,000 games,
   weighted by what the data allows:

   | rule | Elo played |
   |---|---|
   | the steered explorer, 5% of games on another setup | -2.3 |
   | Thompson sampling (prior SD 25-50; at 10 it never explores) | -7 to -16 |
   | Matthew's rule as stated | -18 to -23 |

   The steered explorer never switches: a setup tried 5% of the time never gets enough games against one bot to
   be clearly ahead. The prize, if we knew each bot's best net perfectly, would be +16.
3. **Vary the opening: the variety is ours, not the bots'.** From a position they've met before, the bots repeat
   their move 70% of the time; our weighted-random book repeats 40%. So the book learner's moves almost never come
   up: 3 moves in 617 games. Last game's first out-of-book position came back in only 16 of 319 games against a
   bot met before. Replaying our own line from the last game against that bot would bring it back about 23% of
   the time. That costs no strength (book moves either way), but nothing here can say what it would win.

My picks: **O1 (a)**, switch the log on; **O2 (a)**, no per-bot net choice for now; **O3 (b) then (a)**, the
learner onto the bot's net, then a replay trial.

## 1. Log first: the game record

**Recorded already.** bot/games/*.pgn has the opponent and both titles, both ratings and rating changes, the
result and how it ended, the time control and speed, rated or casual, our colour, the opening (ECO and name), the
clocks, and our evaluation and line after each of our moves. bot/logs/bot-*.log (INFO, kept for good) has who
challenged whom, which book gave each book move, every engine move's evaluation, depth and speed, and the chat. At
every start it also has the engine's `info string evaluation: network ...\h1024-3m.nnue (..., sha256
510a8d0e6c43)` line during "Checking engine configuration". lichess-bot's DEBUG log has every UCI line too, but
only for 7 days.

**Missing: which setup played.** The setup only changes when the bot restarts: start-bot.cmd builds the engine
from the main checkout, and make-config.py picks the first net present in bot\eval. So each game takes the setup
of the last start before it. collect.py works that out: the net from the log, and the build and options from the
main checkout's HEAD history at that moment (its reflog), with Threads, Hash and pondering read from that
commit's make-config.py. Uncommitted changes at a start can't be seen.

| column | from | what |
|---|---|---|
| id, utc, rated, speed, tc | PGN | the game |
| colour, opponent, opp_title, our_elo, opp_elo, our_diff | PGN | who, and the ratings then |
| score, termination, moves | PGN | 1 / 0.5 / 0 for us, how it ended, its length |
| challenger | log | "us" (matchmaking) or "them" |
| net, net_sha | log | the evaluation the engine loaded at the start that played it |
| build, threads, hash, ponder | git | the last commit to touch src then, and make-config.py's options |
| bot_start, log_start | log | that start's time, the game's start (local time, UTC+11) |
| beside | log | other games running beside it (two at once from 2026-10-18 until the 22nd) |
| book_main, book_ours, book_wide | log | our moves from each book |
| eco, opening, line | PGN | the opening, and the first 20 plies (to see who repeats a line) |

**Backfilled**: the main checkout's bot\games\record.csv (out of git like the PGNs, rebuilt in ~3 s), 617 games:

| setup | build | threads, ponder | games | bots | when (UTC) | score | our rating, first to last |
|---|---|---|---|---|---|---|---|
| built-in numbers | 226fd0f | 1, off | 145 | 74 | 10-12 to 10-14 04:10 | 51% | 3000 (provisional) to 2009 |
| sf1 (tuned numbers) | 00ad038, 9c8c0c2, a5b38fa, ddc54c8 | 1 then 8, off then on | 229 | 97 | 10-14 to 10-16 | 64% | 2009 to 2208 |
| nnue1 | 8c3a396 | 8, on | 19 | 18 | 10-16 to 10-17 | 75% | |
| nnue1-h512 | 8c3a396 | 8, on | 154 | 85 | 10-17 to 10-18 | 71% | 2260 to 2520 |
| h512-3m | 8c3a396 | 8, on | 26 | 22 | 10-19 | 48% | 2628 to 2607 |
| h1024-3m | 8c3a396 (33), c4274a9 (10), ebfd2df (1) | 8, on | 44 | 36 | 10-19 to 10-21 | 54% | 2530 to 2553 |
| h1024-3m-l16-32 | 3d1d144 | 8, on | 0 | | from 10-22 03:00 local | | |

(The one h1024-3m-l16-32 game, KDKKzauD, has no PGN: the bot was stopped at 03:26 while it was in progress.)
Before the networks the setup moved under a single "net" (sf1 went from 1 thread to 8 and pondering on). In the
network era 232 of 243 games were on one build with the same options, so the nets differ there by the net, by
the opponents (the rating rose) and by two games at once from 10-18.

**Going forward** (O1): start-bot.cmd runs collect.py at each start (stdlib and git, any Python), so the record is
one start behind at most. If a setup ever changes per game (O2 (b)-(d)), the patch also logs one INFO line a game
("setup for game X: ...") and collect.py reads that instead of the start's net.

## 2. Do opponent-specific weaknesses exist?

**The model** ([analyze.py](scripts/opponents/analyze.py)). Each game's result is predicted from our strength with
that net (fitted), the opponent's Lichess rating, our colour and the speed. Draws are handled the way BayesElo
handles them (a fitted draw width; our games: 16% draws). On top of that, each bot gets its own offset *b*: how
much better (+) or worse (-) we do against it than its rating says. The question is how big those offsets are
across bots (their SD, τ). A second offset *c* per bot per net asks whether a bot's offset changes with the net
(SD σ). That is the effect a per-bot net choice would use. The same *c*, with the games grouped by opening instead
(the family, as in "Sicilian Defense"; or the ECO volume A-E), asks whether a bot's results follow the opening.
All are fitted with the offsets integrated out, and the 95% intervals come from the likelihood profile.

| games | bots (3+ games) | τ: bot offsets | vs none | σ by net | σ by opening family | σ by ECO volume |
|---|---|---|---|---|---|---|
| all, 596 with a result | 194 (90) | 80 Elo [0, 120] | p 0.05 | 0 [0, 100] | 0 [0, 75] | 0 [0, 75] |
| networks only, 240 | 115 (37) | **0** [0, 100] | p 0.5 | **0** [0, 100] | **0** [0, 100] | **0** [0, 100] |

(Without time forfeits, all games: τ 70 [0, 120], p 0.12; σ by net 0 [0, 100].) The weak hint in all games
(τ 80, p 0.05) comes from the first four days. On the built-in numbers and sf1, our strength was moving inside one
"net" (1 thread to 8, pondering on, the rating settling from a provisional 3000), and a model with one strength a
net puts that on whichever bots we met then.

**Bot by bot, against chance.** Each bot's score against its expected score, as a z-score. Then the same count
from 400 simulated histories in which no bot is special:

| | bots with 3+ games | beyond 95% (\|z\| > 1.96) | by chance alone | largest \|z\| | by chance (median, 95%) |
|---|---|---|---|---|---|
| all games | 90 | 3 | 3.8 (95%: up to 7) | 3.00 | 2.68, 3.85 |
| networks only | 37 | 1 | 1.5 (up to 4) | 1.98 | 2.50, 3.83 |

The "most surprising" bots (Alieknek 1 of an expected 4.0 in 6 games, CalciumEngine 5.5 of 9.0 in 12,
DeMen100ns-bot 2.5 of 5.5 in 9) are as surprising as the most surprising of 90 bots would be by luck. That is the
winner's curse: rank enough bots and some look special. A rule that "keeps using" whatever did well against a bot
would mostly be chasing these.

**Opening or net, without a model.** Take two games against the same bot. Each game's surprise is its result
minus the expected result. If a bot's results follow the opening, two games in the same opening family should
agree more than two in different families, and likewise for the net. The common part (the bot's own offset)
adds to every pair alike, so only the differences count (covariance × 1000, 95% interval by resampling bots):

| | networks only | all games |
|---|---|---|
| same opening family, minus different | +11.9 [-21.7, +35.3] (14 + 16 pairs) | +17.9 [-13.4, +51.2] (45 + 66 pairs) |
| same net, minus different | +11.4 [-17.4, +39.5] | +13.0 [-6.3, +36.4] |

Neither is told apart from zero, nor from each other: **the data can't say whether results follow the opening
more than the net.** Only 19 (bot, opening family) cells hold 2+ games on the networks, 59 over all games.

**Matthew's sums, checked.** At even strength one game's score has variance 0.19 with our draw rate, so "about
0.2" holds, and so does his table (10 games each: 40 points, ~280 Elo; 30: 23 points, ~160; 100: 13 points, ~90).
Our games are often lopsided (we score 62%), so their variance averages 0.13, but the Elo it buys is the same:
one game measures a bot's offset to ±320 Elo. To be 95% sure (one-sided) that a setup is 50 Elo better against
one bot takes ~220 games each; for 100 Elo, ~55 each. The most-met bot has 16.

## 3. Explore cheaply: the rules, simulated

[simulate.py](scripts/opponents/simulate.py) builds a world like the bot's. Matchmaking picks bots at random from a
pool fitted to how often the bot has met each one (300 bots, uneven popularity: 596 games meet 209 bots, 72 once,
the most 13 times; the bot's own figures are 194, 67 and 12). Each bot has a rating gap to us (mean +30, SD 190),
a common offset (τ 80), and an offset per setup (σ, the unknown, 0 to 150). The three setups differ overall as
measured: h1024-3m 0, h512-3m -41, nnue1-h512 -52 (MATCHES.md). A second set, "a close second" (0, -10, -41),
stands in for a deep net or an h768-3m that comes out near h1024-3m. Every rule plays the same games.

The rules. Each Bayesian rule keeps a belief per bot (its common offset and one offset per setup, starting from
each setup's overall strength, with a prior SD of 10, 25 or 50 Elo) and updates it after every game.
- **Steered explorer**, Matthew's steer: the best setup; another at random in 5% (or 10%) of games against the
  bot; a switch to another for this bot once it is ahead of the best with 95% probability.
- **Thompson sampling**: each game, a draw from the belief, and the setup that comes out on top.
- **Cautious learner**: the setup with the best expected result; never explores on purpose.
- **Matthew's rule as stated**: the least-used setup against this bot for its first 3 (6, 9) games, then the one
  with the best score against it, a loss pulling a setup down.

The tables give **Elo played**: the chosen setup's true edge minus h1024-3m's, averaged over games, which is
roughly what the rating feels. "Weighted" averages over σ by how well each value fits the network-era games
(σ 0: 1, 25: 0.94, 50: 0.75, 75: 0.51, 100: 0.28, 150: 0.05).

| today's three; σ (true) | 0 | 25 | 50 | 100 | 150 | weighted |
|---|---|---|---|---|---|---|
| *next 600 games (~9 days)* | | | | | | |
| always h1024-3m | 0 | 0 | 0 | 0 | 0 | 0 |
| the ceiling (knows each bot's best) | 0 | +3 | +18 | +57 | +98 | +16 |
| steered explorer, 5% | -2.3 | -2.3 | -2.3 | -2.2 | -2.1 | -2.3 |
| steered explorer, 10% | -4.6 | -4.6 | -4.7 | -4.5 | -4.5 | -4.6 |
| Thompson, prior SD 10 | -0.1 | -0.1 | -0.1 | -0.1 | -0.1 | -0.1 |
| Thompson, prior SD 25 | -7 | -7 | -7 | -7 | -7 | -7 |
| Thompson, prior SD 50 | -18 | -17 | -17 | -16 | -15 | -17 |
| cautious learner, prior SD 50 | 0 | 0 | 0 | 0 | 0 | 0 |
| Matthew's rule, 3 games | -23 | -22 | -21 | -16 | -10 | **-21** |
| Matthew's rule, 9 games | -24 | -23 | -24 | -23 | -23 | -23 |
| *next 3,000 games (~6 weeks)* | | | | | | |
| steered explorer, 5% | -2.3 | -2.3 | -2.3 | -2.4 | -2.3 | -2.3 |
| Thompson, prior SD 25 | -7 | -7 | -7 | -5 | -3 | -7 |
| Thompson, prior SD 50 | -18 | -17 | -16 | -11 | -3 | -16 |
| cautious learner, prior SD 50 | 0 | 0 | 0 | +1 | +5 | +0.2 |
| Matthew's rule, 3 games | -24 | -23 | -18 | -3 | +20 | **-18** |
| Matthew's rule, 9 games | -27 | -26 | -23 | -11 | +4 | -23 |

| a close second (0, -10, -41), next 3,000 games | 0 | 25 | 50 | 100 | 150 | weighted |
|---|---|---|---|---|---|---|
| the ceiling | 0 | +10 | +29 | +69 | +112 | +23 |
| steered explorer, 5% | -1.3 | -1.3 | -1.3 | -1.3 | -1.2 | -1.3 |
| Thompson, prior SD 25 | -6 | -6 | -5 | -3 | +3 | -5 |
| cautious learner, prior SD 50 | -2 | -1 | +3 | +14 | +31 | +3 |
| Matthew's rule, 3 games | -13 | -12 | -7 | +9 | +32 | -7 |

Over 20,000 games (~9 months) at σ 100, Matthew's rule finally gains +12 to +18, Thompson +3 to +9; the steered
explorer still loses 2.2 (results/simulate.txt).

**Why cheap exploring can't pay here.** Exploring buys information about one bot, and one game is worth ±320 Elo
of it. A setup tried in 5% of a bot's games gets one game in twenty; reaching "clearly ahead" (95%) on a 50-Elo
edge takes ~220 games of it. So the steered explorer pays its 2.3 Elo and never acts on what it learns. Thompson
with a narrow prior (SD 10) is cheap only because it never explores. With wider priors it explores freely and
never stops, because each bot's belief never narrows on ~10 games. What the exploring could teach is "which setup
is best overall", and a match teaches that far faster.

**Why Matthew's rule costs most.** Most bots are met a handful of times, so most games against them fall in the
exploring part, where two games in three go to a setup 41-52 Elo weaker. After that, "best score so far" rests on
one to three games a setup, which is mostly luck, so the weaker setups keep getting picked. Counting a loss as
"bias against" doesn't fix that: one loss is one game's worth of evidence (±320 Elo). The rule pays only if bots
really differ by 100+ Elo between nets and the bot meets each one dozens of times.

**When it would change.** If two setups were within ~10 Elo overall, exploring would be cheap and the cautious
learner would make +3 (weighted) to +14 (σ 100). Today's nets aren't that close. The deep nets' matches (the
nnue-deep-layers session) may produce one, which is the time to look again.

## 4. Vary the opening

**Who repeats.** 568 pairs of games against the same bot with the same colours. Only 15 (3%) were identical for 8
plies, and 3 for 16. From a position met before, we played a different move 60% of the time (the book is
weighted-random), the bots 30%. So in 390 pairs we left the line first, and in 176 they did. Of the 50 bots seen
in 4+ such positions, 14 never varied and 29 varied a quarter of the time or more.

**The learner rarely gets its chance.** bot/learn.py works out a move for each position where the bot left its
books, and lichess-bot plays it from ours.bin if the position comes again
([learner_hits.py](scripts/opponents/learner_hits.py)):

| | count |
|---|---|
| book moves in 617 games: main / wide / ours.bin | 4,302 / 291 / **3** (all in one game, 10-16) |
| our positions in the first 12 moves, all games | 7,254, of which 3 learnt before the game |
| games against a bot met before, same colours | 319 |
| ... any position after move 1 the same as last time | 59 |
| ... last game's first out-of-book position reached again | 16, of which 15 are in today's main book |

The book isn't broken (its keys match, and the 3 hits are in the log). The positions just don't come back: we
wander off our own last line, and the main book has grown into most of the few that do. So "our book learner
already exploits the bots' repetition" doesn't hold today: it has had almost nothing to exploit.

**Replaying our line.** If, against a bot met before with the same colours, we replayed our moves from the last
game, the bot's own repeat rate (estimated per bot from the pairs) says we'd reach last game's first out-of-book
position in ~23% of those games (72 of 317), against 5% now. There, the learner's move or the engine's would face
a bot that has already shown its hand. And against the 14 bots that never varied, a won game might replay further.
It costs nothing in strength, since the replayed moves come from the same book as the weighted-random ones. What
it would win can't be measured from these games; it would take a live trial.

**Before that, the learner itself.** It still thinks with eval-sf1.txt (set on 2026-10-14, when sf1 was our best),
not the bot's net, which is hundreds of Elo stronger in self-play. A replay makes its moves come up more often, so
it should think with the net first. A separate check (TODO).

## 5. How it would fit lichess-bot

- **A net per game** (O2 (b)-(d)): lichess-bot makes a new engine for every game, `create_engine(config, game)` in
  lib/engine_wrapper.py (line 35), called from `play_game` (lib/lichess_bot.py:804) before the game's `+++` line;
  `game.opponent.name` is there. A few lines would hand the uci_options (EvalFile) to a chooser, a module copied
  into lib/ the way chat_commands.py is. That covers our challenges and theirs (154 of 617 games were theirs,
  which the matchmaking point alone would miss). bot/lichess-bot-opponent.patch would touch lines 35-75, clear of
  lichess-bot-ponder-note.patch (262-290, the only other patch in that file) and its context.
- **A book per bot** (O3 (a)): `get_book_move(board, game, polyglot_cfg)` (engine_wrapper.py 756-799) tries the
  books in order on every move and has the game. The replay would check first whether the last game against this
  bot (same colours, not lost) played a book move in this position, and play it. It reads bot/games/*.pgn, or the
  record. Also clear of the ponder-note patch.
- **Timing**: per game, "Starting engine" to `+++` takes ~60 ms, and the net loads ~25 ms after the start (the DEBUG
  log, 2026-10-19). The tables load lazily during the game. A per-game net costs no time.
- **The startup check** ("Checking engine configuration") calls `create_engine(CONFIG)` with no game, so it only
  tests the default net. A chooser must check every candidate file at start, and make-config.py should drop
  missing ones; otherwise a broken alternative would fail at a game's start.
- **Memory**: one engine at a time (concurrency 1). Nets are 0.4-1.6 MB and only the chosen one loads; the tables
  (796 MB) and Hash (256 MB) are the same whichever net plays. Several nets cost nothing. A different engine build
  per setup would need its own copy of the build folder (disk only).
- Each change: bot/lichess-bot-<name>.patch applied by start-bot.cmd, bot/test-<name>.py on a copy through
  LICHESS_BOT, settings in make-config.py, every existing patch still reading as in (`git apply --reverse
  --check`).

## Decision O (P8): what to do

**O1. The log (step 1)**
- **(a) Switch it on** (my pick): one line in bot/start-bot.cmd runs collect.py into bot\games\record.csv at each
  start. No lichess-bot patch, no change to play. ~15 min. After ~3,000 more games (~6 weeks), rerun analyze.py.
- (b) Keep it a script, run by hand when wanted.

**O2. The net per bot (step 2)**
- **(a) None for now** (my pick): h1024-3m (or whatever wins the matches) plays every bot. Look again when two
  setups come within ~10 Elo, or if the 3,000-game rerun shows σ ≥ 75 with p < 0.05.
- (b) The steered explorer at 5%: -2.3 Elo for as long as it runs, and it never switches at today's repeat rates.
  The patch, a chooser, a test, settings: ~half a day.
- (c) Thompson sampling, prior from each setup's strength: -7 to -16 Elo; pays only after ~20,000 games if bots
  differ by 100+ Elo between nets. Same build.
- (d) Matthew's rule as stated: -18 to -23 Elo over the next 3,000 games (24-27 if there's nothing to find). Same
  build.

**O3. The opening per bot (step 3)**
- (a) Replay trial: against a bot met before (same colours), replay our book moves from the last game unless it
  was lost; otherwise the weighted-random pick as now. Free in strength; logs which games replayed. A patch to
  get_book_move, a test, ~half a day; then compare those games' results over a few hundred.
- **(b) First, the learner onto the bot's net** (my pick, then (a)): check how often h1024-3m disagrees with
  ours.jsonl's moves and re-learn where it does. The learner's moves are what a replay would bring up.
- (c) Leave the openings as they are.
