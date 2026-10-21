# Draw offers: should the bot offer a draw in a level game?

A scoping pass, 2026-10-22: **P7**, after the ponder decisions P1-P6 (a side session, branch
`claude/draw-offer-scope-6f8c39`, on `compression`'s 882d45c; any code for it goes on `claude/p7-draw-offers`). Nothing in `src` or in bot/lichess-bot changed. Matthew's idea: *"if all moves are 0 gain on the
main predicted path for a long time maybe we should offer draw if we have a similar strength to our
opponent?"* In other words: when the bot's evaluation has sat at about 0.00 for many moves in a row, and the
opponent is about as strong, offer a draw.

**Short answer: the idea is sound, but there is almost nothing left to win.**

- **The bot already offers draws, and accepts them.** lichess-bot does it, with its default rule: the bot's
  evaluation exactly 0.00 for 10 moves in a row, with 10 pieces or fewer. It made offers in 34 games. The
  other bot accepted in 4 of them.
- **With today's evaluation, a long level stretch already ends in a draw.** Since the bot moved to the
  networks (240 games), 30 games had 10 of the bot's moves in a row within ±0.15. They ended in 29 draws, 1
  win and no losses. So an accepted offer would have changed the rating by about 0 (the one win would have
  been a draw, -6 points).
- **What an offer could save is time, and not much of it.** If every offer had been accepted at once, the
  bot would have saved about 66 minutes of 43 hours of play (2.6%). The other bots accept in about 1 game in 8
  (4 of 34), which leaves about 8 minutes per 240 games.
- **Matthew chose (a), leave it as it is** (my recommendation too). Look again if humans start playing the bot, or if the bot
  starts losing level games again. In its first days, on the old evaluation, it did (below).

## How draws work now

- **The settings in force** (bot/lichess-bot/config.yml, read by a script that skips the token): `offer_draw_enabled:
  true`, `offer_draw_score: 0` (centipawns), `offer_draw_moves: 10`, `offer_draw_pieces: 10`. These are
  config.yml.default's values; bot/make-config.py starts from that file and changes only `resign_enabled:
  false`. `offer_draw_for_egtb_zero: true` does nothing here: it only applies to answers from the online
  tables, and those are off (the bot uses its own).
- **The rule** (`offer_draw_or_resign` in lib/engine_wrapper.py) is checked after every search. It offers when
  the last 10 scores the engine reported were all between -0 and +0 cp (so exactly 0.00), and the board has
  10 pieces or fewer before our move. Book moves add no score. A search that reported no score adds a mate
  score, which breaks the run. From the start of pondering (2026-10-16) until P1 (2026-10-21), about a
  quarter of the bot's moves after the book were such searches (ponder hits that finished no depth).
- **An offer** is a flag on the move: `offeringDraw=true` on the move request (lib/lichess.py `make_move`).
  lichess-bot sets the flag on every move while the rule holds. Lichess evidently passes on at most one offer
  per player every 10 moves: 52 of the 55 gaps between the bot's offers in a game were exactly 11 moves.
- **Accepting**: there is no separate rule. If the opponent has an offer standing and our move carries the
  flag, the draw is agreed. If our move carries no flag, the offer is declined. So the bot accepts exactly
  when it would have offered. (lichess-bot also passes `draw_offered` to python-chess, but UCI engines ignore it.)
- **The rule knows nothing about the opponent**: not their rating, and not whether they are a bot or a human.
  Resigning is off ("play every game out").

## What the games say

The data: lichess-bot's 617 game records (bot/games, 2026-10-12 to 10-21 UTC), 596 of them finished, with the
bot's evaluation on each of its moves. **All 617 opponents were bots**, so a "bot or human" split has
nothing on the human side. The bot's logs (bot/logs) cover all 596 finished games, including every offer,
decline and acceptance. The games split into two eras:

| era | games | W / D / L | median blitz rating |
|---|---|---|---|
| early: the hand-made and Texel evaluations, to 2026-10-16 17:50 UTC | 356 | 190 / 39 / 127 | 2067 |
| networks: nnue1 onwards | 240 | 130 / 56 / 54 | 2443 |

**How often a long level stretch happens**, and how those games ended. A stretch here means N of the bot's own
moves in a row, each with an evaluation within ±0.15. Moves with no evaluation are skipped. The columns split
the games by the pieces on the board when the stretch reached N (5 or fewer = in the tables):

| era | N | games with a stretch | ≤5 pieces | 6-10 | 11-20 | 21-32 | then W / D / L |
|---|---|---|---|---|---|---|---|
| early | 10 | 42 (12%) | 5 | 9 | 8 | 20 | 13 / 16 / 13 |
| early | 20 | 12 (3%) | 4 | 5 | 2 | 1 | 1 / 8 / 3 |
| early | 30 | 9 (3%) | 4 | 3 | 2 | 0 | 0 / 7 / 2 |
| networks | 10 | 30 (12%) | 12 | 12 | 4 | 2 | **1 / 29 / 0** |
| networks | 20 | 13 (5%) | 7 | 3 | 3 | 0 | **0 / 13 / 0** |
| networks | 30 | 10 (4%) | 7 | 0 | 3 | 0 | **0 / 10 / 0** |

On the old evaluation, "level" often wasn't level: 10 level moves in a full middlegame went on to 13 wins and
13 losses. On the networks, a level stretch is a draw. Of the 29 drawn games, 14 ended by threefold repetition,
6 by insufficient material, 6 by agreement, 2 by the 50-move rule and 1 unknown.

**By rating difference** (opponent minus us), after a 10-move stretch. The points are blitz rating points from
Lichess's Glicko-2 expected score, at K = 11.5. That is the median change per point of (score - expected) in
the bot's established blitz games of 13-19 October.

| opponent | networks: games, W / D / L | if drawn at the stretch | early: games, W / D / L | if drawn at the stretch |
|---|---|---|---|---|
| 150+ weaker | 6: 0 / 6 / 0 | ±0 | 13: 6 / 4 / 3 | -17 |
| 50-150 weaker | 2: 1 / 1 / 0 | -6 | 8: 5 / 2 / 1 | -23 |
| within 50 | 4: 0 / 4 / 0 | ±0 | 8: 0 / 4 / 4 | **+23** |
| 50-150 stronger | 5: 0 / 5 / 0 | ±0 | 6: 1 / 4 / 1 | ±0 |
| 150+ stronger | 13: 0 / 13 / 0 | ±0 | 7: 1 / 2 / 4 | +17 |

So Matthew's version, "similar strength or stronger", would have paid on the old evaluation (+40 points over
356 games if every offer had been accepted), and pays nothing today. What one result is worth, at K = 11.5:

| opponent minus us | -300 | -200 | -100 | 0 | +100 | +200 | +300 |
|---|---|---|---|---|---|---|---|
| a draw | -4.0 | -2.9 | -1.6 | 0 | +1.6 | +2.9 | +4.0 |
| a win | +1.8 | +2.8 | +4.2 | +5.8 | +7.3 | +8.7 | +9.7 |
| a loss | -9.7 | -8.7 | -7.3 | -5.8 | -4.2 | -2.8 | -1.8 |

**How much play is left after a stretch** (networks era, N = 10). "Minutes" is both clocks' use, increments
included, so it is close to the time the game still took.

| pieces at the stretch | games | median plies left | longest | median minutes | total minutes |
|---|---|---|---|---|---|
| ≤5 (tables) | 12 | 14 | 97 | 0.1 | 5 |
| 6-10 | 12 | 26 | 226 | 1.0 | 19 |
| 11-20 | 4 | 74 | 388 | 3.0 | 25 |
| 21-32 | 2 | 158 | 227 | 8.6 | 17 |
| all | 30 | | | | 66 (of 43 hours) |

**Offers both ways**, from the logs (all eras):

| | offers | accepted | games |
|---|---|---|---|
| the bot's offers | 89 | 4 (4.5%) | 34 games with an offer, 4 ended by agreement (12%). Accepted by DeMen100ns-bot, BattleBornThe3rd, nickdrohan and AggressiveStockfish |
| the opponents' offers | 48 | 3 by the bot | 21 games. Ravager_BOT made 11, DeepBecky 9, BattleBornThe3rd 8 |

- **When they offered, the bot usually thought it was better.** Its last evaluation was within ±0.15 for 19 of
  the 48 offers, above +0.15 for 28 (7 of them +1.50 or more), and below for 1. Bots offer when *their*
  evaluation says level.
- **The bot declined in 18 games: 3 won, 14 drawn, 1 lost.** Suppose it had accepted their first offer in
  each of the 19 games (of 21) that the log places move by move. That would have cost about 12 points (three
  wins lost, -17; one loss avoided, +6) and saved about 49 minutes. Declining paid.

## The trade

- **What an offer gains: time, if the other side says yes.** Today a level stretch is followed by a minute
  or so of play (a median of 0.1 to 8.6 minutes, depending on the pieces), and 66 minutes in all over 240
  games. That is 2.6% of the bot's playing time, and so of its wear on the laptop. But the other bot
  decides, and bots accept in about 1 game in 8. That leaves about 8 minutes per 240 games, under 2 minutes
  a day at the pace of 16-21 October.
- **What it gives up: the chance the opponent goes wrong later.** For humans that chance is large: in the
  tables, 1 game in 4-5 ends otherwise than perfect play would (26.6% in September 2026), and strong players
  lose as many as weaker ones, to the clock (WORKLOG 2026-10-12). The bots the bot plays are steadier. After
  a 10-move level stretch, 1 of 30 games turned into a win, none into a loss, and nobody lost on time.
- **The rating: about zero today.** With today's evaluation, every rule below changes the rating by 0 to -6
  points over 240 games if every offer is accepted. Being rating-aware matters only against weaker
  opponents, where a draw costs 1.6-4 points. In the networks era, 7 of the 8 level games against weaker
  bots were drawn anyway, and the eighth was a win.
- **Where it would matter.** On the old evaluation, the bot lost 4 of 8 level games against bots within 50
  points, and 5 of 13 against stronger ones. A draw then would have been worth +40 points over 356 games. If
  a future change makes the bot weaker than its evaluation in long games, the idea comes back.

## The options (decision N)

The rules simulated on the networks era's 240 games. "Fires in" counts the games where the rule would offer
(and would accept their offer). Minutes are those saved if every offer were accepted at once, and at the
1-in-8 rate the bots actually accepted.

| option | fires in | W / D / L as played | points if accepted | minutes saved: all / 1 in 8 | work |
|---|---|---|---|---|---|
| **(a) leave it as it is**: 0.00 for 10 moves, ≤10 pieces | 20 | 0 / 20 / 0 | ±0 | 16 / 2 | none |
| **(b) draws off**: `offer_draw_enabled = False` | 0 | | ±0 | 0 / 0 (and the 6 agreed draws played out) | one line in make-config.py |
| **(c) a tuned setting**: ±0.15 for 20 moves, any pieces | 13 | 0 / 13 / 0 | ±0 | 35 / 4 | three lines in make-config.py |
| **(d) rating-aware**: ±0.15 for 10 moves, any pieces, only if the opponent is rated at least ours - 50 | 22 | 0 / 22 / 0 | ±0 | 45 / 5 | a lichess-bot patch and its test, an hour or two |

- **(a) Leave it.** The default rule offers where a game is a dead draw (mostly the tables) and accepts
  there. It costs nothing and does no harm.
- **(b) Draws off.** This matches "play every game out", but the bot then can't take a stronger bot's draw in
  a dead position. No gain.
- **(c) Tune it in make-config.py**: `offer_draw_score: 15`, `offer_draw_moves: 20`, `offer_draw_pieces: 32`.
  The bot then also offers in long level middlegames. It knows no ratings, so it would also accept a weaker
  bot's offer by the same rule. On all 596 games it fires in 25 (+11 points if all were accepted: the early
  era's losses).
- **(d) Rating-aware: a new patch, bot/lichess-bot-draw.patch**, applied by start-bot.cmd, with
  bot/test-draw.py run on a copy of lichess-bot (the `LICHESS_BOT` variable). `play_move` (which has the
  game) records the rating gap, and `offer_draw_or_resign` checks it: against a bot more than 50 points
  weaker it neither offers nor accepts. Both places are in lib/engine_wrapper.py, clear of
  lichess-bot-ponder-note.patch's lines (262-290 and their context), so every existing patch still reads as
  in. On all 596 games it fires in 43, and would have been worth +40 points if all were accepted, nearly all
  of it in the early era. A variant could also never offer a human, whose mistakes are worth waiting for.

**Chosen: (a), leave it as it is** (Matthew, 2026-10-22). It was my pick too. The bots decide, and they mostly say no. Today's bot draws its level games anyway. So the
best rule saves about 5 minutes per 240 games. Worth a TODO line to look again if humans play the bot, or if
level games start being lost (the early era's pattern). Then (d) is the one to build.

## Method and caveats

- **Evaluations**: the `%eval` lichess-bot writes into each record (at the end of the PV it attaches to our
  move), in centipawns. A move with none (book moves; ponder hits with no score before P1) is skipped by the
  ±0.15 measure. The "today" rule rows count it as breaking the run, as lichess-bot does. Simulated,
  lichess-bot's rule fires in 34 games; the logs show offers in 34.
- **Small numbers.** 30 games in the networks era had a stretch. None lost out of 30 still allows a true loss
  rate of up to about 10% (the rule of three). The conclusion stands on the time side anyway, because the
  other bots' 1-in-8 acceptance caps what any rule can save.
- **Rating points** use Lichess's Glicko-2 expectation with an opponent RD of about 60 (g = 0.98), and one K
  for every row (11.5). The bot's first day (provisional ratings, K of 50-200) and its few bullet, rapid and
  classical games are counted at the same K, so every row is in today's blitz points.
- **Games after these**: the repetition rule (b5061ed, on by default from ebfd2df) and P4 came after the games
  here. They change how the bot treats positions the game has had before, so they may change how level games
  end (14 of the 29 by repetition). Not how often the other bots accept.
- **Reproduce**: scripts/draw-scope/ (run with lichess-bot's venv Python, `-I`):
  `collect.py <bot folder> data.json` reads the records and logs (never the token).
  `analyze.py data.json [band]` gives the first tables. `analyze2.py data.json` gives the eras, the offers
  both ways and the options. `reasons.py data.json <bot folder>` gives how the draws ended. `detail.py
  data.json N` lists one game per row. The outputs of 2026-10-22 are in
  scripts/draw-scope/results-2026-10-22.txt.
