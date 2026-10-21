# Match log

Every measured match, newest last. Results are from our engine's point of
view. Stockfish's `UCI_Elo` is calibrated at a longer time control than the
fast games played here, so ratings "on Stockfish's scale" are approximate; the
error bars are the 95% interval from the match itself.

Played with `ChessBruteforcer.Match` at 10+0.1 (10 s per game + 0.1 s per move),
16 openings in colour-reversed pairs, 2 games at a time on a 4-core container,
with our endgame tables (all 4-piece) for the engine and for adjudication.

| date | engine | opponent | games | +W -L =D | score | Elo diff [95%] | notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 2026-09-27 | v0.1 | Stockfish 16, UCI_Elo 1500 | 20 | +18 -2 =0 | 90.0% | +382 [+208, +∞] | 1 loss on time (see below) |
| 2026-09-27 | v0.1 | Stockfish 16, UCI_Elo 1500 (2+0.02) | 16 | +14 -2 =0 | 87.5% | +338 [+158, +∞] | fast control, no time losses |
| 2026-09-27 | v0.2 | Stockfish 16, UCI_Elo 2000 | 20 | +11 -9 =0 | 55.0% | +35 [-121, +208] | 1 loss on time |
| 2026-09-27 | v0.3 | Stockfish 16, UCI_Elo 2000 | 20 | +14 -4 =2 | 75.0% | +191 [+51, +441] | no time losses; 2 tablebase adjudications |
| 2026-09-27 | v0.3 | Stockfish 16, UCI_Elo 2000 | 20 | +17 -3 =0 | 85.0% | +301 [+142, +∞] | **Matthew's laptop** (WSL, 1 game at a time); no time losses; 1 tablebase adjudication |

**Estimate so far:** v0.2 and v0.3 play the same chess (v0.3 only fixes time
handling). Together they score +25 -13 =2 against Stockfish-2000 (65%), about
+100 Elo, so **roughly 2100 on Stockfish's scale**. That needs hundreds of
games, not tens, to pin down.

The laptop result (85%) isn't pooled with the container's. On a faster CPU,
running one game at a time, both engines see more per move at the same clock,
and there is no reason the gain should be equal for both. The two runs overlap
within their error bars, and the laptop result's main use is showing the Windows /
WSL setup plays clean games: 20 finished, no forfeits, no illegal moves.

## Threads against one thread (2026-10-15)

Our engine against itself, Matthew's laptop (i9-11900H, 8 cores), 10+0.1,
one game at a time at below-normal priority, 20 openings from
games/tune/openings-300.txt in colour-reversed pairs, the tuned evaluation
(sf1), Hash 64, no endgame tables. Branch claude/nostalgic-greider-09538a;
the details, and the time-to-depth bench, are in WORKLOG (2026-10-15, 21:25).

| date | engine | opponent | games | +W -L =D | score | Elo diff [95%] | notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 2026-10-15 | 8 threads, LazySMP | 1 thread | 40 | +26 -4 =10 | 77.5% | +215 [+124, +344] | no time losses; games 1-8 shared the cores with a test run |
| 2026-10-15 | 8 threads, Ranges | 1 thread | 40 | +20 -2 =18 | 72.5% | +168 [+95, +259] | no time losses |

## Evaluations against each other (2026-10-14 on)

Our engine against itself with two evaluations, Matthew's laptop, 10+0.1, 5
games at a time, one thread and Hash 64 each, no tables in the engines (ours
adjudicate), the 300 openings of games/tune/openings-300.txt in
colour-reversed pairs. sf1 and sf2 are Texel-tuned numbers (piece-square
values and the bishop pair; WORKLOG 2026-10-14 and 15); nnue1 is a network,
768 -> 256 x2 -> 1, trained on September 2026's 224 M Lichess positions
scored by Stockfish (NNUE.md). `scripts\tune-match.cmd` played the first
two, `scripts\nnue-match.cmd` the rest.

| date | engine | opponent | games | +W -L =D | score | Elo diff [95%] | notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 2026-10-14 | sf1 | built-in numbers | 69 | +48 -11 =10 | 76.8% | +208 [+130, +311] | SPRT [0, 20] accepted H1; the bot (1 thread) playing alongside |
| 2026-10-15 | sf2 | sf1 | 2,000 | +668 -662 =670 | 50.1% | +1 [-11, +13] | SPRT [0, 10] undecided; ~3 h beside the bot |
| 2026-10-17 | nnue1 | sf1 | 20 | +19 -1 =0 | 95.0% | +512 [+308, +∞] | smoke test |
| 2026-10-17 | nnue1 | sf1 | 117 | +114 -0 =3 | 98.7% | +755 [+622, +∞] | SPRT [0, 10] accepted H1 at game 113 (see below) |
| 2026-10-17 | nnue1 | sf1 | 400 | +392 -3 =5 | 98.6% | **+742 [+645, +970]** | fixed length, for the Elo figure |
| 2026-10-17 | nnue1-h512 | nnue1 | 285 | +114 -53 =118 | 60.7% | **+76 [+45, +107]** | SPRT [0, 10] accepted H1 at game 284 (LLR 2.98); bot stopped (see below) |
| 2026-10-18 | h512-blend25 | nnue1-h512 | 1,252 | +311 -349 =592 | 48.5% | -11 [-25, +3] | SPRT [0, 10] **accepted H0** (LLR -3.06): the result blend doesn't help; bot stopped |
| 2026-10-18 | h512-mates | nnue1-h512 | 1,212 | +310 -349 =553 | 48.4% | -11 [-26, +3] | SPRT [0, 10] **accepted H0** (LLR -2.99): mates in training don't help; bot stopped |
| 2026-10-19 | h512-3m | nnue1-h512 | 2,000 | +583 -521 =896 | 51.5% | **+11 [-1, +22]** | SPRT [0, 10] undecided at the 2,000-game limit (LLR 1.73), LOS 96.9%: three months' data, a small gain; bot stopped |
| 2026-10-20 | h1024-3m | h512-3m | 584 | +211 -143 =230 | 55.8% | **+41 [+19, +63]** | SPRT [0, 10] **accepted H1** at game ~579 (LLR 2.95); games 321-584 beside the bot, 34 time losses there (see below) |
| 2026-10-20 | h768-3m | h512-3m | 525 | +200 -189 =136 | 51.0% | +7 [-18, +33] | **stopped, not a result**: all beside the bot, 135 games lost on time (78 by h768-3m); to be played again with the bot off |
| 2026-10-22 | h1024-3m-l32 | h1024-3m | 191 | +25 -71 =95 | 38.0% | **-85 [-121, -51]** | SPRT [0, 10] **accepted H0** at game 187 (LLR -3.09); a deep net at half the speed (see below); bot stopped |
| 2026-10-22 | h1024-3m-l16-32 | h1024-3m | 276 | +45 -91 =140 | 41.7% | **-58 [-88, -30]** | SPRT [0, 10] **accepted H0** at game 272 (LLR -3.09); bot stopped |

The nnue1 matches ran with the bot stopped, on the charger, at below-normal
priority: 4.5-5.4 s a game (~790 games an hour). No time losses, crashes or
illegal moves in 537 games. Of the 400: nnue1 as white +198 -0 =2, as black
+194 -3 =3; 392 wins by mate (355 on the board, 37 by the tables). All three
runs pooled: +525 -4 =8, 98.5%, +728 [+646, +886].

How each searched (the last info line before each move, from the match log):

| run | nnue1 depth | nnue1 knps | sf1 depth | sf1 knps |
| --- | --- | --- | --- | --- |
| smoke, 20 games | 9.6 | 2,167 | 9.5 | 1,946 |
| SPRT, 117 games | 9.5 | 2,179 | 9.2 | 1,936 |
| fixed, 400 games | 9.4 | 2,137 | 9.1 | 1,887 |

So the net is no slower in play: +13% nodes a second, a third of a ply
deeper. (`bench 9` had it 3.8% slower a node, on a fixed set of positions.)

**Why 117 games and not ~22.** The match tool's SPRT (fishtest's simple
normal form) divides by the variance of the games' scores, and with every
game a win that variance is zero, so the LLR is reported as 0. nnue1 won its
first 112 games and the LLR sat at 0.00; the first draw (game 113) took it
to 362 at once, past the 2.94 bound. The verdict stands; the test just
couldn't stop on a perfect score. TODO has the fix.

**nnue1-h512 against nnue1** (22:48-23:17, session 2b's launcher, right after
the 10-game match against Stockfish-2500, with the bot stopped, on the
charger, at below-normal priority): nnue1's recipe at 512 hidden, trained on
the desktop's GTX 1660 (NNUE.md, session 2b). 10+0.1, 5 at a time, the 300
book openings in colour-reversed pairs, our tables adjudicating: 285 games
in 29 minutes. Ends: 91 by the tables, 76 checkmates, 55 table draws, 51
repetitions, 12 by the 50-move rule; no time losses, crashes or illegal
moves. The lead held at +64 to +79 from game 150 on. In play, nnue1-h512
searched 2,131 knps against nnue1's 2,339 (**-8.9%**), depth 10.3 against
10.5: slower, and better anyway. On Matthew's word (2026-10-17, ~23:20) the
bot plays on it from its next start (`bot\eval\nnue1-h512.nnue`).

**h512-blend25 against nnue1-h512** (2026-10-18, 08:22-10:25, the bot stopped
by Matthew for it, the book learner's engine analysing on one core beside
it): nnue1-h512's recipe with a quarter of each target taken from the game's
result (`--result-blend 0.25`). 1,252 games in 2 h 3 min; the score sat at
47-49% from game 300 on, and the SPRT accepted H0 at game 1,252: **-11 Elo
[-25, +3]**. Ends: 336 checkmates, 324 by the tables, 309 repetitions, 250
table draws, 32 by the 50-move rule, 1 stalemate; no time losses, crashes
or illegal moves. Equal speed (2,051 against 2,058 knps, depth 10.1 each).
The bot stays on nnue1-h512.

**h512-mates against nnue1-h512** (2026-10-18, 14:03-16:03, the bot still
stopped): nnue1-h512's recipe with the 13.6 M mate-scored positions kept in
training (`--mates`, target 0 or 1). 1,212 games in 2 h; SPRT accepted H0 at
game 1,212: **-11 Elo [-26, +3]**, the score 47-50% throughout. Ends: 351 by
the tables, 308 checkmates, 277 repetitions, 221 table draws, 54 by the
50-move rule, 1 stalemate; no time losses, crashes or illegal moves. Equal
speed (2,018 against 2,008 knps, depth 10.1 each). The bot stays on
nnue1-h512.

**h512-3m against nnue1-h512** (2026-10-19, 11:09-14:27, the bot stopped
by Matthew for it): nnue1-h512's recipe at 512 hidden, trained on three
months' records instead of one (July, August and September, 677 M
positions, 10 passes; NNUE.md, session 5). 2,000 games in 3 h 17 min; the
SPRT didn't decide (LLR 1.73 of the 2.94 needed at the game limit): **+11
Elo [-1, +22]**, LOS 96.9%. Level at game 300 (-1), ahead from game 400 on
(+5 at its lowest, game 454; +21 at its highest, game 795). As white +317
-220, as black +266 -301. Ends: 604 by the tables, 500 checkmates, 465
repetitions, 344 table draws, 85 by the 50-move rule, 2 stalemates; no time
losses, crashes or illegal moves. Equal speed (2,091 against 2,078 knps,
depth 10.2 each). On Matthew's word (2026-10-19, ~14:35) the bot plays on
it from its next start (`bot\eval\h512-3m.nnue`).

**h1024-3m against h512-3m** (2026-10-20, 03:04-04:01): h512-3m's recipe at
1024 hidden (NNUE.md, session 5). The SPRT accepted H1 at game ~579 (LLR
2.95); 584 games in all: **+41 Elo [+19, +63]**, LOS 100.0%. As white +116
-68, as black +95 -75. Ends: 175 checkmates, 145 by the tables, 131
repetitions, 82 table draws, 34 on time, 16 by the 50-move rule, 1
stalemate; no crashes or illegal moves. **Not all of it was clean**: the bot
came back on h1024-3m at 03:28 (Matthew's call at game 188) and from about
game 320 the match played beside it, at below-normal priority. Speeds there
swung from ~1,700 down to 80-500 knps a side while the bot thought, and
**all 34 time losses came after game 420** (h512-3m 18, h1024-3m 16). The
two halves agree: games 1-320, +109 -74 =137, 55.5%, no time losses; games
321-584, +102 -69 =93, 56.2%. In the clean half h1024-3m searched 1,600-1,790
knps against h512-3m's ~2,000-2,110 (-16%), depth ~0.2 shallower. The bot
plays on it (decision I in NNUE.md).

**h768-3m against h512-3m, stopped** (2026-10-20, 04:01-04:54, all of it
beside the bot): 525 games, +200 -189 =136, +7 [-18, +33], LLR 0.13.
**135 games were lost on time** (games 139-437; h768-3m 78, h512-3m 57):
with five match engines below the bot's priority, each side dropped to
80-1,100 knps at times, and the slower net reached the clock more often.
Without the time losses: +143 -111 =136 in 390 games, 54.1%. So the clock
pulled h768-3m's score down by about 20 Elo: the match measured the
crowding, not the nets. Matthew stopped it to play it again with the bot
off; the games are kept as games\nnue\match-h768-3m-h512-3m-beside-bot.*.
The lesson for matches beside the bot: at 10+0.1, five games at a time is
too many while the bot plays; h1024-3m's last 264 games were near the
edge too.

**The deeper nets against h1024-3m** (2026-10-22, 05:09-05:58, back to back,
the bot stopped and nothing else running; NNUE.md session 6): small layers
between h1024-3m's feature layer and the output, .nnue version 2, from the
nnue-deep-layers branch's engine (the new repetition rule in, both sides
alike). Both judge the held-back positions better and both lost, on speed:

| net | held back v h1024-3m | games | Elo [95%] | knps, it v h1024-3m | depth, it v h1024-3m |
|---|---|---|---|---|---|
| h1024-3m-l32 (-> 32 -> 1) | -8.4% | 191 | **-85 [-121, -51]** | 1,344 v 2,982 (-55%) | 9.4 v 10.7 |
| h1024-3m-l16-32 (-> 16 -> 32 -> 1) | -6.8% | 276 | **-58 [-88, -30]** | 1,597 v 3,112 (-49%) | 9.8 v 10.8 |

No time losses. At p2.1's +87 Elo a doubling of time, half the speed costs
~85-100 Elo, so at equal speed these evaluations would be worth roughly +15
to +25 (with +-35 of noise): the extra judgement is real but small, and the
speed it costs is not. l32's bigger tree (+37% to depth 9 in `bench 9`)
cost it another 0.3 ply. Endings: l32 51 mates, 47 threefolds, 86 by the
tables, 7 fifty-move; l16-32 57, 69, 139, 10 and a stalemate.

## Pondering (2026-10-16 on)

Our engine against itself through python-chess, as lichess-bot drives it
(`scripts/ponder-match.py`): each engine pinned to a physical core of its
own, one thread, Hash 64, below-normal priority, the 300 book openings in
colour-reversed pairs, no endgame tables. Plain mode: one side ponders, the
other doesn't. A/B mode: both ponder, and they differ by a UCI option.
Results from the first-named side's point of view; "clock left" is at the
end of the game. Details in PONDER.md.

| date | engine | opponent | games | +W -L =D | score | Elo diff [95%] | notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 2026-10-16 | ponder on | ponder off | 119 | +48 -20 =51 | 61.8% | +83 [+37, +133] | 10+0.1, sf1 numbers; hit rate 71.5%; clock left 48% v 20% |
| 2026-10-21 | P2 (PonderFinish on) | P2 off, both pondering | 360 | +61 -103 =196 | 44.2% | **-41 [-65, -17]** | 10+0.1, h1024-3m, 3 at a time, the bot stopped; SPRT [0, 10] **accepted H0** at game 358 (LLR -3.07); hit rate 69%; clock left 34% v 43%; no time losses |
| 2026-10-21 | P2's cap only (PonderCap on) | both off | 575 | +106 -142 =327 | 46.9% | **-22 [-41, -3]** | 10+0.1, as above; SPRT [0, 10] **accepted H0** at game 571; clock left 46% v 40%; no time losses |
| 2026-10-21 | P2's finish only (PonderFinish on) | both off | 614 | +117 -129 =368 | 49.0% | -7 [-24, +11] | 10+0.1, as above; SPRT [0, 10] undecided (LLR -1.50), stopped at a fixed count; clock left 28% v 44%; no time losses |
| 2026-10-21 | P3 (PonderTimeBonus 25) | 0, both pondering | 2000 | +408 -410 =1182 | 50.0% | -0 [-10, +9] | 10+0.1, as above (P2's rules off); SPRT [0, 10] undecided (LLR -2.17) at the 2,000-game cap; 8.5% more clock spent (11% after hits, 5% otherwise); clock left 29% v 40%; no time losses. The option's default is 0 since then |

## The repetition rule (2026-10-21/22)

Stockfish's repetition rule (a draw if the earlier time is inside the
search, or on the third time) against the old one (any position seen once
before is a draw), as `RepetitionRule=Threefold;RepetitionRule=Twofold` in
`scripts/ponder-match.py`'s A/B mode: 10+0.1, both pondering, h1024-3m,
one thread and one core each, 3 games at a time on CPUs 2-12, the bot off.
`--draws occurred`: a game is drawn when a position is there for the third
time, not when the side to move could make it so (python-chess's
claim_draw, which would hold a winner that let a position come round twice
to a draw it was avoiding). Results from the new rule's point of view.

| date | engine | opponent | games | +W -L =D | score | Elo diff [95%] | notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 2026-10-21 | new rule (95a231c) | old rule | 642 | +107 -136 =399 | 47.7% | **-16 [-32, +1]** | SPRT [0, 10] **accepted H0** at game 638 (LLR -3.00); won 79% of its +3.00 leads against 93%: a stale table entry hid threefolds the other side could make |
| 2026-10-22 | new rule + no table cutoff at positions seen before (b5061ed) | old rule | 2,000 | +423 -410 =1167 | 50.3% | **+2 [-8, +12]** | SPRT [0, 10] undecided at the cap (LLR -1.09); won 88% of its +3.00 leads against 89%; 3 time losses (A 2, B 1) while the bot rebuilt its engine beside the match; **on by default since** (Matthew's call) |

Both got ahead equally often in each match. The table fix took the leads
lost to the other side's repetitions from 25 (in 642 games) to 14 (in
2,000). WORKLOG (2026-10-21, the repetition rule) has the tables.

## More search against less (2026-10-21)

Does depth win games? (Matthew, 2026-10-21.) Our engine against itself,
the same build on both sides, with more search on one: a fixed depth
against one ply less (`go depth N`, no clock), or twice the clock. Matthew's
laptop with the bot stopped, the bot's network (h1024-3m), one thread and
Hash 64 each, no tables in the engines (ours adjudicate), the 300 openings
of games/tune/openings-300.txt in colour-reversed pairs, below-normal
priority. Depth pairs ran 7 at a time (a one-thread search to a fixed depth
plays the same game however busy the machine is), the clock pair 5 at a
time, as the matches above did. `scripts/depth-match.py` on branch
claude/gallant-nash-c4b034 (the match tool's per-engine `tc=`, commit
793d71d); `scripts/depth-summary.py` reads the logs (games\depth\).

| date | engine | opponent | games | +W -L =D | score | Elo diff [95%] | notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 2026-10-21 | depth 7 | depth 6 | 600 | +321 -56 =223 | 72.1% | **+165 [+143, +188]** | 4 min |
| 2026-10-21 | depth 9 | depth 8 | 600 | +261 -43 =296 | 68.2% | **+132 [+113, +152]** | 18 min |
| 2026-10-21 | depth 11 | depth 10 | 600 | +207 -31 =362 | 64.7% | **+105 [+88, +122]** | 1 h 41 min |
| 2026-10-21 | 20+0.2 | 10+0.1 | 600 | +212 -65 =323 | 62.3% | **+87 [+68, +106]** | 1 h 26 min; mean depth 12.0 v 10.8 |
| 2026-10-21 | depth 13 | depth 12 | 14 | +3 -0 =11 | 60.7% | +76 [0, +159] | smoke test only; the 300-game run stopped at game 3 so the bot could play |

**One more ply is worth about 20% less for every two plies deeper**:
+165, +132, +105 (each 0.80 of the one before). At that rate depth 13
against 12 would be about +84, which the smoke test's +76 fits. **A ply
costs about 2.2x the time** (the mean time a move over both sides: 21 ms at
depths 6-7, 104 ms at 8-9, 554 ms at 10-11, or 475 ms at the speed of the
other pairs; that pair ran 14% slower in nodes a second). So at depths
10-11, one doubling of time buys 1/log2(2.2) = 0.88 of a ply, about +92
Elo by the depth matches, and **doubling the clock measured +87 [+68,
+106]**: the two methods agree. At 10+0.1 on one thread the engine
reached depth 10.8 on average; at 20+0.2, 12.0.

For scale: 8 threads against 1 (+215, above) is worth about two and a half
doublings, and pondering (+83, 2026-10-15) about one. Draws rise with
depth: 37% of games at depths 6-7, 49% at 8-9, 60% at 10-11, 54% with
double time. Counting each opening's two games together (pentanomial) gave
the same intervals to within 2 Elo. Ends over the 2,400 games: 733
repetitions, 705 by the tables, 491 checkmates, 418 table draws, 50 by the
50-move rule, 3 stalemates; no time losses, crashes or illegal moves.

## Builds against builds (2026-10-21 on)

Our engine against an earlier commit's build, for a change to the search itself (no option to switch it, so both
sides are builds): `scripts/build-match.py`, played as the evaluation matches above were. 10+0.1, the bot's network
(h1024-3m) on both sides, one thread and Hash 64 each, no tables in the engines (ours adjudicate), the 300 openings
of games/tune/openings-300.txt in colour-reversed pairs, 5 at a time, below-normal priority, SPRT [0, 10] (at most
2,000 games), no pondering. Logs in games\builds\. Results from the new build's point of view.

| date | engine | opponent | games | +W -L =D | score | Elo diff [95%] | notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 2026-10-21 | P4 (a cut depth plays the move it proved better), 477d0f8 | P3's tip, 08a4118 | 2,000 | +442 -404 =1154 | 50.9% | +7 [-3, +17] | 5 at a time, the bot stopped; SPRT [0, 10] undecided at the cap (LLR 0.63), LOS 90.4%; halves +14 and -0; equal depth (10.9) and speed; no time losses. The lab saw P4 change no move at this clock, so most likely noise (PONDER.md, "P4") |

## Versions

- **v0.1**: first engine (alpha-beta + PVS, TT, null move, LMR, quiescence,
  simplified piece-square evaluation, endgame tables loaded from disk on first use).
- **v0.2**: endgame tables memory-mapped, and a 50 ms Move Overhead.
- **v0.3**: endgame tables read into memory on `isready`, so the search never
  touches the disk.

## Time losses, explained

Both forfeits came from the first read of an endgame table in the middle of a
search. Capture sequences reach 4-piece positions even from a 7-piece root,
and this container's disk reads only ~65 MB/s: v0.1 read the whole 64 MB file,
and v0.2 page-faulted it in. Neither was counted by the search clock (v0.2
used 1,221 ms with 270 ms left). v0.3 loads every table at `isready` (965 MB,
~15 s here, about a second on a normal SSD) and never reads the disk during a
game.
