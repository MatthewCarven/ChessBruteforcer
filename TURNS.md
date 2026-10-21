# Where the bot's games turned, and whose fault it was

P2.2, 2026-10-21 (a side session, branch `claude/goofy-ellis-ded337`, on `compression` a9e2355). Matthew's
question: *"if all of a sudden you were not winning you'd know if someone made a mistake or you were being
out-calculated"*. This takes every game the bot lost or drew in the week its DEBUG logs keep (2026-10-14 to
10-20) and says where it turned, and why. Script: `scripts/turning-points.py`.

**Short answer.** 502 games; 225 not won (139 lost, 86 drawn), so 182 points lost.

- **Plain mistakes are rare: 5%.** That's a move our own engine, two plies deeper, wouldn't have played.
- **Out-calculated: 23%.** The opponent found a reply our depth didn't see, and two plies more would have seen it.
- **Misjudged: 23%.** Our evaluation was wrong: more depth didn't fix it, and Stockfish had the right score.
- **Outplayed slowly: 20%.** No single moment: a little went each move, and Stockfish agreed we were fine when it
  began.
- **False draws: 7%.** This is one engine rule. The bot counts *any* repeated position as a draw, so it showed
  0.00 in lost positions, and the winning opponent simply didn't repeat.
- The rest: 14% are level draws, never ahead; 10% other (lost on time, worse straight out of the book, and so on).

**The moment our score falls is usually not the moment the game was lost.** At the last move where our engine still
thought we were OK, Stockfish agreed in only 24 of 167 games. In 84 it had had us worse for more than 5 moves, or
ever since the book. And by
Stockfish's reckoning, 69 games turned on one move of ours, but our own score fell at that move in only 11 of them.

## The table

| | where the game turned | games | points lost | share |
|---|---|---:|---:|---:|
| A | **our mistake**: our engine, two plies deeper on the position before, plays another move, and Stockfish agrees ours cost something | 9 | 8.5 | 5% |
| B | **out-calculated**: two plies deeper sees the fall in our move. The opponent found a reply we hadn't predicted (37 games, 34 points), or played the one we had and our own line failed a move later (7, 7) | 44 | 41 | 23% |
| C | **misjudged**: two plies deeper still doesn't see it, and Stockfish already does on the position before. Sudden (22, 19.5) or a slide (31, 21.5) | 53 | 41 | 23% |
| D | **false draw**: our move, or the line it expected, came back to an earlier position; the engine scored it 0.00, and the opponent played on | 13 | 13 | 7% |
| E | **outplayed slowly**: no single fall. Stockfish agreed with us when the slide began, and it went over 2-30 moves | 38 | 36 | 20% |
| F | **never ahead**: a draw in which we never had +1.00 | 50 | 25 | 14% |
| G | **other**: lost on time while not lost (9, 8.5); Stockfish sees no real fall, so our new score was the odd one out (5, 5); worse straight out of the book (3, 3); neither engine saw it coming (1, 1) | 18 | 17.5 | 10% |
| | all | 225 | 182 | |

How a game's turn is found: our score at every engine move, from the logs. The game turned between the last of our
moves where we were still OK (-1.00 or better in a loss; +1.00 or better in a draw) and the first where that was
gone (-3.00; +0.20). If our score fell by 1.00 or more in one move in there, the biggest such fall is the turn
(sudden: A to D). If not, it slid (C, E). The kinds are tested in the order shown.

## Stockfish's view: when did it really go wrong?

At the last move where we thought we were OK, how long ago had Stockfish last thought so? Games / points lost:

| | agreed | 1-2 moves before | 3-5 | 6-10 | 11-20 | more than 20 | never, after the book |
|---|---:|---:|---:|---:|---:|---:|---:|
| A. our mistake | | 3 / 3 | | 2 / 2 | 1 / 1 | | 3 / 2.5 |
| B. out-calculated | 4 / 3.5 | 8 / 8 | 8 / 8 | 8 / 8 | 6 / 6 | 7 / 5.5 | 3 / 2 |
| C. misjudged | 4 / 4 | 7 / 4.5 | 13 / 12.5 | 10 / 8 | 6 / 4.5 | 6 / 3.5 | 7 / 4 |
| D. false draw | 1 / 1 | 1 / 1 | | | 2 / 2 | 8 / 8 | 1 / 1 |
| E. outplayed slowly | 12 / 11 | 11 / 11 | 6 / 5.5 | 5 / 5 | 1 / 1 | | 3 / 2.5 |
| G. other | 3 / 3 | 1 / 1 | 1 / 1 | | 2 / 2 | | 3 / 3 |
| **all** | **24 / 22.5** | **31 / 28.5** | **28 / 27** | **25 / 23** | **18 / 16.5** | **21 / 17** | **20 / 15** |

Where Stockfish itself says the game turned, using the same rule on its own scores: was it one move of ours (with
the reply) costing 0.10 or more of expected score, about 1.00 at equality? And did our own score fall at that move
too?

| | one move: our score fell 1.00+ there | fell 0.40-1.00 | fell less, or not at all | gradual | never OK after the book |
|---|---:|---:|---:|---:|---:|
| A. our mistake | 2 / 2 | | | 4 / 4 | 3 / 2.5 |
| B. out-calculated | 3 / 3 | 6 / 6 | 15 / 14.5 | 17 / 15.5 | 3 / 2 |
| C. misjudged | 3 / 3 | 5 / 4.5 | 12 / 10 | 26 / 19.5 | 7 / 4 |
| D. false draw | 2 / 2 | 2 / 2 | 2 / 2 | 6 / 6 | 1 / 1 |
| E. outplayed slowly | | 5 / 4.5 | 9 / 8.5 | 21 / 20.5 | 3 / 2.5 |
| G. other | 1 / 1 | 1 / 1 | 1 / 1 | 4 / 4 | 3 / 3 |
| **all** | **11 / 11** | **19 / 18** | **39 / 36** | **78 / 69.5** | **20 / 15** |

So in Stockfish's eyes most games went gradually (78), and where one move did it (69), our engine mostly didn't
notice at the time (39). Two caveats. This is Stockfish 16 at depth 16, one position per move of ours. And picking
games we didn't win picks the positions where we and Stockfish disagree. On games we won the two scales agree
(slope 0.94, median difference -0.17, 2,099 positions; "How it was done").

## By time control, opponent and evaluation

Points lost, by the groups above:

| time control | A | B | C | D | E | F | G | all |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| bullet (under 3 min) | | 4.5 | 6 | 1 | 7 | 2.5 | 2 | 23 |
| blitz 3 min (3+x) | 4 | 17 | 16.5 | 10 | 14 | 11.5 | 7 | 80 |
| blitz 5 min (5+x) | 4.5 | 16.5 | 9.5 | 2 | 10 | 9.5 | 6 | 58 |
| rapid and longer | | 3 | 9 | | 5 | 1.5 | 2.5 | 21 |

| opponent's rating | A | B | C | D | E | F | G | all |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| under 2000 | 1 | 6.5 | 3.5 | 2 | 0.5 | 3 | 2 | 18.5 |
| 2000-2199 | 1 | 5.5 | 8 | 1 | | 4.5 | 4 | 24 |
| 2200-2399 | 3 | 16 | 11 | 4 | 8.5 | 3 | 1 | 46.5 |
| 2400+ | 3.5 | 13 | 18.5 | 6 | 27 | 14.5 | 10.5 | 93 |

| evaluation (days) | games | A | B | C | D | E | F | G | points lost |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| hand-written: built in, then tuned eval-sf1 (10-14 to 10-17; 1 thread until 10-16, then 8) | 264 | 6 (6%) | 33 (33%) | 27 (27%) | 9 (9%) | 9.5 (9%) | 8 (8%) | 8 (8%) | 100.5 |
| a network: nnue1, nnue1-h512, h512-3m, h1024-3m (10-17 to 10-20; 8 threads) | 238 | 2.5 (3%) | 8 (10%) | 14 (17%) | 4 (5%) | 26.5 (33%) | 17 (21%) | 9.5 (12%) | 81.5 |

With the networks and 8 threads, being out-calculated went from a third of the lost points to a tenth, and being
outplayed slowly from a tenth to a third. But the opponents changed too. Their median rating went from 2030 to 2464,
and ours from 2084 to 2501. So read it as "stronger engine, stronger opponents", not the network alone.

Where it turned, by the pieces on the board then: 6-7 pieces 6.5 points, 8-12 pieces 23.5, 13-20 pieces 51.5, 21-32
pieces 67; 33.5 had no turn (F, and the flags). So most games turn in the middlegame, not the ending.

## Examples

Each link opens the game at the position before the move in question.

- **A, our mistake**: [RoBLgHNd](https://lichess.org/RoBLgHNd#45), move 23 (10-15, tuned evaluation, 1 thread,
  opponent 2031). We were at -0.85 and played Qf4. Two plies deeper prefers Bf8 (-1.14 against -2.37). Stockfish
  agrees: Bf8 -2.55, Qf4 -5.41.
- **B, out-calculated**: [7fu8ANJ1](https://lichess.org/7fu8ANJ1#69), move 35 (10-19, h512-3m, bullet, opponent
  2568). At -1.30 we expected Rd6, they played Rg3, and our score went to -2.95. Two plies deeper already sees
  -2.79 in our move. Stockfish had agreed with us until then.
- **C, misjudged, sudden** (today's network): [7LWWHoYo](https://lichess.org/7LWWHoYo#91), move 46 (10-20,
  h1024-3m, opponent 3022). A 7-piece ending: we said 0.00 for two moves and Stockfish said -4.5; then we saw -3.31.
  Two plies deeper still says -1.28.
- **C, misjudged, a slide**: [UBIA6Hdc](https://lichess.org/UBIA6Hdc#130), TODO's knight ending: +2.39 at move 56
  down to +0.07 at move 72, while Stockfish said +0.1 to +0.2 the whole way. Also
  [bhJE4lVW](https://lichess.org/bhJE4lVW#173) (+2.02 for 11 moves in a 6-piece ending; Stockfish 0.00) and
  [kq9Vc4JD](https://lichess.org/kq9Vc4JD#107) (+1.44 for 11 moves, 7 pieces; Stockfish 0.00).
- **D, false draw**: [uS0ooncI](https://lichess.org/uS0ooncI#85), move 43 (10-14). Qb6 repeated a position, and we
  said 0.00 with no reply in the line (and so no ponder move). Stockfish said -5.98; the opponent played Qf4 and we
  were at -7.85. Stockfish had had us at -1.6 or worse since move 29, while we said +0.9 to +1.9.
- **E, outplayed slowly**: [U3vZtQ0x](https://lichess.org/U3vZtQ0x#28): from -0.38 at move 15 to -3.00 at move 46
  with no single fall (Stockfish: -0.94 to -6.17). [21w1Gt5m](https://lichess.org/21w1Gt5m#101) (a draw): +1.43 at
  move 51 with Stockfish at +1.39. Then one move cost it all for Stockfish (to 0.00) but only 0.82 for us, and we
  drifted to the draw.

## Every fall, not only the turns

All 304 falls in the 225 games, each re-searched the same way. (The run was still going when this was written: 135
of 304 are in; the rest are falls in games already lost, and this table is updated when it ends.)

| kind | falls from -2.00 or better | falls from below -2.00 (already lost) |
|---|---:|---:|
| mistake | 15 | 1 |
| out-calculated | 33 | 11 |
| seen late (our predicted line) | 4 | 3 |
| misjudged | 28 | 4 |
| unexplained | 1 | 0 |
| phantom (Stockfish sees no fall) | 5 | 2 |
| false draw | 28 | 0 |
| not searched yet | 30 | 139 |

## How it was done

- **Scores.** lichess-bot's DEBUG log has every UCI line. Our score for a move is the last `info` line of the
  search that chose it. For a ponder hit, that includes the lines from before `ponderhit`, which python-chess (and
  so the bot's PGNs) drops. So every one of the 29,020 engine moves has a score, before P1 too. Results, ratings and
  time controls come from bot/games. Scores are clamped to +-10.00; mates count as +-10.00.
- **A fall**: our score 1.00 or more below our score one move before, and our expected score (Lichess's curve) at
  least 0.05 lower. So +9.00 to +7.50 isn't one.
- **The re-search** (`search`) runs on the position before the fall. Our engine is a copy of the bot's build (Oct 20,
  22:11) with the game's own evaluation file, endgame tables on, Hash 256, 2 threads on CPUs 14-15 at below-normal
  priority. It searches to the depth the bot reached + 2 (capped at 90 s), on the position, then on our move alone.
  Then Stockfish 16 (WSL, 1 thread) to depth 20 on the position, on our move alone, and on the position after the
  reply. Of the falls searched so far, 83 reached +2 plies, 20 +1, and 32 the same depth or less. Those 32 are mostly
  the false draws, where the bot's own depth was inflated by the repetition cutting its search short.
- **The kinds**, with s0 our score before the fall and "half" half the fall below it, tested in this order:
  - mistake: our deeper search prefers another move by 0.50 or more, and Stockfish says ours cost 0.05+ of expected
    score;
  - false draw: our move scored exactly 0.00, and it or its logged line comes back to a position from the game so far;
  - phantom: Stockfish after the reply is above half;
  - out-calculated: our deeper search of our move is at or below half. It's "seen late" if the reply was the one we
    predicted;
  - misjudged: Stockfish's score for our move is at or below half;
  - unexplained: otherwise.

  For a slide, Stockfish on its first position at or below half means misjudged; otherwise outplayed.
- **Stockfish's curve** (`curves`, depth 16): every move of ours from the book to the end of the stretch, 7,035
  positions. **Calibration** (`calibrate`): every 5th move of ours in games we won, 2,099 positions. Stockfish
  against us there: slope 0.94, median difference -0.17. Where we said +0.50 to +2.00, Stockfish's median was +1.02.
  In the games we didn't win, Stockfish's median there was +0.08.
- **The re-search sees no game history** (it starts from the position's FEN), so it can't see repetitions. That's
  why the false draws are told apart by the repetition itself, not by the search.
- **Mixed builds**: five evaluations and two thread counts over the week, re-searched with today's search code and
  each game's own evaluation. The kinds say what a deeper search of *that* evaluation does. Opponents got stronger as
  the bot did (above).

## The false draw, in the code

`Search.IsRepetition` (Search.cs:823) says a position is a repetition if it occurred once before, since the last
capture or pawn move, anywhere in the history: the game before the search as well as the search's own line.
`Negamax` scores every repetition as a draw (Search.cs:556). So a move back to a position from three moves ago scores
0.00 at once, and the line stops there, so there's no reply to ponder on. A position seen once before the search
isn't a draw unless it comes round a third time, and a winning opponent won't let it. Stockfish counts a repetition
as a draw only if the earlier position is inside its own search, or if the position has now occurred three times.
In lost games, 94 of our moves scored 0.00 because the move (54) or its line (40) came back to an earlier position;
Stockfish had 67 of those 94 at -1.50 or worse. (Drawn games had 333 such moves, which is harmless when the position
is level.) Another 51 moves in lost games scored exactly 0.00 at -1.50 or worse for Stockfish with no repetition in
the logged line. Some may be the same thing out of sight: a draw score from a repetition kept in the table and met
again by another path.

## What it suggests (Matthew's call)

- **(a) The repetition rule.** Count a repetition as a draw only inside the search, or on the third occurrence, as
  Stockfish does. It's a small change in `Search.IsRepetition`. 13 games (7% of the lost points) turned on it, and it
  hid when they'd really turned: Stockfish had 8 of them lost for 20+ moves. It needs an A/B match.
- **(b) Evaluation (C, 23%).** The misjudged positions make a test set for the networks, ours against Stockfish. The
  knight-ending kind is in there: +1.4 to +2.4 in endings Stockfish called 0.00.
- **(c) Depth (B, 23%).** The ponder work (P2-P6) and speed give depth. p2.1 measured a ply at about +105 Elo at
  this depth. With the networks and 8 threads, B is already down to 10%.
- **(d) Nothing for A.** Plain mistakes are 5%.

## Scripts and data

- `scripts/turning-points.py extract | falls | search | stretches | curves | calibrate | table`. Run it with the bot's
  venv python, `-I`. `search` can be stopped any time and carries on; `search --reverse` runs a second worker from the
  other end, and each skips what the other has done.
- Data in `games/turns/` (not in git): games.json (our score at every engine move), searches.jsonl,
  stretches.jsonl, curves.jsonl, calibrate.jsonl, and the run logs. The engine copy is in games/turns/engine.
