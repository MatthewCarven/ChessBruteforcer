# Pondering: what the engine keeps, and what it throws away

A scoping pass, 2026-10-21 (a side session, branch `claude/ponder-tree-scope-5945b6`). The
engine was measured at `compression`'s 81173f8. The branch now sits on 6975e96, with no `src`
change between the two. This pass changed nothing in `src`. Matthew's question: when the bot ponders
(thinks on the opponent's time) and then moves from that ponder, does the engine keep the part of
the tree it was working on?

**Short answer: yes, it keeps all of it. But by design it doesn't use the extra time pondering
gives it.** A ponder hit plays the same move, from the same search, that a plain search on our own
clock would have played. It just plays it sooner. The saved time goes into the bank, and the bank
is spent slowly, so games end with a lot of clock left.

| question | answer | evidence |
|---|---|---|
| 1. Ponder hit (the opponent plays the predicted reply): does the same search go on? | **Yes.** Same thread, same depth loop, same table, history, killers and PV. The depth goes on from where the ponder got to. | Code: `Search.PonderHit` only changes the limits. Lab: 495 of 495 depths finished by ponder searches had exactly the node count of a plain `go`'s same depth. Bot log: 745 of 745 hits that reported after the hit went on at the next depth; none started again at depth 1. |
| 2. Ponder miss (`stop`, then a new `position` and `go`): is the table kept? | **Yes.** `stop` and `position` clear nothing. Each `go` clears only the history and killer tables. Only `ucinewgame`, `Hash` and `EvalFile` clear the table. **But** on the bot's 8 threads, a long ponder on the wrong reply overwrites some of what our own search left for the real position. | Code below. Lab: the next search does 1.08-1.33x the work of no ponder after an 8-thread miss; 1.18x from scratch. |
| 3. After our own move, does the next search reuse the last one's tree? | **Yes, through the table.** No `ucinewgame` comes between moves. The early depths come nearly free; the last few cost about what they'd cost from scratch. | Bot log: ponder searches finish depth 12 in 36 ms (median), against 981 ms for a search with nothing of ours before it. Lab: at the deepest depth, 0.90x the nodes of a cold search when the position was our own line. |
| 4. After a ponderhit, does the clock count the pondering? | **It counts the pondering as spent.** The budget runs from `go ponder`. A hit uses only what's left of the move's budget, and often nothing. The bank is spent only as each later move's budget (1/30 of the clock left) grows. | Code: `Search.Prepare`, `Search.TimeBudget`. Lab: ponder for a quarter of the budget, then the hit: the identical search, 1.2 s sooner. Bot log: 3+2 games end with 41% of the clock left, against 22% before pondering. |

## How the pieces fit

- **lichess-bot** (`lib/engine_wrapper.py`, `search()`) calls python-chess's `engine.play(board,
  limit, ponder=True)` with no `game`. It makes a new engine every game (WORKLOG 2026-10-15/16).
- **python-chess** (`chess/engine.py`, `UciProtocol.play`):
  - After `bestmove m ponder r`, it sends `position` on the board after m and r, then
    `go ponder` with the clocks moved on (ours: the clock less the time used, plus the
    increment; :1666-1680).
  - On the next `play()`: if the new board and its moves equal the pondered one (same game,
    options unchanged; :1599, :1691), it sends `ponderhit` and nothing else (:1608-1611).
    Otherwise it sends `stop`, then the usual `position` and `go` (:1695, :1646).
  - It keeps only the `info` lines that arrive after `ponderhit` (:1649-1651).
  - `ucinewgame` goes out only when `first_game` is set or `game` changes (:1624). With no
    `game` passed, that's once an engine, so once a game. The bot's log agrees: 43 a day for
    43 games on 2026-10-20.
  - Options go out only when they change (`_setoption`, :1461), so `Hash` and `EvalFile`
    (which clear the table) are sent once, at the engine's start.
- **The engine** (`src/ChessBruteforcer.Core/Engine`):

| command | what it does to what the search knows |
|---|---|
| `go ponder <clock>` | A new search (`Search.Prepare`). It clears the history and killers, keeps the table and starts its clock now. It works out the budget from `<clock>` but holds it back (soft and hard limits 0) until `ponderhit`. |
| `ponderhit` | `Search.PonderHit` (Search.cs:166): the budget now applies, counted from `go ponder`. A depth begun past half the planned time is stopped at once, and the answer is the last finished depth. Otherwise the depth goes on, as it would in a plain search. |
| `stop` | Stops the search. Clears nothing (UciEngine.cs:103). |
| `position` | Stops any search and sets the board. Clears nothing (UciEngine.cs:90, `SetPosition`). |
| `go` | A new search: clears the **history and killers** (Search.cs:325-326), keeps the table. Every new search starts at depth 1. |
| `ucinewgame` | Clears the **table** (UciEngine.cs:85). |
| `setoption Hash` / `EvalFile` | A new table / clears it (UciEngine.cs:145, :188). |
| `setoption Ponder` | Accepted, changes nothing (UciEngine.cs:163). |

The table has no age. A slot is replaced by any other position, and kept only against a
shallower result for the same position (`TranspositionTable.Store`). So nothing marks an entry
as left over from an old search, and nothing protects one.

## What a hit does in the bot (its log, 2026-10-20)

The DEBUG log (`bot/lichess-bot/lichess_bot_auto_logs`) has every UCI line with a timestamp.
That day: 1,369 plain `go` searches and 2,416 `go ponder`, of which 1,364 hits and 1,052 misses.
That's a hit rate of **56.5%** (forecast-hits.py said 51% on older games). Each move's budget
was worked out from its clock, as `Search.TimeBudget` does.

| after `ponderhit` | hits | share | real clock used after the hit (median) | pondered / planned time (median) |
|---|---:|---:|---:|---:|
| stopped at once: the depth in progress began past half the budget | 503 | 36.9% | 1 ms | 0.97 |
| went on and finished a depth (an `info` sent) | 745 | 54.6% | 2.6 s | 0.60 |
| went on, no depth finished (the hard limit cut it) | 116 | 8.5% | **10.4 s** | 0.82 |

| a hit, 2026-10-20 | p25 | median | p75 |
|---|---:|---:|---:|
| pondered before `ponderhit` | 2.0 s | 3.5 s | 6.5 s |
| planned time for the move (soft budget) | 3.4 s | 4.8 s | 7.7 s |
| depth finished at `ponderhit` | 13 | 15 | 16 |
| depth played | 14 | 15 | 17 |

Searches after a miss are ordinary searches. They used a median 3.7 s and played depth 15, the
same as the hits.

## What's lost, with numbers

1. **The half-finished depth on 37% of hits.** When the depth in progress began past half the
   budget, the hit stops it and plays the depth before. That depth had run a median **1.2 s**
   (p75 2.5 s) on 8 threads, about half as long as the depth before it took (median ratio 0.52).
   Meanwhile the whole budget for the move (median **4.4 s**) is still on the clock. Those
   seconds were never at risk.
2. **Depths that never finish.** On 8.5% of hits the depth in progress had begun before half
   the budget, so it went on after the hit, and the hard limit (3x the budget, counted from `go
   ponder`) cut it. Real clock spent after the hit: median 10.4 s, mean 16.4 s, for nothing:
   the move came from the ponder's last finished depth. Over the day, **37% of all the real
   clock spent after hits (2,159 of 5,911 s) went on depths that never finished**, against 14%
   (1,237 of 9,042 s) for ordinary searches. The engine throws an unfinished depth away
   (Search.cs:244), even when it has found a better move there.
3. **The bank.** The engine spends the saved clock only as later budgets (1/30 of what's left)
   grow:

   | time control | ponder off (10-14, 10-15): games | clock left at the last search | ponder on (10-18 to 10-20): games | clock left |
   |---|---:|---:|---:|---:|
   | 3+2 | 48 | 40 s (22%) | 45 | **80 s (41%)** |
   | 3+3 | 24 | 63 s (33%) | 34 | 95 s (48%) |
   | 5+2 | 36 | 57 s (19%) | 31 | 90 s (29%) |
   | 5+3 | 37 | 68 s (22%) | 29 | 121 s (38%) |

   (The builds differ between the two periods: the evaluation, and threads from 10-16. So read
   the doubling as mostly pondering, not all of it.)
4. **No stats on 45% of hits** (619 of 1,364, 23% of all engine moves). These are the hits
   stopped at once, plus those whose depth never finished. python-chess keeps only what comes
   after `ponderhit`, and nothing does. lichess-bot then scores the move as "mate in 1 for us",
   its placeholder, which breaks a run of draw-offer scores. Offers still went out 144 times
   that day, so the effect is small.
5. **Misses.** The ponder's tree is for a position the game didn't reach, so it's lost. Worse,
   on the bot's 8 threads a ponder of 0.8-1.5x the budget overwrites part of what our own search
   had left in the table for the position the game did reach. The next search then does 8-24%
   more work for the same depth (on one thread there's no such effect). See "Misses" below.

## The lab runs

A Release build of this branch, driven over raw UCI (`scripts/ponder-scope`, below). One
thread, Hash 256, the bot's network (h1024-3m), Move Overhead 100, no endgame tables.
Positions: 12 hits and 12 misses from the bot's games of 2026-10-20 (every 79th and 57th
candidate, middlegames, 12+ pieces), and 8 of the misses again with the ponder on 8 threads.
One clock for all: 100 s + 2 s, so a 4.8 s budget (the bot's median), hard limit 14.5 s. Before each arm: `ucinewgame`, then our own search on the position before (A), to the
fixed depth it reached in 2.5 s. So every arm starts from the same table. On one thread the
search repeats node for node.

### Hits: a ponder then `ponderhit`, against a plain `go`

| arm | pondered | real clock used | depth played (median) | nodes (median) | same move as `go` | same nodes as `go` |
|---|---:|---:|---:|---:|---:|---:|
| `go` from scratch (`ucinewgame`, no own search before) | 0 | 3.0 s | 14 | 9.8 M | 10/12 | |
| `go` (after our own search; no pondering) | 0 | 3.4 s | 14 | 11.4 M | | |
| ponder 1/4 of the budget, then `ponderhit` | 1.2 s | 2.2 s | 14 | 11.4 M | 12/12 | **12/12** |
| ponder 3/4 of the budget, then `ponderhit` | 3.6 s | 0 | 14 | 11.9 M | 12/12 | 11/12 |
| ponder 1.5x the budget, then `ponderhit` | 7.2 s | 0 | 14 | 16.7 M | 10/12 | 7/12 |

| arm | depth v `go`: deeper / same / shallower | `info` after the hit | the depth thrown away had run (median) |
|---|---|---:|---:|
| ponder 1/4 of the budget | 0 / 12 / 0 | 11/12 | |
| ponder 3/4 of the budget | 1 / 11 / 0 | 4/12 | 0.1 s |
| ponder 1.5x the budget | 5 / 7 / 0 | 0/12 | **2.7 s** |

So a hit is a plain search that started early. Pondered time comes off the move's budget, and
only a ponder that runs past the budget can finish a depth the clock wouldn't have reached
(5 of 12 at 1.5x).

### After our own move: the table from the move before (question 3)

The plain `go` against the one from scratch, on the same pondered position P:

| depth | from scratch (median) | after our own search (median) | n |
|---:|---:|---:|---:|
| 6 | 9 ms | 2 ms | 12 |
| 8 | 47 ms | 2 ms | 12 |
| 10 | 304 ms | 6 ms | 11 |
| 12 | 593 ms | 407 ms | 9 |
| 14 | 1,571 ms | 1,585 ms | 6 |

| at the deepest depth both reached | positions | nodes, after / from scratch (median) | time, after / from scratch (median) |
|---|---:|---:|---:|
| all | 12 | 0.97 | 1.10 |
| P was our own search's line (its PV) | 7 | 0.90 | 0.86 |
| P was not | 5 | 1.11 | 1.19 |

Our own search had looked at P's subtree to about two plies less than its own depth. That
makes the next search's first ~11 depths nearly free, but those were cheap anyway. The last
two or three depths cost about what they cost from scratch. In the bot (8 threads) a ponder
search finishes depth 12 in a median 36 ms, against 381 ms for the search after a miss and
981 ms for a search with no search of ours just before it (after book moves).

### Misses: does pondering the wrong reply help or hurt the next search? (question 2)

Before each arm, our own search at A. Then `stop`, and the position the game really reached (B)
searched to a fixed depth D, the depth a search from scratch reaches there in 3 s (median 13).
On one thread the node count to depth D measures the head start: fewer nodes, more head start.

| search at B, to depth D | nodes (median) | time (median) | nodes v "no ponder" (geometric mean) | table filled at the `stop` |
|---|---:|---:|---:|---:|
| from scratch (`ucinewgame` first) | 9.3 M | 2.55 s | 1.13 | |
| after our own search, no ponder | 7.6 M | 2.10 s | 1.00 | |
| after our own search, then 3/4 of the budget pondering the wrong reply | 7.4 M | 2.06 s | 0.98 | 9.6% |
| ... 1.5x the budget | 7.0 M | 2.10 s | 1.11 | 15.5% |
| ... 3x the budget | 6.6 M | 1.93 s | 1.02 | 22.3% |

On one thread, then, the table from our own search is worth about 12% at the next position, and
pondering the wrong reply neither adds to that nor takes it away, on average. One position at a
time it swings both ways (0.47x to 2.98x), since a different table sends the search down a
different tree.

**But the bot ponders on 8 threads**, which fills the table about five times as fast. The same
runs with the ponder on 8 threads (8 of the 12 positions; the search at B still on one thread):

| search at B, to depth D | nodes (median) | time (median) | nodes v "no ponder" (geometric mean) | table filled at the `stop` | time to depth D-4 (median) |
|---|---:|---:|---:|---:|---:|
| from scratch (`ucinewgame` first) | 9.6 M | 2.57 s | **1.18** | | 130 ms |
| after our own search, no ponder | 8.2 M | 2.12 s | 1.00 | | 33 ms |
| ... then 3/4 of the budget pondering the wrong reply | 7.6 M | 2.05 s | 1.08 | 27% | 70 ms |
| ... 1.5x the budget | 9.3 M | 2.65 s | **1.24** | 48% | |
| ... 3x the budget | 8.5 M | 2.39 s | **1.33** | 47% | 143 ms |

Here a long ponder on the wrong reply overwrites what our own search had left for the real
position, and then some. After 1.5-3x the budget the next search does worse than one from
scratch, and its early depths come no faster than from scratch. In the bot a miss ponders a
median 3.9 s (p75 7.0 s), about 0.8-1.5x the budget, so its next search does roughly 8-24% more
work for the same depth. That's about a fifth of a ply. The cause is the table's replacement
rule: any new position takes the slot, so shallow entries from the ponder push out deeper ones
from our own search. That's P6 below. The move played at B was the same as with no ponder in
10-11 of 12 (one thread) and 7 of 8 (8 threads). The one other thing a new `go` clears, the
history and killer tables, is P5.

### What other hit rules would do (a replay)

On one thread the search runs identically whatever its limits, until something stops it. So
one long ponder per hit position (4x the budget, every depth's finish time recorded) can be
replayed for a hit at any moment, under any rule (`simulate.py`). The rules:

- **today**: as the engine does now (above).
- **finish**: as today, but a depth that today would be dropped at the hit may run on to its
  end, if it gets there within the move's planned time S of real clock after the hit. No
  further depth after it. (P2 below.)
- **capped**: finish, and also anything that runs on after the hit stops by hit + S, instead of
  at the hard limit (3S from `go ponder`).
- **fresh**: the hit starts a whole new budget, as if the pondering were free (the rule the
  pondering session turned down on 2026-10-15).

| hit at (times the budget S) | rule | depth played (median) | deeper than today | real clock after the hit: median | mean | clock spent, no depth gained |
|---|---|---:|---:|---:|---:|---:|
| 0.5 S | today | 14 | | 1.1 s | 2.0 s | 1/12 |
| 0.5 S | capped | 14 | 0/12 | 1.1 s | 1.4 s | 2/12 |
| 0.5 S | fresh | 15.5 | 10/12 | 5.6 s | 6.0 s | 1/12 |
| 0.75 S | today | 14 | | 0 | 1.3 s | 1/12 |
| 0.75 S | finish / capped | 15 | **5/12** | 3.8 s | 3.5 / 3.0 s | 4/12 |
| 0.75 S | fresh | 15.5 | 10/12 | 5.2 s | 6.9 s | 1/12 |
| 1.0 S | today | 14.5 | | 0 | 1.1 s | 1/12 |
| 1.0 S | finish / capped | 15.5 | **9/12** | 3.1 s | 3.3 / 2.9 s | 2/12 |
| 1.0 S | fresh | 15.5 | 10/12 | 4.7 s | 6.3 s | 1/12 |
| 1.5 S | today | 14.5 | | 0 | 0.7 s | 1/12 |
| 1.5 S | finish / capped | 15.5 | **7/12** | 2.3 s | 2.9 / 2.7 s | 4/12 |
| 2.0 S | today | 15.5 | | 0 | 0.4 s | 1/12 |
| 2.0 S | finish / capped | 16 | **8/12** | 3.1 s | 3.3 s | 4/12 |

In the bot, hits come at a median 0.75 S (p75 1.0 S), and 37% of them are the "stopped at once"
kind that "finish" changes. Finishing gives one more ply in roughly half to three quarters of
those hits, for 2-4 s of real clock each. That's never more than the move would have used with
no pondering at all. A 3+2 game has ~28 hits, ~10 of them that kind, so it would spend ~30 s of
the ~40 s that pondering now leaves unspent. "Fresh" gains a little more depth but spends
nearly a whole budget on every hit, which is the clock pondering exists to save.

## Proposed changes

All are in `src`, so they wait for the work in flight there (the speed branch
`claude/stoic-goodall-ecc132`, the bitboard sessions). P1-P3 touch only `UciEngine` and the
time-keeping in `Search`, away from what those branches change. P4 and P6 touch the search's
core and the table, so they go after. The Elo figures are **guesses**, not measurements. The
only figure measured here is pondering's own +83 at 10+0.1, against itself. Each change is
measured before it reaches the bot.

| | change | where | effort | gain (measured above) | gain in Elo (guess) | how to measure |
|---|---|---|---|---|---|---|
| **P1** | **A final `info` after ponderhit** (the card "Engine: a final info line after ponderhit", task_16a444a7). When a ponder search answers after a hit with nothing reported since the hit, send its last `info` again (depth, score, pv; nodes and time as of now) just before `bestmove`. | UciEngine: remember the last line, and whether one went out since `ponderhit` | ~1 h, 1 test | Stats on the 23% of engine moves that have none (45% of hits); lichess-bot's draw-offer runs no longer broken by its placeholder score | 0 | PonderTests: a hit answered at once still sends an `info` between `ponderhit` and `bestmove`. The bot's log: no more "no depth finished after the hit". |
| **P2** | **A hit may finish the depth it would drop, within the move's planned time.** At `ponderhit`, a depth begun past half the budget isn't stopped: it gets until hit + S of real clock to finish, and no new depth starts after it. Also cap anything else that runs on after the hit at hit + S, not at the hard limit 3S from `go ponder` (the "capped" rule). | `Search.PonderHit` and the hard limit | ~2 h, 2-3 tests | +1 ply on ~half to three quarters of the 37% of hits that stop at once, for 2-4 s each. Spends ~30 s of the ~40 s a 3+2 game now banks. The cap trims the 8.5% of hits that now run a median 10.4 s for nothing to at most S. | +5 to +15 | A/B match, both sides pondering (below). |
| **P3** | **Plan more time a move when pondering**, as Stockfish does (+25% to the planned time when `Ponder` is on). Spends the bank on every move, the misses included. | `Search.TimeBudget`, and the `Ponder` option (now ignored) | ~1 h, 1 test | The bank: 41% of the clock left at the end of 3+2 games, against 22% with no pondering | +5 to +15, partly the same gain as P2 | The same A/B match, after P2 |
| **P4** | **Play an unfinished depth's better move** instead of throwing the depth away. When the hard limit cuts a depth after a root move other than the last depth's best has been searched and proved better, play it. This is standard in engines. | `Search.IterativeDeepening` (Search.cs:244), the root of `Negamax`, and the Ranges path | about half a day, tests | Gives some value to the 37% of the clock after hits and 14% of ordinary searches' clock now spent on depths that never finish. Not pondering's own loss, but where it shows most. | +5 to +20 | An SPRT match against the current build, no pondering, then the ponder A/B |
| **P5** | **Keep the history table between moves** (halve it, don't clear it, at each `go`). The table already carries over; the move-ordering history doesn't. | `Search.Prepare` (Search.cs:325) | ~30 min, plus a bench | Not measured. The search after a miss starts its ordering cold. | 0 to +5 | Nodes to a fixed depth on runs of positions from real games, then a match if that shows anything |
| **P6** | **A table that keeps its deep entries**: slots in buckets of 2-4, with a search number (age) in each entry. A new entry replaces the shallowest or oldest in its bucket, not whatever is there. Engines commonly do this. The slot has room for an age: the move's flags use 4 of their 8 bits, and its squares 6 of 8. | `TranspositionTable` (Store, TryGet, the slot layout; the lockless XOR stays) | about half a day, tests | Wins back the 8-24% extra work after an 8-thread miss (above). Helps every search that fills the table, ponder or not. | +3 to +10 | This harness's miss runs with an 8-thread ponder (nodes to depth D), then an SPRT match |

Hash itself is a cheaper lever (512 MB would halve the filling), but the laptop's commit is
tight (TODO: memory ran out on 2026-10-19), so P6 rather than more memory.

**The A/B match.** `scripts/ponder-match.py` plays ponder on against ponder off. P2 and P3 need
both sides pondering, differing only in the change (and P4 and P6, once their plain match is
in). The simplest way is a UCI option per change
(e.g. `PonderFinish`, default on), so one build serves both arms, plus an `--arms` mode in the
script (~1 h). Play 10+0.1 first, because it's quick, then a control with an increment, like
the bot's 3+2: the bank only builds up with an increment. Against itself the hit rate is 71.5%,
against 56.5% on Lichess, so the match overstates anything that works through hits. A run
against Stockfish (`book-match.py`, the TODO's "Lichess-like hit rate") would settle that.

## Decision M: what to do (Matthew's)

- **(a)** P1 only: every move gets its stats. An hour, nothing to measure but the log.
- **(b)** P1 + P2, with the A/B match mode. Fixes pondering's own loss: the half-searched depth
  dropped with the budget unspent, and the long runs after a hit. About half a day with the
  match.
- **(c)** (b) + P3 measured on top: the whole of "spend the banked clock".
- **(d)** P4 and P6 first, as changes to the search and its table in general, then (b) on top.
- **(e)** Nothing for now.

My pick: **(b)**, then P4 and P6 as a decision of their own. P2 is small and goes straight at
what this pass found. It spends only clock the bank already holds, and never more on a move than
pondering off would. P1 rides along: it's an hour, and its test sits beside P2's in PonderTests. P3 is
worth trying only if a bank still shows after P2.

## Results

### P1: a final `info` after ponderhit (2026-10-21, done)

A side session, branch `claude/p1-final-info-a1e9a9`. **When a ponder search that got `ponderhit` answers
with no `info` line sent since the hit, the engine now sends its last line again, just before `bestmove`.**
The depth, score and line (pv) are the same. Nodes, time and hashfull are as of the answer, with the time
counted from `go ponder`, as the engine's clock counts it. Nothing else changes: no new depth, the same move.
A miss (`stop`) sends nothing again.

That covers every kind of hit that had no stats: those stopped at once, those whose depth in progress the
hard limit cut, a ponder search that ended by itself (a mate seen, a depth limit), and a move from the
endgame tables, which is reported as depth 1 while pondering. lichess-bot then logs real stats for those
moves, and its draw-offer and resign runs no longer see its "mate in 1 for us" placeholder.

An example from the run below. The hit came at 1,142 ms, past the budget, and stopped the search at once:

```
>> info depth 12 score cp 35 nodes 12170983 nps 15119233 time 805 hashfull 517 pv b1c3 g8e7 a2a3 b4a5 ...
<< ponderhit
>> info depth 12 score cp 35 nodes 17038733 nps 14920081 time 1142 hashfull 552 pv b1c3 g8e7 a2a3 b4a5 ...
>> bestmove b1c3 ponder g8e7
```

**How.** UciEngine keeps the search's last line and two flags (the hit came; a line went out after it)
under one lock. Every `info` line is sent under that lock, and `ponderhit` notes the hit and calls
`Search.PonderHit` under it too. So a hit that answers at once can't miss the hit, and a line written
before the engine read `ponderhit` doesn't count as after it. (python-chess starts keeping lines just
before it sends `ponderhit`, so it may count such a line as well. Then the line sent again repeats a depth
it already has, and its stats just take the newer nodes and time. Harmless.) `Search.Elapsed` (the
search's clock) is new.

**Measured**, with the engine driven through python-chess exactly as lichess-bot drives it (`play(...,
info=INFO_ALL, ponder=True)`). A game of 60 plies against nobody: 4 threads, a 6 s + 0.5 s clock (about
0.57 s planned a move). The opponent waits 0, 0.25, 0.8 or 2 times the planned time, and plays the reply
we pondered on four times in five.

| build | hits | hits with no depth or score in python-chess's result | hits with no `info` after `ponderhit` (ponderlog.py) |
|---|---:|---:|---:|
| before P1 (1d56571) | 27 | **14 (52%)** | 14 |
| with P1 | 33 | **0** | 0 (in 14, 42%, the only line after the hit is the one sent again) |

The two runs' games part early, since the timing differs, hence 27 hits against 33. In the bot on
2026-10-20 it was 45% of hits (619 of 1,364).

**Tests** (PonderTests, 13 → 20):

| test | threads | what it checks |
|---|---|---|
| APonderHitAnsweredAtOnceSendsItsLastInfoAgain | 1, L4, R4 | Ponder past the whole budget, then `ponderhit`, twice in one engine. An `info` line comes between `ponderhit` and `bestmove`: the last depth's depth, score and pv, more nodes, and a time counted from `go ponder`. |
| APonderHitThatFinishesADepthSendsNothingAgain | 1, L4, R4 | A hit at once on a 10 s clock: depths finish after the hit, and no depth is sent twice. |
| ATableMoveFoundWhilePonderingIsSentAgainAfterTheHit | 1 | A K+R v K table move (depth 1) found while pondering is sent again after the hit. |
| PonderThenStopGivesABestmoveAtOnce (one assertion added) | 1, L4, R4 | A miss sends nothing again. |

Mutations: 12 of the new lines, 11 caught. The one that lived noted the hit only after releasing the
search. That's a race the UCI thread nearly always wins, so no test can force it. The code now does both
under one lock, so their order no longer matters. Full suite: 484 passed, 2 skipped. `bot/test-ponder-note.py`
passes (run on a copy of lichess-bot).

**Confirming it in the bot**, after the next restart once this is merged (start-bot.cmd builds the engine
from the main checkout):
- `python scripts/ponder-scope/ponderlog.py bot/lichess-bot/lichess_bot_auto_logs/lichess-bot.log.<date>`
  should say "hits with no info after ponderhit: 0 of N" (619 of 1,364 on 10-20). Its new line, "hits whose
  only info after ponderhit is the last one sent again (P1)", should be about 45%.
- Simpler: the bot's INFO log, `bot/logs/bot-<date>.log`, should have no more "no depth finished after the
  hit to report" (183 on 10-20). The ponder-note patch writes that only when a hit has no score.
- `ponderlog2.py` doesn't count the line sent again as a depth finished (`new_infos_after_hit` in
  ponderlog.py), so its three rows keep their meaning on logs from after P1.

## Scripts and data

`scripts/ponder-scope/` (plain Python. `extract.py` needs python-chess: use the bot venv's
python):

- `ponderlog.py`, `ponderlog2.py`, `ponderlog3.py`, `clocks.py <log>...`: the bot's DEBUG log
  (`bot/lichess-bot/lichess_bot_auto_logs/lichess-bot.log.<date>`). For each search: its kind,
  its `info` lines, how it ended. The tables in "What a hit does in the bot" and "What's lost"
  come from these.
- `extract.py <log> samples.json 12`: the sample positions (`samples.json`, kept).
- `uci.py`, `experiment.py hits|misses|trace <first> <last> <out.jsonl> [ponder threads]`: the
  lab runs. They use the Release build under `src/ChessBruteforcer.Engine/bin` and
  `bot/eval/h1024-3m.nnue`. In a worktree, set `PONDER_SCOPE_NET` to the main checkout's copy.
- `summarise.py <hits.jsonl> <misses.jsonl>...`, `simulate.py <trace.jsonl>`: the tables.
- `results/`: the runs behind this page (hits, misses, misses with an 8-thread ponder,
  traces).
