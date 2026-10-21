# Opponents: choosing the setup per bot (P8, decision O)

Matthew's idea (2026-10-22, P8), in his words: "player data logging and comparison, we will pick our best 3
engines and select the best engine with the least usage for 3 to 10 games and switch before the beginning of
the match at the matchmaking point, some engines may be confused by some of our reasoning and that might lead
to wins, if we find a player that has a particular weakness to an engine we keep using it, if we loose badly
with another choice we can count that as bias against".

So: before each game, the bot picks which of its three best setups (nets) plays, from what it has seen against
that opponent. That is a "multi-armed bandit" per opponent. This file scopes it with the bot's own games
(2026-10-22, a side session; scripts in [scripts/opponents/](scripts/opponents/), outputs in
scripts/opponents/results/). Nothing in src or bot/lichess-bot changed. The options are at the end.

## The short answer

- **The bot rarely meets the same bot twice.** 617 games against 196 bots: 68 bots met once, 36 twice, and only
  three 10 times or more (the most, 16). Matchmaking picks at random from ~100 suitable bots online.
- **No sign of opponent-specific weaknesses beyond chance.** Since the networks (240 games, 115 bots), no bot
  scores against us differently from what its rating says by more than luck explains. That holds whether we
  look for an effect in general (best estimate 0 Elo, at most ~100) or bot by bot: one bot stands out, where
  luck alone gives 1.5.
- **No sign that the net matters to a bot's result** beyond the net's overall strength: best estimate 0 Elo,
  at most ~100 (95%).
- **One game says little.** One blitz game measures "this bot against this setup" to ±320 Elo (one standard
  deviation). Telling two setups apart for one bot, at 95%, takes ~80 games each for a 100-Elo difference.
- **So exploring costs more than it can find.** Simulated over the bot's next 3,000 games, weighted by what
  the data allows, Matthew's rule as stated costs about **18-23 Elo** of playing strength. Thompson sampling
  costs 7-16. A cautious learner costs nothing but gains ~0.2. The prize, if we knew each bot's best net
  perfectly, would be ~16 Elo.
- **Openings vary more than the bots do.** From a position they've met before, the bots repeat their move 70%
  of the time; our weighted-random book repeats 40%. If anything opponent-specific is worth trying, it is the
  opening, not the net. The data can't price that yet.

My pick: **(a), log only**. The record is backfilled and needs only one line in start-bot.cmd. Revisit after
~3,000 more games.

## 1. The game record

**Recorded today, per game.** bot/games/*.pgn has the opponent and both titles, both ratings and rating changes,
the result and how it ended, the time control and speed, rated or casual, our colour, the opening (ECO and
name), the clocks, and our evaluation and line after each of our moves. bot/logs/bot-*.log (INFO, kept for
good) has who challenged whom, which book gave each book move, every engine move's evaluation, depth and
speed, and the chat. At every start it also has the engine's `info string evaluation: network ...\h1024-3m.nnue
(..., sha256 510a8d0e6c43)` line during "Checking engine configuration". lichess-bot's DEBUG log has every UCI
line too, but only for 7 days.

**Missing: which setup played.** The net only changes when the bot restarts (make-config.py picks the first
file present in bot\eval), so each game takes the setup of the last start before it.
[scripts/opponents/collect.py](scripts/opponents/collect.py) does that, along with the rest:

| column | from | what |
|---|---|---|
| id, utc, rated, speed, tc | PGN | the game |
| colour, opponent, opp_title, our_elo, opp_elo, our_diff | PGN | who, and the ratings then |
| score, termination, moves | PGN | 1 / 0.5 / 0 for us, how it ended, its length |
| challenger | log | "us" (matchmaking) or "them" |
| net, net_sha | log | the evaluation the engine loaded at the start that played it |
| bot_start, log_start | log | that start's time, the game's start (local time, UTC+11) |
| beside | log | other games running beside it (two at once from 2026-10-18 until the 22nd) |
| book_main, book_ours, book_wide | log | our moves from each book |
| eco, opening, line | PGN | the opening, and the first 20 plies (to see who repeats a line) |

**Backfilled**: the main checkout's bot\games\record.csv (out of git like the PGNs, rebuilt in about a second),
617 games:

| setup | games | bots | when (UTC) | score | our rating, first to last |
|---|---|---|---|---|---|
| built-in numbers | 145 | 74 | 10-12 17:28 to 10-14 04:10 | 51% | 3000 (provisional) to 2009 |
| sf1 (tuned numbers) | 229 | 97 | 10-14 to 10-16 16:15 | 64% | 2009 to 2208 |
| nnue1 | 19 | 18 | 10-16 to 10-17 00:06 | 75% | |
| nnue1-h512 | 154 | 85 | 10-17 to 10-18 23:58 | 71% | 2260 to 2520 |
| h512-3m | 26 | 22 | 10-19 03:38 to 10:16 | 48% | 2628 to 2607 |
| h1024-3m | 44 | 36 | 10-19 16:33 to 10-21 15:29 | 54% | 2530 to 2553 |
| h1024-3m-l16-32 | 0 | | from 10-22 03:00 local | | |

(The one h1024-3m-l16-32 game, KDKKzauD, has no PGN: the bot was stopped at 03:26 while it was in progress.) The engine
build changed along the way too (threads, pondering, P4, the repetition rule), and so did the opponents as the
rating rose. So a "net" here means a period, not a clean experiment.

**Going forward**: under (a), start-bot.cmd runs collect.py at each start (stdlib only, any Python) and the
record stays one start behind at most. Under (b)-(d), the patch logs one INFO line a game ("setup for game X:
...") and collect.py reads that instead of the start's net.

## 2. Do opponent-specific weaknesses exist?

**The model.** Each game's result is predicted from our strength with that net (fitted), the opponent's Lichess
rating, our colour and the speed. Draws are handled the way BayesElo handles them (a fitted draw width; our
games: 16% draws). On top of that, each bot gets its own offset *b*: how much better (+) or worse (-) we do
against it than its rating says. The question is how big the offsets are across bots (their SD, τ). A second
offset *c* per bot per net asks whether a bot's offset changes with the net (its SD, σ). σ is the effect a
per-opponent net choice would use. Both are fitted with the offsets integrated out, and their 95% intervals
come from the likelihood profile ([analyze.py](scripts/opponents/analyze.py)).

| games | bots (3+ games) | τ: opponent offsets | 95% | vs none | σ: offsets that change with the net | 95% |
|---|---|---|---|---|---|---|
| all, 596 with a result | 194 (90) | 80 Elo | 0-120 | p 0.05 | 0 Elo | 0-100 |
| without time forfeits, 562 | 189 (86) | 70 | 0-120 | p 0.12 | 0 | 0-100 |
| networks only, 240 | 115 (37) | **0** | 0-100 | p 0.5 | **0** | 0-100 |

The weak hint in all games (τ 80, p 0.05) comes from the first four days. On the built-in numbers and sf1, our
strength was also moving inside one "net" (1 thread to 8, pondering on, the rating settling from a provisional
3000), and a model with one strength per net puts that on whichever bots we met then.

**Bot by bot, against chance.** Each bot's score against its expected score, as a z-score. Then the same count
from 400 simulated histories in which no bot is special:

| | bots with 3+ games | beyond 95% (\|z\| > 1.96) | by chance alone | largest \|z\| | by chance (median, 95%) |
|---|---|---|---|---|---|
| all games | 90 | 3 | 3.8 (95%: up to 7) | 3.00 | 2.68, 3.85 |
| networks only | 37 | 1 | 1.5 (up to 4) | 1.98 | 2.50, 3.83 |

The "most surprising" bots (Alieknek 1 of an expected 4.0 in 6 games, CalciumEngine 5.5 of 9.0 in 12,
DeMen100ns-bot 2.5 of 5.5 in 9) are as surprising as the most surprising of 90 bots would be by luck. That is
the winner's curse: rank enough bots and some look special. A rule that "keeps using" whatever did well against
a bot would mostly be chasing these.

**Matthew's sums, checked.** At even strength one game's score has variance 0.19 with our draw rate, so "about
0.2" holds, and his table holds (10 games each: 40 points, ~280 Elo; 30: 23 points, ~160; 100: 13 points,
~90). Our games are often lopsided (we score 62%), so their variance averages 0.13, but the Elo it buys is the
same: one game measures a bot's offset to ±320 Elo.

**Openings.** 568 pairs of games against the same bot with the same colours. Only 15 (3%) were identical for 8
plies, and 3 for 16. From a position met before, we played a different move 60% of the time (the book is
weighted-random), the bots 30%. So in 390 pairs we left the line first, in 176 they did. Of the 50 bots seen
in 4+ such positions, 14 never varied and 29 varied a quarter of the time or more. Over a quarter of the bots
would let us replay a game; we almost never do.

## 3. Selection rules, simulated

[simulate.py](scripts/opponents/simulate.py) builds a world like the bot's. Matchmaking picks bots at random
from a pool fitted to how often the bot has met each one (300 bots, uneven popularity: 596 games meet 209 bots,
72 once, the most 13 times; the bot's own figures are 194, 67 and 12). Each bot has a rating gap to us (mean
+30, SD 190), a common offset (τ 80), and an offset per setup (σ, the unknown: 0 to 150). The three setups
differ overall as measured: h1024-3m 0, h512-3m -41, nnue1-h512 -52 (MATCHES.md). A second set, "a close
second" (0, -10, -41), stands in for a deep net or an h768-3m that comes out near h1024-3m. Every rule plays the
same games. The tables give **Elo played**: the chosen setup's true edge minus h1024-3m's, averaged over games,
which is roughly what the rating feels.

| rule | σ 0 | 25 | 50 | 100 | 150 | weighted by the data |
|---|---|---|---|---|---|---|
| *next 600 games (~9 days), today's three* | | | | | | |
| always h1024-3m | 0 | 0 | 0 | 0 | 0 | 0 |
| the ceiling (knows each bot's best) | 0 | +3 | +18 | +57 | +98 | +16 |
| Matthew's rule, 3 games to explore | -23 | -22 | -21 | -16 | -10 | **-21** |
| Matthew's rule, 9 games | -24 | -23 | -24 | -23 | -23 | -23 |
| Thompson sampling (prior SD 25) | -7 | -7 | -7 | -7 | -7 | -7 |
| Thompson sampling (prior SD 50) | -18 | -17 | -17 | -16 | -15 | -17 |
| cautious learner (no deliberate exploring) | 0 | 0 | 0 | 0 | 0 | 0 |
| *next 3,000 games (~6 weeks), today's three* | | | | | | |
| Matthew's rule, 3 games | -24 | -23 | -18 | -3 | +20 | **-18** |
| Matthew's rule, 9 games | -27 | -26 | -23 | -11 | +4 | -23 |
| Thompson (25) | -7 | -7 | -7 | -5 | -3 | -7 |
| Thompson (50) | -18 | -17 | -16 | -11 | -3 | -16 |
| cautious learner (prior SD 50) | 0 | 0 | 0 | +1 | +5 | +0.2 |
| *next 3,000 games, a close second* | | | | | | |
| Matthew's rule, 3 games | -13 | -12 | -7 | +9 | +32 | -7 |
| Thompson (50) | -10 | -10 | -8 | -3 | +7 | -8 |
| cautious learner (prior SD 50) | -2 | -1 | +3 | +14 | +31 | +3 |

"Weighted by the data" averages over σ by how well each value fits the network-era games (σ 0: 1, 25: 0.94, 50:
0.75, 100: 0.28, 150: 0.05). Over 20,000 games (~9 months) at σ 100, Matthew's rule finally gains +12 to +18;
at σ 0 it still costs 22-24 (results/simulate.txt).

**Why Matthew's rule costs so much.** Most bots are met a handful of times, so most games against them fall in
the exploring part. There, two games in three go to a setup 41-52 Elo weaker. After exploring, "best score so
far" rests on one to three games a setup, which is mostly luck, so the weaker setups keep getting picked.
Counting a loss as "bias against" doesn't fix that: one loss is one game's worth of evidence (±320 Elo). The
rule pays only if bots really differ by 100+ Elo between nets and the bot meets each one dozens of times.

**Thompson sampling, UCB.** With a prior from each setup's overall strength, an unknown bot mostly gets
h1024-3m. But Thompson still tries the others whenever its uncertainty allows, and with ~10 games a bot the
uncertainty never shrinks: 7-18 Elo, for good. UCB explores more than Thompson does over short horizons, so it
would cost more. The cautious learner (pick the best expected result, never explore on purpose) is the only
rule that doesn't lose. It hardly ever leaves h1024-3m, because no bot's record is long enough to outweigh a
41-Elo prior gap.

**Where the arms are close, it changes.** If three setups were within ~10 Elo overall, exploring would be cheap
and the cautious learner would make +3 (data-weighted) to +14 (σ 100). Today's nets aren't that close. The deep
nets' matches (the nnue-deep-layers session) may produce one; that's the time to look again.

## 4. How it would fit lichess-bot

- **Where**: lichess-bot makes a new engine for every game, `create_engine(config, game)` in lib/engine_wrapper.py
  (line 35), called from `play_game` (lib/lichess_bot.py:804), before the game's `+++` line. `game.opponent.name`
  is there. A patch of a few lines would hand the uci_options (EvalFile) to a chooser, a module copied into
  lib/ the way chat_commands.py is. That covers our challenges and theirs alike: 154 of the 617 games came
  from other bots' challenges, which the matchmaking point alone would miss. The patch would be
  bot/lichess-bot-opponent.patch, applied by start-bot.cmd. It touches engine_wrapper.py lines 35-75, clear of
  lichess-bot-ponder-note.patch (262-290, the only other patch in that file) and its context.
- **Timing**: per game, "Starting engine" to `+++` takes ~60 ms, and the net loads ~25 ms after the start (the
  DEBUG log, 2026-10-19). The tables load lazily during the game. A per-game net costs no time.
- **The startup check** ("Checking engine configuration") calls `create_engine(CONFIG)` with no game, so it only
  tests the default net. A missing or broken alternative would fail at a game's start. The chooser must check
  every candidate file at start, and make-config.py should drop missing ones.
- **Memory**: one engine at a time (concurrency 1). Nets are 0.4-1.6 MB and only the chosen one loads; the
  tables (796 MB) and Hash (256 MB) are the same whichever net plays. Several nets cost nothing. A different
  engine build per setup would need its own copy of the build folder (disk only).
- **The live record**: the chooser would read results from bot/games/*.pgn (written at game end) and its own
  choices from a small bot/games/setups.jsonl. Nothing new is held in lichess-bot's memory.

## Decision O (P8): what to do

- **(a) Log only** (my pick): start-bot.cmd runs collect.py at each start, so bot\games\record.csv stays
  current. No lichess-bot patch, no change to play. After ~3,000 more games (~6 weeks), rerun analyze.py. If σ's
  upper bound has fallen well under 50 (likely), the idea is closed. If a real effect shows (σ ≥ 75 with p <
  0.05), come back to (b). ~15 min.
- **(b) Log and a cautious learner**: per bot, the setup with the best expected result after what we've seen,
  with each setup's overall strength as the prior and no deliberate exploring. Costs ~0 if there's nothing to
  find, gains ~0.2 Elo (data-weighted; +5 if σ is 150) with today's nets. The patch, the chooser module,
  bot/test-opponent.py on a copy through LICHESS_BOT, and settings in make-config.py: ~half a day. Worth it
  only once two setups are within ~10 Elo.
- **(c) Log and Thompson sampling**: the same, but it explores on purpose. Costs 7-18 Elo from now on unless
  bots differ by 100+ Elo between nets; pays only after ~20,000 games. Same build as (b).
- **(d) Matthew's rule as stated**: the least-used setup for each bot's first 3-10 games, then the best scorer,
  losses counting against. Costs ~18-23 Elo over the next 3,000 games (data-weighted; 24-27 at σ 0). It would
  need effects at the very top of what the data allows, and thousands of games, to pay. Same build.
- **(e) Vary the opening per bot instead** (scope only, as a next step): over a quarter of the bots repeat
  their moves. A memory per bot (replay our line from a won game; leave a lost one earlier) costs no strength, unlike a
  weaker net. The data can't price it yet: 15 pairs of games ever reached the same position 8 plies in. It
  needs a patch to lichess-bot's book choice and a measurement first: how often the deterministic bots let a
  replay reach the same middlegame. (a)'s record is its first step too.

Turned up on the way, not part of P8: the book learner (bot/learn.py) still thinks with eval-sf1.txt, not the
bot's net. Its moves are played instantly from ours.bin, ahead of the bot's own thinking. That was set on
2026-10-14, when sf1 was the best evaluation. The nets are now hundreds of Elo stronger in self-play, so the
learner's choices may be the weakest moves the bot plays out of book. Worth a check (TODO).
