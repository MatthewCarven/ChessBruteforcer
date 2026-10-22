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
| `ponderhit` | `Search.PonderHit` (Search.cs:166): the budget now applies, counted from `go ponder`. A depth begun past half the planned time is stopped at once, and the answer is the last finished depth. Otherwise the depth goes on, as it would in a plain search. (Since P2, below: such a depth may finish within the planned time of the hit, and nothing runs past it.) |
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

### P2: a hit may finish the depth it would drop (2026-10-21)

**Verdict: built, measured, and off. It does what the replay said (a ply more on most such hits) but doesn't win
games.** At 10+0.1, both sides pondering, against both rules off:

| P2 version | games | Elo [95%] | SPRT [0, 10] |
|---|---:|---|---|
| both rules (finish + cap) | 360 | **-41 [-65, -17]** | H0 accepted |
| cap only | 575 | **-22 [-41, -3]** | H0 accepted |
| finish only | 614 | -7 [-24, +11] | undecided; stopped at a fixed count |

So both options default to off: merged as it stands, P2 changes nothing in the bot. The code and the A/B match
mode stay, to measure again after P4 (below).

A side session, branch `claude/p2-finish-depth-246981`, on compression with P1 (7cb0adb). **At `ponderhit`, a
depth begun past half the planned time S is no longer stopped. It has until the hit + S to finish, and no depth
starts after it. And whatever else runs on after a hit stops by the hit + S, not at the hard limit H (3S from `go
ponder`).** These are the replay's "finish" and "capped" rules. A hit never spends more than S of real clock, which
is the move's own planned time: no more than a plain search on our clock would plan to spend. Behind two UCI
options, `PonderFinish` and `PonderCap` (checks; both off by default since the matches below, where neither rule won
games). Both off, a hit does exactly what it did before.

The rule is `Search.AfterPonderHit`, which `PonderHit` calls. For a hit at h (on the search's clock, from `go
ponder`), with the depth in progress begun at b:

| case | both off (as before P2) | `PonderFinish` ("finish") | `PonderCap` ("cap") |
|---|---|---|---|
| b > S/2: a depth the clock alone would never have begun | stopped at once; the depth before is played | runs to h + S; played if it finishes | as off |
| h ≥ H: pondering ran past the whole budget | stopped at once (by the clock check) | the depth in progress runs to h + S | as off |
| otherwise: the depth goes on, as in a plain search | runs to H; new depths until S/2 | as off | the same, but stops by h + S if that comes before H |
| `movetime` (no S) | the hard limit | the same | the same |

**The lab** (`experiment.py p2`, `p2.py`; `results/p2.jsonl`). The replay's 12 hit positions, one thread, each from
the table its trace started from. Hits at 0.75 to 2 S (S = 4.83 s, as the bot's median), PonderFinish on and off in
turn on each position:

| hit at | off: depth played (median) | on: depth played | on deeper than off | the replay predicted | positions where the replay was right | real clock after the hit, on: median | mean | most | off: mean |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 0.75 S | 14 | 15 | **6/12** | 5/12 | 11/12 | 1.9 s | 2.2 s | 4.83 s | 0.4 s |
| 1.0 S | 14.5 | 15.5 | **9/12** | 9/12 | 12/12 | 2.9 s | 2.4 s | 4.83 s | 0.3 s |
| 1.5 S | 14.5 | 15.5 | **8/12** | 7/12 | 11/12 | 1.7 s | 2.2 s | 4.83 s | 0.05 s |
| 2.0 S | 15.5 | 16 | **8/12** | 8/12 | 12/12 | 2.6 s | 2.9 s | 4.83 s | 0 |

- **The build does what the replay said**: a ply more on 6-9 of 12 hits, for about 2-3 s of real clock each. The
  replay was right about which positions gain the ply in 46 of 48. A hit spent S for no new depth on 1-3 of 12
  (the depth didn't finish in time). The move changed on 2-4 of 12.
- **Never more than S after the hit**: at most 4,830 ms, S to the millisecond. Off, a depth begun before S/2 ran
  4.2 s past a hit at 0.75 S, and could have run to H.
- **The option changes only when the search stops.** Every one of the 1,383 depths searched (both arms, all
  positions) had exactly the node count of the trace's same depth. The traces were made by the build before P1, so
  the tree is also node for node what it was.

In the bot, hits come at a median 0.75 S (p75 1.0 S), and 37% of them are the kind P2 changes ("stopped at once").
So expect a ply more on roughly half to three quarters of those, about 2-3 s each.

**The A/B match: P2 loses.** `ponder-match.py --arms "PonderFinish=true;PonderFinish=false" --sprt 0,10`, 10+0.1,
both sides pondering, the bot's network (h1024-3m), one thread and one physical core each, 3 games at a time, the
bot not running. The SPRT accepted H0 at game 358 (LLR -3.07):

| A (P2 on) v B (off), 360 games | A | B |
|---|---:|---:|
| won / lost / drawn, A's point of view | +61 -103 =196 | |
| score, Elo [95%] | 44.2%, **-41 [-65, -17]** | |
| ponder hit rate | 68.9% | 68.5% |
| clock a move: all / after a hit / otherwise | 191 / 167 / 243 ms | 178 / 139 / 262 ms |
| clock spent in all, after hits / otherwise | 3,026 s / 1,989 s | 2,510 s / 2,174 s |
| clock left at the end of the game | 3.37 s (34%) | 4.30 s (43%) |

A scored 46% in games under 100 plies, 46% at 100-150, 40% at 150-200 and 46% past 200: a steady loss, not one
phase of the game. As white 51%, as black 37%.

**Why, most likely: P2 moves clock from the misses to the hits.** It spent 21% more after hits (3,026 s against
2,510) and so had less for the rest (9% less after misses, 1,989 s against 2,174). After a hit the search is already
as deep as a whole budget would take it, and the extra time buys a ply on an already deep search. After a miss the
search starts cold, so time there is worth more. Spending the bank on hits starves the moves that need it. (The lab
saw only the hits.)

Two things this match can't separate. P2 is two rules behind one switch: "finish" spends more on late hits, and
the cap spends less on early ones. In self-play about 69% of replies come right after the opponent's own hit, so
early, where the cap applies. And at 10+0.1 a move gets about 0.2 s on one thread, far shallower than the bot's
4.8 s on eight threads (depth 15); deeper, a ply may be worth more against the clock it costs. Self-play also
overstates hits (69% against the bot's 56.5%).

**The split** (Matthew's choice after that). `PonderCap` became an option of its own (check), so
`PonderFinish=false,PonderCap=false` is the rule before P2, and each half can play against it. Same conditions as
above.

| A v B (both off), 10+0.1 | games | +W -L =D | Elo [95%] | SPRT [0, 10] | A's clock a move after a hit / otherwise | clock left at the end, A / B |
|---|---:|---|---|---|---|---|
| both rules (P2 as first measured) | 360 | +61 -103 =196 | **-41 [-65, -17]** | H0 at 358 | 167 / 243 ms (B 139 / 262) | 34% / 43% |
| cap only | 575 | +106 -142 =327 | **-22 [-41, -3]** | H0 at 571 | 130 / 267 ms (B 150 / 253) | 46% / 40% |
| finish only | 614 | +117 -129 =368 | -7 [-24, +11] | undecided (LLR -1.50); stopped at 600 | 188 / 226 ms (B 139 / 259) | 28% / 44% |

The cap alone loses too, though it spends *less* after hits and leaves more clock for the misses and the end. So
the "time after a miss is worth more" reading can't be the whole story. What the cap does is cut a depth that a
plain search would have finished, by 3S, and an unfinished depth is thrown away (Search.cs:244, P4's subject): the
time it ran buys nothing, and the move comes from the depth before. In self-play most hits come early (the
opponent's own hit makes it reply at once), which is where the cap bites.

Finish alone breaks even, near enough: a ply more on the late hits, paid for with 36% more clock after hits (5,627 s
against 4,143) and 13% less on the other moves (3,203 s against 3,697), and games ended with 28% of the clock left
against 44%. The SPRT would need thousands more games to tell -7 from 0 (about 11 hours here), so the match stopped
at a fixed count. Even its top end, +11, is below the +5 to +15 guessed in "Proposed changes" for the whole of P2.

**What next, for P2.** The matches skipped the planned slower control (30+0.3, "like 3+2"): telling a near-zero
effect from zero there would take about 1,500 games, some 11 hours, for a likely null. The one change that would
alter P2's sums is P4, which plays an unfinished depth's better move instead of throwing the depth away. Then the
cap no longer wastes the depth it cuts, and a late depth that misses S still counts for something. So run P2's
A/B match again on top of P4 (`--arms "PonderFinish=true,PonderCap=true;PonderFinish=false,PonderCap=false"`, and
each half). And for P3: the off arm still ended its games with 40-44% of the clock left at 10+0.1, so the bank P3
would spend is there. But P2 says spending more of it on the hits doesn't help; P3 spends it on every move,
misses included.

**Tests** (PonderTests 20 → 65):

| test | threads | what it checks |
|---|---|---|
| APonderHitSetsTheLimitsP2Describes | | 14 cases of the rule, on and off, at its edges (b at S/2 exactly, h at H exactly, a late depth hit between 2S and 3S, movetime) |
| APonderHitLetsTheDepthItWouldDropFinish | 1, L4, R4; 1 with the option switched off and on | Find a depth whose next depth is quick for its time, set S just under twice that depth's time, and hit as the next one begins. It finishes after the hit, within S, and its move is played; or, if it can't, the answer comes by S with the depth before, its line sent again (P1). At least one try in four finishes. |
| APonderHitAnswersWithinThePlannedTime | 1, L4, R4 | Hits at once, at 0.9 S and past H: answered within S (+200 ms). |
| AHitAtOnceStopsAsAPlainSearchWouldNotAtTheCap | 1, L4, R4 | No depth begins past S/2 after a hit, and the answer comes when a depth ends, well before the hit + S in at least one of six tries. |
| WithPonderFinishOffAHitDropsTheDepthBegunPastHalfThePlannedTime | 1, L4, R4 | Off: a hit as a late depth begins answers at once with the depth before. |
| PonderFinishLeavesTheSearchItselfAlone | 1 | On and off, the same depths, node for node, and the same move. |
| UciOffersPonderOffByDefault (extended) | | `option name PonderFinish type check default false`, and `PonderCap` the same; true and false accepted, anything else an error. |
| PonderFinishAndPonderCapReachTheSearch | | Each option, set or left at its default, reaches the search a `go` runs. |
| PonderHitAppliesBothOptions | | The hard limit a hit sets: the cap's hit + S, or H without it; finish's hit + S past H, or H without it. |

Changed: `APonderHitKeepsTheDepthPonderingReached` asserted an answer within 200 ms of a hit after a long ponder. Under
P2 that depth may finish first, so it now runs both ways: off within 200 ms, PonderFinish on within S (98 ms there)
+ 200 ms, and it says why. The second half of `PonderThenPonderhitAnswersWithinTheClock` allows S + 200 ms likewise.
`APonderHitAnsweredAtOnceSendsItsLastInfoAgain` (P1's) runs with the option off, since its premise is a hit that
answers at once. The tests of P2's own rules turn the options on themselves.

Mutations: 26, all caught. Two lived at first, and the tests above catch them now: a late depth hit between 2S and
3S given only to H, and a hit that forgot the soft limit, which runs on to h + S on every hit. Then 23 more for the
split (all caught) and 4 for the defaults turned off (all caught). Full suite: 529 passed, 2 skipped.

### P3: plan more time a move when pondering (2026-10-21)

**Verdict: built, measured, and off. A quarter more planned time a move neither wins nor loses at 10+0.1: Elo -0
[-10, +9] over 2,000 games.** The option stays, at 0 by default, so merging changes nothing in the bot.

A side session, branch `claude/p3-ponder-time-254cad`, on compression with P2 (e92ac2e; first written on P1 alone,
7cb0adb, while P2's match ran, and rebased). **When the GUI sets the UCI option `Ponder`, every move plans
`PonderTimeBonus` percent more time: the soft limit S becomes S + S x bonus / 100.** Stockfish does this with a
quarter (`optimumTime += optimumTime / 4` when Ponder is on). python-chess sends `setoption name Ponder value true`
before every `play(..., ponder=True)` (chess/engine.py, `play`), so lichess-bot turns it on whenever it ponders, and
the bonus would reach the bot. Unlike P2, it spends the bank on every move, misses included, not only after hits.

- **The hard limit H keeps its rule**: three times the *plain* S, at most a third of the clock (plus the increment).
  Stockfish too works out its maximum before adding the ponder bonus. S never passes H. With 25%, H is 2.4 S'
  rather than 3 S, which still leaves room for a depth begun before S'/2 to finish.
- `PonderTimeBonus`: a spin, 0-100. **Default 0** since the match below (it was 25 while measured). With Ponder off
  it counts for nothing. `movetime` and `infinite` searches are unchanged. The Ponder option, ignored until now, is
  read: true or false, anything else an error.
- Where: `Search.TimeBudget` (now internal, for tests) and `SearchLimits.PonderTimeBonusPercent`, which `UciEngine`
  fills on every `go` from the two options. P2's options (PonderFinish, PonderCap) are separate and stay off.

What 25% means for a move (Move Overhead as in each setting):

| clock | S plain | S + 25% | H (both) |
|---|---:|---:|---:|
| 10+0.1, 10 s left (the A/B match) | 406 ms | 507 ms | 1,218 ms |
| 3+2, 3 min left (the bot, Move Overhead 100) | 7,496 ms | 9,370 ms | 22,488 ms |

**The A/B match.** `ponder-match.py --arms "PonderTimeBonus=25;PonderTimeBonus=0" --sprt 0,10`, 10+0.1, both sides
pondering (so both have Ponder on, and P2's rules off), the bot's network (h1024-3m), one thread and one physical
core each, 3 games at a time, the 300 openings in colour-reversed pairs, the bot not running. The SPRT never decided
(LLR -2.17 at the end), so the run went to its cap of 2,000 games (10:45-15:51):

| A (25%) v B (0), 2,000 games | A | B |
|---|---:|---:|
| won / lost / drawn, A's point of view | +408 -410 =1182 | |
| score, Elo [95%] | 50.0%, **-0 [-10, +9]** | |
| ponder hit rate | 69.3% | 69.2% |
| clock a move: all / after a hit / otherwise | 199 / 165 / 274 ms | 184 / 148 / 261 ms |
| clock spent in all, after hits / otherwise | 16,235 s / 12,329 s | 14,567 s / 11,755 s |
| clock left at the end of the game | 2.89 s (29%) | 4.01 s (40%) |
| games ended with under 1 s left | 158 | 45 |
| games lost on time | 0 | 0 |

The halves agree (games 1-1000 +6 [-8, +20], 1001-2000 -7 [-20, +7]), and so does every game length (100-150 plies
-2, 150-200 -6, past 200 +1; under 100 plies +10 [+1, +20], one bucket in four, likely chance).

**What the quarter bought.** 25% more planned time came out as 8.5% more clock spent (28,564 s against 26,322):
11% more after hits, 5% more otherwise. A search starts a new depth only before S/2, and a depth takes about twice
the last, so more S often changes nothing; and as the clock runs down, later budgets shrink. So most of the extra
went where P2 found it buys least, after hits. The bank shrank (40% → 29%), and 158 games against 45 ended with
under a second left, with no time losses.

What 8.5% more time should be worth, if it cost nothing: p2.1 measured twice the time at 10+0.1 as +87
[+68, +106] (MATCHES.md, "More search against less"), so 8.5% (0.12 of a doubling) is about +10. The match's top end
is +9. So the extra clock was worth at most what free time is worth, and likely less: it isn't free, since spending
it early leaves less for the moves after.

**Not run: the slower control** (30+0.3, "like 3+2"). As for P2: telling a few Elo from zero would take thousands of
games, about 11 hours. The bot's own setting is less promising than 10+0.1, not more: deeper searches gain less a
ply (p2.1: +165 for depth 7 over 6, +105 for 11 over 10), and on Lichess hits are rarer (56.5% against 69% here), so the bank
is smaller.

**For Matthew.** Off as merged. To try it in the bot anyway, add `"PonderTimeBonus": 25` to `engine["uci_options"]`
in bot/make-config.py and restart the bot. Worth measuring again after P4 (an unfinished depth's better move gets
played, so time that ends mid-depth buys something) and P6.

**Tests** (PonderTests 65 → 75):

| test | threads | what it checks |
|---|---|---|
| TheTimeBudgetPlansAQuarterMoreWhenPondering | | S and H at 3 s, 10+0.1, the bot's 3+2 and a nearly empty clock (where H caps S), with 0, 25 and 100%: S grows, H doesn't |
| ThePonderTimeBonusLeavesMovetimeAndMovesToGoAlone | | `movetime` and `infinite` unchanged; `movestogo` 10 gets the bonus on its own S |
| WithPonderOnEveryMovePlansTheBonus | | Through UCI: no bonus with Ponder off, none by default with it on; 25 and 100 on `go ponder` and on a plain `go` (a miss); none for `movetime` |
| UciOffersAPonderTimeBonusFrom0To100 | | `option name PonderTimeBonus type spin default 0 min 0 max 100`; 150 → 100, -5 → 0, 1 and 50 exact (a minute's clock, so 1% shows); a bad Ponder value is an error and changes nothing |
| WithPonderOnHitsStillAnswerWithinTheClock | 1, L4, R4 | With 25%: a hit at once within H (+300 ms), a hit past the budget at once (200 ms), and a plain `go` within H |

Mutations: 17 over the new lines while the default was 25, and 2 for the default turned to 0: all caught. Full suite:
541 passed, 2 skipped.

### P4: play the move a cut depth proved better (2026-10-21)

**Verdict: built, correct, and it almost never fires.** It changed 1 move in 1,044 lab searches of the bot's own
positions. Against P3's build at 10+0.1, no pondering: **+7 Elo [-3, +17] over 2,000 games** (SPRT [0, 10] undecided at
the cap, LOS 90%). That's no loss, and most likely no gain either: at that clock the lab saw it change no move at all.

A side session, branch `claude/p4-partial-depth-081e28`, on P3's branch (08a4118: compression with P1-P3). **When a
depth is cut off part way (the hard limit, a node limit, `stop`, or a ponder hit that stops it at once), the engine
threw the whole depth away and played the last finished depth's move. Now, if the cut depth had already searched the
last depth's best move to the end, and another root move then did better in a search that also ended, that other move
is played**, with the cut depth's score and line. It's the move the depth would have played had every root move it
didn't reach done worse. Engines commonly do this (Stockfish keeps a stopped iteration's best move the same way). There
is no option: it's always on.

What a cut plays:

| the depth in progress, when it was cut | before P4 | P4 |
|---|---|---|
| hadn't finished searching the last depth's best move | the last finished depth's move | the same |
| had searched it, and no other move had done better | the same | the same |
| a move had "failed high" (looked better on a quick test, `score > alpha` on a null window) and was being searched again in full | the same | the same: an unconfirmed fail-high never counts |
| a move's full search had ended above the best so far | the same | **that move** (the latest such) |

One thread searches the last depth's best first, always: the table's move at the root is the one the last depth
stored. On Lazy SMP the main thread's table move can be a helper's, since half the helpers run a depth ahead and store
the root too. Then the main thread may search that move before the last best, and a cut between the two keeps the last
best (row 1), however well the helper's move did.

**Where.** One thread and Lazy SMP's main thread (whose answer is the search's): at the root, each move's search that
ends notes whether it was the last best, and the score of a move that beat the best so far (`NoteRootMove`);
`ProvedAtCut` reads them. Ranges: each window's thread notes the same in its own window. A move that beats the best in
a window has beaten the last best too (which scored at most the window's floor, or exactly the best before it), so a
cut gathers from every window's thread, and from the calling thread if it was searching the depth again with the full
window, and plays the move with the highest score. Depth 1 is cut as before (it has no finished depth before it: its
best so far is played). `Search.RootTrace` is a test hook that reports each root move's end and each search again.

**The `info` line (the brief asked whether to send one): yes, when the cut depth changes the move, and only then.**

Kiwipete (a test position), the built-in evaluation, one thread, Hash 8, `go nodes 425984`, which cuts depth 7 just
after d5e6 has beaten depth 6's e2a6 there:

```
info depth 6 score cp 60 nodes 140429 nps 422978 time 332 hashfull 10 pv e2a6 e6d5 c3d5 f6d5 e5d3 d7d6
info depth 7 score cp 45 lowerbound nodes 425984 nps 930096 time 458 hashfull 32 pv d5e6 a6e2 c3e2 e7e6 d2f4 d7d5 e2d4
bestmove d5e6 ponder a6e2
```

(Before P4: the same depth 6 line, then `bestmove e2a6`. The new score, 45, is below depth 6's 60: a deeper depth
often scores every move differently, so the comparison is always within the cut depth, against the last best's
score there, never against the depth before.)

- python-chess keeps the last `info` line's score and line as the move's stats. lichess-bot logs them, and its
  draw-offer and resign rules read the score. Without this line they would be the last finished depth's, whose line
  starts with a different move from the one played. After a hit, P1 would even send that line again just before a
  `bestmove` naming another move.
- The ponder move comes from the line (`bestmove ... ponder <its second move>`), so the line must be the played move's.
- `lowerbound`: the score is exact for the move played, but for the depth it's only a lower bound, since root moves
  not yet searched might do better. That's what UCI's `lowerbound` says. It also lets the log scripts tell a cut
  depth from a finished one. python-chess reads it (`info["lowerbound"]`); lichess-bot and the match tool ignore it
  (the match tool's depth tally counts the line as the move's depth). Stockfish sends its stopped iteration's line
  without the flag.
- When the cut depth proved nothing, nothing extra goes out, as before (and after a hit, P1's line).

**Searches that aren't cut are unchanged.** 60 of the bot's positions to depth 11 on one thread, P3's build (08a4118)
against P4's: every depth's line the same (depth, score, nodes, line) and the same move, 148 M nodes in all. A fixed
depth is never cut, so this is all a search with no limit does.

**How often it fires (the lab, `p4.py`).** 297 of the bot's searches from 2026-10-19 and 10-20 (144 plain `go`,
153 ponder hits, every 24th and 28th), each from an empty table, with the bot's settings (Hash 256, Move Overhead 100,
h1024-3m, no tables). A plain `go` on its own clock; a hit as `go ponder`, `ponderhit` after the time it pondered in the
bot, then the answer:

| run | searches | a depth cut | of them: stopped at once at the hit (the depth had run, median) | ran on to the end of their time (median) | **the cut changed the move** |
|---|---:|---:|---:|---:|---:|
| 8 threads (the bot), plain `go` | 144 | 2 (1%) | | 2 (6.5 s) | **0** |
| 8 threads (the bot), ponder hits | 153 | 74 (48%) | 68 (1.2 s) | 6 (18.6 s) | **1**, one that ran on |
| 1 thread, the same hits | 153 | 83 (54%) | 82 (1.1 s) | 1 (12.0 s) | **0** |
| 1 thread, every position at 10+0.1 (the match's clock) | 297 | 2 (1%) | | 2 (0.6 s) | **0** |
| the same, the table warm: our own search two plies earlier first, as in a game | 297 | 2 (1%) | | 2 (1.0 s) | **0** |
| all | 1,044 | 163 | 150 | 13 | **1** |

(Not all of it on a quiet machine, by the other sessions' notes. The 8-thread run, 16:55-17:32, had no company
reported. The cold 10+0.1 run, 17:33-17:35, ran beside two other sessions' test suites. The one-thread hits, 17:35-17:53,
shared the CPUs with another session's builds and tests until 17:48. The warm 10+0.1 run, 21:06-21:09, ran beside a
match pinned to CPUs 2-12. A crowded search is a slower one, and reaches less deep in its time, but no run changed
more than one move, so the conclusion stands.)

So **1 changed move in 1,044 searches, from 163 cut depths.** The one: a hit at 4.7 s (S 5.2 s) whose depth 14, begun
at 1.9 s, ran 13.7 s to the hard limit, and played f4e3 (-7) where depth 13 had e1e2 (+17). A search four times as long
(depth 15, from an empty table) preferred a third move, f2g1, so even that one isn't clearly a gain.

**Why so rarely.** A cut depth can change the move only once the last best's search there has ended, and that first
move takes a large share of a depth: it's searched with the full window, the others mostly with a null one (on
Kiwipete's depths 3-8, from a quarter to three fifths of the depth's nodes). Most cuts come early in the depth. A hit
that stops at once drops a depth begun past half the planned time, which has run about half as long as the depth
before it (the scoping's 0.52), so about a quarter of its own way: 150 such cuts in the lab, none changed. P4 can only
fire on depths cut late, those that run on to the hard limit, and they're few: 13 of 1,044 in the lab, 1 changed. In
the bot they carry most of the clock ponderlog3.py counts (14% of plain searches' clock, 37% after hits; "What's
lost", item 2), but they buy a move very seldom. A plain search is rarely cut at all: a depth begun before half the
planned time S nearly always ends well before the hard limit 3S, since a depth takes about twice the one before. The
bot's log has a tail of over 1 s after the last finished depth in 5% of plain searches and 9% of hits, more than the
lab, so the bot may see a little more than the lab: still around a move in a few hundred, by the hits above.

**The match.** `scripts/build-match.py p4-v-p3-10+0.1 08a4118`: P4 (477d0f8) against P3's tip (08a4118), 10+0.1, no
pondering, h1024-3m on both sides, one thread and Hash 64 each, 5 at a time, the 300 openings in colour-reversed pairs,
our tables adjudicating, 17:53-21:05. The bot wasn't running; another session's fixed-depth lab held CPU 14 from 17:57.
Nothing crowded it: both sides searched 3,140-3,240 knps (median) in every quarter hour.
The SPRT never decided (LLR 0.63 at the cap; its high was 1.63 at game ~1,000):

| P4 v P3, 2,000 games | |
|---|---|
| won / lost / drawn, P4's point of view | +442 -404 =1154 |
| score, Elo [95%] | 50.9%, **+7 [-3, +17]**, LOS 90% |
| first 1,000 games / last 1,000 | +14 [0, +27] / -0 [-14, +14] |
| depth and speed in play | 10.9, 3,188 knps / 10.9, 3,185 knps |
| games lost on time | 0 |

Ends: 698 repetitions, 540 by the tables, 401 table draws, 306 checkmates, 54 by the 50-move rule, 1 stalemate.

**Reading it.** The two builds play the same move in every search P4 doesn't change (node for node, above), and at
this clock the lab saw P4 change none of 594 searches, with the table cold or warm. Even at one search in 200 (about
the most that zero in 594 allows), a game of ~65 moves a side gets about 0.3 changed moves. To move the score by 0.9%,
each would have to swing about 3% of a game, a lot for a move the cut depth rates a few centipawns better. So the +7 is
most likely noise around zero: the first half's +14 and the second's 0 fit that. The firm part is the bottom of the
interval: it costs at most about 3 Elo, and likely nothing. The guess in "Proposed changes" (+5 to +20) assumed a cut
depth often had a better move ready; it seldom does.

**What it means for the others.** P2's and P3's pages asked for their A/B matches again after P4, on the idea that an
unfinished depth would no longer be wasted. It still is, nearly always: P4 rescues about 1 in 13 of the depths cut
late, and none of those cut early. So their sums don't change, and the matches aren't worth playing again for P4. (The
brief: "if it gains, also P2's ponder A/B". It didn't, so that wasn't run.)

**Confirming it in the bot**, if it's merged and the bot restarts on it: `ponderlog.py` prints "hits answered by a cut
depth's move, proved better there (P4)", and `ponderlog3.py` has a column for the clock on cut depths whose move was
played. By the lab (1 of 153 hits on 8 threads), expect roughly ten a day: the bot had 1,364 hits on 2026-10-20.

**For Matthew (decision P4).** P4 has no option: merged, it is on in the bot.

- **(a)** Merge it as it is. It's what engines do, it's correct by the tests, and the match says it costs nothing. The
  bot's logs then say how often it fires. *My pick.*
- **(b)** Merge it behind a UCI option, off by default, as P2 and P3 are: no change in the bot until you turn it on.
- **(c)** Leave it unmerged: an unfinished depth stays thrown away, as before.

**Matthew chose (a)** (2026-10-22). P4 has been in compression since 4ebe998 (merged with P3), so the bot plays with it
from its next start on compression.

One idea it turned up, not part of decision M: on the bot's 8 threads, half the helpers run a depth ahead and often
finish a depth the main thread hasn't. Their move is in the table, and the main thread's answer ignores it. Engines
commonly pick the answer from the thread that got deepest ("thread voting", Stockfish's best-thread choice). That
would use far more of the work a cut throws away than P4 can. It isn't measured here.

**Tests** (CutDepthTests, new, 17):

| test | threads | what it checks |
|---|---|---|
| OnOneThreadACutPlaysWhatTheDepthProvedAndNothingElse | 1 | Kiwipete, the start position and another to depth 8, then the same search cut by node limits at points through the depths where a move was searched again (after the last best's search, after each fail-high, after each move proved better and the two moves after it): what's played is what the rule says from the uncut search's trace; every finished depth and every root event before the cut is the uncut search's, node for node; a cut during a search again doesn't play that move |
| StoppedJustAfterAMoveIsProvedBetterItPlaysThatMove | 1, L4, R4 | Stopped from the trace the moment a thread whose answer counts proves a better move: that move is played, at the cut depth, with its line (Ranges: the highest score any window's thread proved) |
| StoppedAsAFailHighIsSearchedAgainItKeepsWhatWasProved | 1, L4, R4 | Stopped as a fail-high is about to be searched again: that move isn't played, and what was proved before is |
| ARangesDepthCutInItsFallbackCountsWhatEveryThreadProved | R4 | Each window's thread stopped at its first move, so the calling thread searches the depth again with the full window; stopped there, as it proves a move or at its first move: the calling thread's proof counts, with the windows' |
| ADepthThatHasNotSearchedTheLastBestMoveYetKeepsIt | 1 | Another move put in the table at the root: stopped after it, the last best is kept; stopped after the last best too, which did worse, the other move is played |
| ARangesThreadThatHasNotSearchedTheLastBestMoveYetProvesNothing | R4, in the windows and in the fallback | The same, on Ranges' threads |
| ACutInTheFirstDepthPlaysTheBestMoveItHasFound | 1 | Depth 1 as before P4 |
| UncutSearchesAreTheSameWithTheRootTraced | 1 | The hook changes nothing, node for node |
| TheCutDepthsLineNamesTheMovePlayedAsALowerBound | 1 | Through UCI: `info depth 7 score cp ... lowerbound ... pv <move>` and `bestmove <move> ponder <its reply>`; no other line has `lowerbound` |

Changed: three ponder tests (`APonderHitAnsweredAtOnceSendsItsLastInfoAgain`,
`WithPonderFinishOffAHitDropsTheDepthBegunPastHalfThePlannedTime`, `APonderHitLetsTheDepthItWouldDropFinish`) expected
P1's line sent again (or a finished depth) after a hit. A depth the hit stops may now send its own line instead, if it
proved a better move, so they accept that too (`AssertCutDepthAnswered`).

Mutations: 32 over the new lines. The first battery caught 26. Five of the six that lived showed gaps in the tests,
filled since (the score kept for every move, not just the best; a cut in depth 1; Ranges threads that search another
move first, in the windows and in the fallback), and one meant a change: Ranges now resets every thread's notes at each
depth, those sitting the depth out too, instead of counting only the first `windows`. The second battery caught 31 of
32. The one that lives plays the last Ranges thread's move instead of the highest score's; the windows run in score
order, so the two agree unless two windows contradict each other. (It was "caught" once, by a timing test that then
passed it three times running.) Full suite: 558 passed, 2 skipped.

### P5: keep the move-ordering history between moves (2026-10-22)

**Verdict: no gain. Keeping the history took 4-7% more nodes to the same depth, not fewer.** So it is off by default
(`HistoryKept` 0, every search starts it from nothing as before), and merging changes nothing in the bot. No match was
played: the brief was to stop at the bench if it saved under 3%.

A side session, branch `claude/p5-keep-history-7a2c68`, on compression (5fc8a61: P1-P4 in). The search tries the most
promising moves first, because a good move found early lets it skip the rest of a position's moves (a cutoff). For
quiet moves (not captures or promotions) its guide is the **history**: a tally, by side and by from and to square, of
how often a move caused a cutoff, weighted by depth². Every search cleared it (`Search.Prepare`), the ponder after a
hit's move and the search after a miss included, while the transposition table carries over. P5 lets a new search keep
a share of it instead: `HistoryKept` percent of each entry, on every thread (each helper has its own). `ucinewgame`
clears it. The killers (the last two moves that cut off at each ply) are still cleared: they belong to a ply of the old
tree.

**The bench** (`scripts/ponder-scope/p5.py`): 40 runs of 8 searches from the bot's games of 2026-10-19 and 10-20,
picked evenly from the 1,041 such runs in 142 games. Each run is 8 searches one game's engine ran one after another,
as it met them: a search two plies on from the one before (our move and the reply: a hit, or our own move), or, after a
miss, the search of the move really played, beside the ponder on the wrong one. Every position has 10 pieces or more,
and none was a table move. Each run starts with `ucinewgame`, then every search goes to **depth 12 on one thread**
(median 3.4 M nodes, 1.06 s), with the bot's Hash 256 and h1024-3m and no endgame tables. The table carries over
between searches in every arm, so the history is the only difference. On one thread a search to a fixed depth is the
same node for node every time, so each arm's node count differs from the cleared arm's only because of the history.

| history kept | searches (after a run's first) | nodes against cleared: geometric mean (95% interval) | median | middle half | searches with fewer nodes | total nodes against cleared | a different move |
|---|---:|---|---:|---|---:|---:|---:|
| 25% | 280 | 1.038 (0.965-1.130) | 1.030 | 0.74-1.37 | 46% | 1.042 | 24% |
| 50% (P5 as proposed) | 280 | 1.073 (0.994-1.170) | 1.035 | 0.78-1.37 | 47% | 1.095 | 24% |
| 100% | 280 | 1.062 (0.990-1.154) | 1.046 | 0.78-1.42 | 45% | 1.106 | 23% |

By what came just before the search, 50% kept:

| search | searches | nodes against cleared (95% interval) |
|---|---:|---|
| two plies on from the last (a hit, or our own move) | 206 | 1.080 (0.974-1.205) |
| after a miss, beside the ponder | 74 | 1.056 (0.927-1.215) |
| a run's 2nd search | 40 | 1.073 (0.946-1.227) |
| 3rd-4th | 80 | 1.064 (0.936-1.227) |
| 5th on | 160 | 1.078 (0.957-1.230) |

The only figure under 1 anywhere is 25% kept after a miss, 0.949 (0.828-1.102) on 74 searches, well inside the noise.
The interval is from resampling whole runs, since a run's searches share a game. One search at a time, the counts swing
widely both ways (the middle half from about 0.75x to 1.4x), because any change in order sends a fixed-depth search
down a different tree: one search in four ends on a different move (whether better or worse isn't measured here).
Only the average over 280 searches says anything, and it says "no fewer nodes", whichever share is
kept.

Checks: every run's first search (nothing to carry yet) matched the cleared arm node for node, 40 of 40, in every arm.
The build before P5 (5fc8a61) matched the cleared arm at every depth of all 320 searches.

Why it doesn't help is not measured. A guess: this history only grows (depth² for each cutoff, nothing taken off for a
move that was tried and failed), and move ordering caps it at 700,000. So kept tallies pile up evidence from earlier
positions on top of the new one's, and many moves sit at the cap together, where the tally can't order them. Stockfish
does keep its history between moves, but its updates have a penalty for moves that failed and pull every entry back
toward a bounded range ("history gravity"), so old evidence fades by itself. That would be a change to how the history
is updated, not to pondering. It isn't proposed here. Two other limits: the bot searches to depth 14-16 on 8 threads,
not 12 on one, and shifting the killers two plies (the brief's other idea) wasn't tried.

**What's left in the code**: the UCI option `HistoryKept` (0-100, default 0) and `Search.HistoryKept`, which keep that
share at each new search, every helper's too. `Search.ClearHistory()` forgets it: `ucinewgame`, `SearchPlayer.NewGame`
and `bench-threads` (before each search, as it clears the table) call it. With the default 0, every search is node for
node as before. The option is there so the bench can be run again on any later change to the history.

**For Matthew (decision P5).**

- **(a)** Merge as it is: the option, off. Nothing changes in the bot, as for P2 and P3, and the bench stays runnable.
  *My pick.*
- **(b)** Drop P5's code: merge only the write-up and the bench (the docs and `scripts/` from the branch's last commit)
  and leave `src` as it was. Equally fine: with the option off, the code does nothing.

**Tests** (KeptHistoryTests, new, 10):

| test | what it checks |
|---|---|
| ANewSearchKeepsItsShareOfTheHistoryOnEveryThread (0, 25, 50, 100) | Every thread's history (the search's and two helpers'), after a search that adds nothing to it, is the old one times the share kept, rounded down, entry for entry |
| UcinewgameForgetsTheHistoryOnEveryThread | After searches on 3 threads and then 1, all three histories are empty after `ucinewgame` |
| AFirstSearchIsTheSameNodeForNodeAsOneThatKeepsNothing (25, 50, 100) | With nothing before it, a search's every depth (score, nodes, line) is the cleared engine's |
| AfterUcinewgameASearchIsAFreshEnginesNodeForNode | A search after another search and `ucinewgame` is a fresh engine's, line for line; after a new table alone (Hash), with the history kept, it isn't |
| TheHistoryKeptOptionReachesTheSearch | `uci` lists the option; 25, 0, 150 (taken as 100) and -5 (taken as 0) reach the search |

Mutations: 6, all caught (`ucinewgame` keeping the history; `ClearHistory` or the aging skipping the helpers; the search
clearing its own as before; the option never reaching the search; halving whatever the share). Full suite: **601 passed,
2 skipped**. Two older tests failed once each in four full runs on this busy laptop, neither one touching the history:
`PonderTests.AHitAtOnceStopsAsAPlainSearchWouldNotAtTheCap` (one thread, a wall-clock test: it failed 2 of 2 on the
build before P5 here too) and `CutDepthTests.StoppedAsAFailHighIsSearchedAgainItKeepsWhatWasProved` (Ranges on 4
threads, a fresh search each time: it passed 8 of 8 alone, on both builds).

### P6: a table that keeps its deep entries (2026-10-22)

**Verdict: at the bot's Hash, a ponder miss no longer costs the next search extra work.** After an 8-thread ponder on
the wrong reply, the search of the position really reached does **1.03, 0.87 and 0.91x** the work it does with no
ponder (misses of 0.75, 1.5 and 3x the move's planned time S), against **1.23, 1.22 and 1.16x** on the old table the
same morning (1.08, 1.24 and 1.33x the day before, "Misses" above). A search with no ponder before it is unchanged
(0.99x the nodes), and the speed per node too (0.993x, within noise). In a match with no pondering it is neither
better nor worse: **-3 Elo [-13, +7] over 2,000 games** at 10+0.1 (SPRT [0, 10] accepted "not 10 better").

A side session, branch `claude/p6-tt-buckets-f4c01d`, on compression e1868ae (P1-P5 in).

**The change** (`TranspositionTable`). The table is the search's memory of positions it has already searched: for each,
the best move found, a score and how deep the search under it went. Before P6, each position had exactly one place
(slot) it could go, and a new position always took that slot from whatever was there. So an 8-thread ponder on the
wrong reply, filling a quarter to half of the table with shallow entries, threw out the deep entries our own search
had just left for the position the game then reached. Now:

- **Buckets of four.** A position may go in any of four slots (one bucket, 64 bytes: one cache line, so reading all
  four costs about what one did).
- **An age.** Each entry carries the number of the search that stored it (6 bits that were spare). Every `go` and
  every `go ponder` is a new search; a ponderhit carries on in the ponder's own.
- **Who is replaced.** A position already in its bucket is updated in place, as before. Otherwise the new entry
  takes the slot worth least: an empty one, else the one with the lowest *depth less 2 for each search since its
  own*. A ponder's flood is mostly entries of depth 1-2, so our own search's entries of depth 4 and more outlast it,
  and an entry nothing uses again falls behind any new one within a few moves. An old entry the current search finds
  and uses counts as new again.
- Unchanged: the lockless check on every slot (threads share the table with no locks; a slot two threads wrote at
  once reads as a miss). `hashfull` now counts only the current search's entries, so it reads lower than before and
  starts again each move.
- **Hash means megabytes** (Matthew's (a), 2026-10-22). The table used to be sized at 24 bytes a 16-byte slot, only
  so that a given Hash kept the same search as before the table went lockless. P6 changes the search anyway, so Hash
  256 is now 256 MB and 16.8 M slots (was ~171 MB and 11.2 M). **The bot's engine commits 85 MB more.**

**The lab** (`scripts/ponder-scope/p6.py`, `results/p6/`): the 8 positions of the "Misses" lab, the ponder on 8
threads, as before. Three arms rotated position by position (07:54-08:14, the bot off, nothing else running): the old
build (e1868ae, Hash 256), P6 at Hash 256 (what the bot gets), and P6 at Hash 171, the old table's 11.2 M slots: the
replacement rule alone. Each arm's search at B goes to its own depth D (where a search from scratch gets in 3 s, median
13), so each row is against the same arm's search with no ponder:

| search at B, to depth D: nodes against no ponder (geometric mean, 8 positions) | old, 10-21 ("Misses") | old, today | **P6, Hash 256** | P6, Hash 171 |
|---|---:|---:|---:|---:|
| from scratch (`ucinewgame` first) | 1.18 | 1.19 | 1.22 | 1.22 |
| after our own search, no ponder | 1.00 | 1.00 | 1.00 | 1.00 |
| ... then 0.75 S pondering the wrong reply | 1.08 | 1.23 | **1.03** | 0.90 |
| ... 1.5 S | 1.24 | 1.22 | **0.87** | 0.95 |
| ... 3 S | 1.33 | 1.16 | **0.91** | 1.15 |

The old build's two days differ by up to 0.15 at the same row, so read each figure as give or take that much (8
positions, and an 8-thread ponder runs differently every time). On the 5 positions where both builds searched to the
same D, build against build:

| search at B, to depth D | P6 (Hash 256) / old: nodes (geometric mean) | range | P6 (Hash 171) / old | range |
|---|---:|---|---:|---|
| after our own search, no ponder | 0.99 | 0.69-1.44 | 0.99 | 0.69-1.44 |
| after a 0.75 S miss | 1.04 | 0.70-1.60 | 1.00 | 0.74-1.45 |
| after a 1.5 S miss | **0.73** | 0.47-0.96 | 0.80 | 0.73-0.95 |
| after a 3 S miss | **0.86** | 0.63-1.18 | 1.18 | 0.76-2.24 |

In the bot a miss ponders a median 0.8 S (p75 1.5 S), where the rule alone (Hash 171) already removes the cost; after
the longest ponders (3 S) it takes the larger table as well. The move played at B was the same as with no ponder in 8,
8 and 8 of 8 (P6, Hash 256). Time to depth D-4 after a 3 S miss: 35 ms (P6) against 115 ms (old), where with no ponder
both take 37. (`hashfull` at the stop isn't comparable across the builds: P6 counts only the ponder's own entries.)

**Speed** (`bench 9`, h1024-3m, one thread, 8 rounds alternating the builds, the machine otherwise idle):

| | nodes to depth 9 (6 positions) | knps, median (clock) | knps, median (CPU time) | time to depth |
|---|---:|---:|---:|---:|
| old (e1868ae) | 3,775,600 | 2,728 | 2,584 | 1.38 s |
| P6 | 3,731,271 (-1.2%) | 2,718 | 2,554 | 1.37 s |
| P6 / old, round by round (median) | | 0.993 (2 of 8 rounds faster) | 0.988 | 0.996 |

A variant reading the bucket without bounds checks gave 0.995 (CPU 0.999), no better within the noise, so it wasn't
kept.

**The match** (`scripts/build-match.py`, MATCHES.md): P6 (6b60ff0) against e1868ae, 10+0.1, one thread and Hash 64
each, no pondering, 5 games at a time, 12:12-15:30 with the bot off.

| games | +W -L =D | score | Elo [95%] | SPRT [0, 10] | depth (mean) | knps | time losses |
|---:|---|---:|---|---|---:|---:|---:|
| 2,000 | +415 -430 =1155 | 49.6% | **-3 [-13, +7]** | H0 accepted at game 2,000 (LLR -2.98) | 11.2 against 11.1 | 3,308 against 3,310 | 0 |

The halves were about -5 and -1. This is the match the brief asked for, and it shows that P6 costs nothing in plain
play. It can't show P6's gain: at this clock on one thread a move's search stores a few hundred thousand entries and
nothing ponders, so the table fills only slowly over a game, from our own searches, and never takes the flood P6 is
for. That flood (an 8-thread ponder on the wrong reply, filling up to half the table) is the bot's setting, and a
match in that setting isn't possible here: two engines pondering on 8 threads each want 32 threads of a 16-thread
laptop for every game. The lab above is the measure of that.

**Decision P6: Matthew chose (a), merge it as it is (2026-10-22, 15:50).**

- **(a)** Merge as it is (always on). The bot loses nothing measurable in plain play, and its searches after a missed
  ponder stop paying 8-24% extra work. It costs 85 MB more memory at Hash 256 (your (a) earlier today). *My pick.*
- **(b)** Merge, and then watch the bot's own log for a few days: `scripts/ponder-scope/ponderlog*.py` gives the
  depth reached after misses, before and after.
- **(c)** Leave it out. Nothing is lost in play today; the ponder-miss cost stays.

**Tests** (TranspositionTableTests, new, 21; and three changed):

| test | what it checks |
|---|---|
| EveryEntryInABucketComesBackAsStored, AnEmptySlotIsNoEntryEvenForKeyZero | Four entries in one bucket read back field for field, at a search number past 0; an empty slot never matches, key 0 included |
| EverySlotHoldsAnEntry, HashIsMegabytesAtSixteenBytesASlot (4), BucketsStartOnACacheLine | 2,000 entries in 500 full buckets all read back; Hash N is N MB at 16 bytes a slot; bucket 0 starts on a 64-byte line |
| DeepEntriesOutlastAFloodOfShallowOnes | Our search's entries of depth 12, 4 and 2, then 500 depth-1 entries from the next search in the same bucket: the 12 and the 4 stay, the 2 goes |
| TheLastSearchsEntriesGoBeforeTheCurrentOnesOfTheSameDepth, AnOldEntryIsWorthItsDepthLessTwoForEachSearchSince (2) | Old entries go first; depth 7 three searches old (worth 1) goes before a current depth 2, depth 9 (worth 3) doesn't |
| AgesCountOnRoundTheWrap, AnEmptySlotIsTakenBeforeAnyEntry, TheSearchCountRunsRoundAtSixtyFourAndClearStartsItAgain | The 6-bit search number wraps from 63 to 0 and ages still count; an empty slot goes first even against an old shallow entry; `Clear` starts the count again |
| AnEntryInUseIsAsGoodAsNew | An old entry the current search finds counts as new |
| ThePositionAlreadyThereIsUpdatedInPlace (2) | In the bucket's first slot and a later one, the others untouched |
| HashfullCountsWhatTheCurrentSearchStoredOrUsed | 500 of the first 1,000 slots: 500; a new search: 0; one entry used again: 1 |
| EachSearchIsOneGenerationHelpersIncluded (1, 4 threads); PonderTests.GoAndGoPonderEachStartANewTableGenerationAndAHitGoesOnInItsOwn | One search moves the number on once, whatever its threads; `go` and `go ponder` each do, `ponderhit` doesn't, `ucinewgame` starts it again |
| ThreadTests.ThreadsWritingOneSlotNeverReadAMixedEntry (changed) | 4 threads writing 256 positions into 4 buckets, 4 reading (and writing back ages as the search number moves on): no mixed entry. It now runs until the readers have 10,000 hits (at least 400 ms, at most 30 s), not a flat 400 ms, which failed once under load in the merged suite ("only 0 hits") |
| CutDepthTests.OnOneThreadACutPlaysWhatTheDepthProvedAndNothingElse (changed) | P4's test needs a position where a cut plays a proved move; on P6's tree "Late" had none at depths 5-10, so it uses "Tactic" at 10 (4 s). Every node-for-node check passed on all of them |

Mutations (`scripts/ponder-scope/p6-mutate.py`): 27, **26 caught**. The one left only changes which of two slots of
equal worth goes (first or last), which nothing depends on. With the lockless check taken out, the thread test finds
mixed entries. A 28th, resetting the search number when the table is made, was dead code and is gone. Full suite:
**629 passed, 2 skipped.**

**What changes in the bot when it's merged**: the search itself (a different tree from the same position: the bench's
is 1.2% smaller), 85 MB more memory at Hash 256, and `hashfull` reading lower. No option: P6 is always on.

#### P6 in the bot's own misses (2026-10-22, 18:55-19:46; the ponder-scope session)

Matthew asked for a before-and-after check of the search after a missed ponder, from the bot's own log. The log
can't give one. On 10-20 (the old table), the search after a long miss needed 2.6-7.8x the nodes of one after a
short miss, but that's the position, not the table: the opponent thinks longest where the play is hard. Two windows
on the same table disagreed by 3x, and the share of the early depths didn't separate it either
(`scripts/ponder-scope/misscheck.py`, WORKLOG). So the bot's own misses were **replayed** instead
(`scripts/ponder-scope/replay.py`).

- 60 misses from the bot's games of 10-20, every 11th candidate, middlegames of 12-30 pieces
  (`replay-samples.json`).
- Each miss is replayed as the bot met it. First, our own search on the position before, one thread, to a fixed
  depth. Then a ponder on the reply we predicted, **on 8 threads for the ponder's real length** (median 4.7 s,
  capped at 15 s for 6 of them). Then `stop`, and the position the game reached, searched to a fixed depth D
  (median 13). The yardstick is the same arm with no ponder in between.
- The bot's network now (h1536-3m), Hash 256 as in the bot, no endgame tables. Three engines: the old table (the
  last build before P6, e1868ae); P6 (compression's 653254b); and P6 at Hash 171, the old table's slot count, which
  separates the new replacement rule from the 1.5x more entries P6 gets at the same Hash. The arm order rotated from
  miss to miss.

| the next search's work after the miss / with no ponder | geometric mean [95%] | median | misses where it cost >10% more |
|---|---|---:|---:|
| old table | 1.02 [0.87, 1.19] | 0.96 | 26/60 |
| **P6** (the bot's setting) | **0.75** [0.63, 0.88] | 0.85 | 13/60 |
| P6 at the old slot count | 0.85 [0.72, 0.98] | 0.95 | 20/60 |

| paired, on the same miss | geometric mean [95%] | P6 better on |
|---|---|---:|
| **P6 / old** | **0.74** [0.60, 0.90] | 42/60 |
| P6 at the old slot count / old | 0.83 [0.68, 1.01] | 36/60 |

| by the ponder's real length | old | P6 | P6 / old, paired [95%] |
|---|---:|---:|---|
| shortest third, 0.5-3.6 s | 0.90 | 0.65 | 0.72 [0.49, 1.06] |
| middle third, 3.7-6.7 s | 1.15 | 0.72 | 0.63 [0.40, 0.91] |
| longest third, 6.8-15 s | 1.00 | 0.89 | 0.89 [0.73, 1.10] |

- **On P6, the search after a miss needs about a quarter fewer nodes to the same depth than on the old table**:
  0.74x, paired on the same miss, better on 42 of 60. Speed per node is the same (bench 0.993x), so that's time.
- About two thirds of it is the replacement rule (0.83x at the old slot count), and the rest is P6's bigger table at
  the same Hash.
- On P6, a miss now *helps* the next search: 0.75x the work of not pondering at all. The wrong reply's tree shares
  positions with the real one, and P6 keeps both. On the old table the help and the evictions cancel (1.02x).
- On the bot's real misses the old table's own penalty is small (1.02x overall, 1.15x in the middle third). That's
  smaller than the lab's 1.08-1.33x, whose 8 positions sat at fixed fractions of the budget, on h1024-3m.
- Not measured: Elo. It applies only to the searches after misses, about 40% of the bot's ponder moves on Lichess.
  The searches of A and B here ran on one thread, where the bot uses eight, and to a fixed depth, not the bot's
  clock.

## Does the bot keep a plan? One game, move by move (61InZpXn, 2026-10-22)

A side session (branch `claude/game-plan-61inzpxn-4b4e4d`), from the bot's own logs only. Matthew's question: *"I want
to know if the bot has a long term ponder in mind"*: does it keep a plan from one move to the next, or decide again
from scratch each move? The game: [caiporaBot (2926) - matthewcarven (Black, 2548)](https://lichess.org/61InZpXn),
blitz 5+2, 20:12, the bot's first evening on h1536-3m with P6's table. A Sicilian (B40); book to move 6, then 85
engine moves. **Lost: mated at move 92** (Lichess's export has no computer analysis of its own).

**How an engine like ours "plans".** It has no plan stored anywhere. Every move it searches again, from depth 1 up.
What it carries forward is memory: the transposition table holds the positions the last searches looked at, each
with the best move found there and a score. The new search finds them again, so the early depths come nearly free.
The PV (principal variation) is the line it expects both sides to play, at the end of each search: its moves, the
replies it expects, its next moves. That's the nearest thing it has to a plan. So "does it keep a plan?" can be
measured: does the new move's PV carry on the old one, two plies (one move each) further along?

**Short answer: it keeps its plan for the next move, and keeps it well. Beyond that the "plan" is a forecast that
the opponent rarely lets come true. And in this game the plan was kept and was wrong.**

- When the opponent played the reply we expected (a ponder hit, 49 of 84 moves, 58%), we played the move our old
  line had next on 34 of the 43 where the line went that far (79%). The new search was handed the old line, score and
  all (on 34 of 49 hits its line at the old search's depth less 2 had *exactly* the old score), reached that depth in
  a median **78 ms**, and only had to look further. The first search of the game, on an empty table, took 2.7 s to
  depth 13.
- When the opponent played something else (a miss, 35 of 84), there was no plan for it: the search started over in
  effect (860 ms to the same depth). We still played the move planned for the other reply on 5 of 35.
- Further ahead, the PV is a forecast. The game followed our line for our own move only on 40 of 84 moves, for 3
  plies or more on 34, and never for more than 7 plies (3.5 moves), though the lines were a median 12 plies long.
- The plan wasn't right. While the game was in the balance (moves 8-48, our score from -0.17 to -3.06), **two thirds
  of the fall (-1.97 of -2.89) came on moves where the opponent had played exactly the reply we expected.** Each time
  we took the next step along our own line and looked two plies further, it was a little worse than we'd thought:
  4 such steps fell by 0.30 or more, none rose that much. No surprise refuted the line; it was overrated.
  **Stockfish agrees** (section 4): through moves 16-31 it had us about 0.25 worse than our engine did, so the falls
  on hits were our engine finding out, two plies at a time, what Stockfish already saw.

### 1. Plan continuity

| our move came after | moves | played the old line's next move | the old line went that far | played the table's move (the search's own line at the old depth less 2) | new line follows the old, shifted two: plies agreeing (mean) / could agree |
|---|---:|---:|---:|---:|---|
| the reply we expected (ponder hit) | 49 | **34 (79%)** | 43 | 39 (80%) | 3.2 / 8.9; all the way on 7 of 43 |
| another reply (ponder miss) | 35 | (the old line had no move for this reply; we played the one it had for the other on 5) | | | |

| what happened to the plan, move by move | moves | in the balance (our score -3.00 or better) |
|---|---:|---:|
| kept: the old line's (or the table's) next move | 37 | 18 |
| changed by the deeper search | 11 | 4 |
| already the table's move, not the old line's (the line printed and the table had parted) | 1 | 0 |
| they deviated; we played our planned move anyway | 5 | 3 |
| they deviated; a new move | 29 | 16 |
| the endgame tables (5 pieces or fewer) | 1 | 0 |

| plies of our line the game then followed | 1 (our move only) | 2 | 3 | 4 | 5 | 7 |
|---|---:|---:|---:|---:|---:|---:|
| moves | 40 | 10 | 20 | 5 | 5 | 4 |

Within a search, the move played was decided early on a hit (median depth 5 from which it stayed; it changed a mean
0.7 times from depth 6 on) and late on a miss (median depth 11; 1.6 changes). On a hit the search mostly confirms the
plan; on a miss it makes one.

### 2. The ponder in this game

| | moves | thought on the opponent's time (median) | depth reached then | depth played | own time after (median) | time to the old search's depth less 2 (median) |
|---|---:|---:|---:|---:|---:|---:|
| hits | 49 | 3.2 s | 16 | 17 | 1.7 s after `ponderhit` | 78 ms |
| misses | 35 | 5.2 s, on the reply not played | 16 | 15 | 4.7 s, a new search | 860 ms |

- Hit rate **58%** (49 of 84), as on 10-20 (56.5%).
- **On every hit the search carried on from the ponder's depth**: none of the 49 went back to a shallower depth. After
  `ponderhit` it added no depth on 14 (the move came from the ponder's last depth, which P1 sent again, so lichess-bot
  logged real stats for all 49), one depth on 24, two on 8, three on 1 and five on 2.
- On a miss the ponder's move for the reply we expected was the move we then played on 5 of 35; the rest was a
  search of its own (P6's table keeps what the wrong ponder learnt; this log can't measure that, see "P6 in the bot's
  own misses").

### 3. Where the plan changed, and why

In the balance (moves 8-48), 23 of 41 plan changes or non-plans:

- **The opponent deviated: 19** (16 with a new move, 3 with our planned move anyway). caiporaBot played the reply we
  expected only 22 times in 41 here. Each deviation meant a new plan, but rarely a jolt: our score moved 0.30 or more
  on 4 of 19 (it fell at 42...Bb7 after 42.Qb1, -0.46, and at 45...Rxd6 after 45.Rd6, -0.69, which traded into an
  ending we judged ever worse).
- **The deeper search changed it: 4** (23...Bd6, 26...Re7, 41...c5, 46...Qc6). Each was a close call settled the
  other way: where the new move took over, the score moved -0.05, -0.21, +0.10 and +0.02. **No tactic appeared** (none
  moved the score 0.50 at the switch); the one that did came at move 72, long lost.
- **The tables: none in the balance.** They took over only at 91...Kh4 (5 pieces, mated next move).

### 4. The evaluation's path: was the plan right?

Our score, from our side (Black), by stage:

Stockfish 16 (WSL, depth 18, one thread) scored every position of the game afterwards (`plan-sf.py`, 20:45-20:47,
with the bot off), our side's view like ours. Its "cost" of a move is its score after the move less its score
before; at a fixed depth that's give or take 0.2-0.3 a move, so read the totals, not single moves.

| moves | our score | Stockfish (mean) | ours (mean) | Stockfish's cost of our moves (its move played) | what happened |
|---|---|---:|---:|---:|---|
| 7-15 | -0.17 to -0.23 | -0.23 | -0.16 | 0.17 over 9 (7) | out of the book; 6 hits of 8, plan kept each time |
| 16-25 | -0.11 to +0.05 | **-0.30** (-0.51 by move 25) | -0.03 | 0.52 over 10 (3) | caiporaBot deviates at 7 of 10 moves; each a new plan, our score steady while Stockfish's sinks |
| 26-31 | +0.05 to **-1.01** | -0.72 | -0.48 | **1.13 over 6** (2) | the slide: 26...Re7 (hit, -0.21), four deviations by White's rooks and bishop (-0.04, -0.15, -0.15, -0.19), 31...Qd8 (hit, -0.32) |
| 32-44 | -0.33 to -1.04 | -0.77 | -0.70 | 1.46 over 13 (7) | the plan mostly kept (hits at 32, 33, 35, 37, 39, 41, 43, 44); 43...c4 cost 0.84 |
| 45-48 | -1.48 to -3.06 | -2.82 | -2.33 | 0.58 over 4 (4) | 45.Rd6 (a miss) Rxd6, then a queen trade (46...Qc6, a close call over the planned Qa8); each next step along our own line was worse (hits at 46-48: -0.57, -0.69, -0.32) |
| 49-92 | -3.27 to mated | -4.21 (moves 49-60) | -4.96 | | lost: the hits keep finding it worse (a further -5.95, capped at -10.00, against -0.99 on misses); here our score is the gloomier one |

| our move came after (in the balance, moves 8-48) | moves | score change, total | mean a move | fell 0.30+ | rose 0.30+ |
|---|---:|---:|---:|---:|---:|
| the reply we expected (hit) | 22 | **-1.97** | -0.09 | 4 | 0 |
| another reply (miss) | 19 | -0.92 | -0.05 | 2 | 2 |

If the evaluation were right on average, a hit's change would average about zero: the game went exactly where we
predicted, and the extra two plies would find it better as often as worse. Here they found it worse 4 times and
better never (by 0.30 or more). The line the bot kept playing towards was judged too kindly, and that kind of error
is the evaluation's, not the planning's. Stockfish says the same, in three parts:

- **Misjudged.** Through moves 16-31 Stockfish had us about 0.25 worse than our engine did (-0.30 against -0.03,
  then -0.72 against -0.48). The falls on hits were our engine finding out, two plies at a time, what Stockfish
  already saw. Closest to TURNS.md's "misjudged" (not tested its way, two plies deeper): Stockfish saw it first.
- **Some of it was our moves.** Stockfish's cost of our moves in the balance came to 3.91 over 41 moves; the dearest
  stretch was the slide itself (1.13 over moves 26-31, its own move played 2 times in 6). Both engines put the game
  past -1.00 for good at nearly the same move (Stockfish from move 44, ours from 45).
- **Keeping the plan didn't make the moves worse, or better.** Stockfish's cost a move after a hit (mostly the
  planned move) and after a miss (a fresh decision): 0.14 and 0.05 here, 0.09 and 0.31 in DpEHbqlQ below; over both
  games **0.12 and 0.13**. When the deeper search overruled the plan in the balance (5 times in the two games),
  Stockfish preferred the new move twice (41...c5, 46...Qc6), the plan's move twice (26...Re7 for the planned Rbd7,
  costing 0.60; DpEHbqlQ's 25...Rhf8 for the table's Raf8, costing 1.12, that game's dearest move in the balance), and
  neither once (23...Bd6). Too few to call.

### What would make it more plan-like

- **The plan is already kept, as well as an alpha-beta engine keeps one.** The table hands the next search the old
  line and its score (78 ms to the old depth on a hit). P5 tried carrying more (the move-ordering history) and found
  no gain.
- **The line it prints is often cut short.** 14 of 85 PVs had 1-3 plies (9 of them in the balance, all after misses:
  e.g. "9...Nc6" alone). A PV node that finds a table entry deep enough returns its score without the line under it
  (`Search.Negamax`, the table cutoff before null move), so the "plan" exists in the table but isn't shown.
  The ponder move doesn't suffer: it falls back to the table's move (`UciEngine.PonderMove`).
- **A more stubborn plan wouldn't have saved this game.** The plan was kept, 18 times in 22 hits in the balance, and
  it was wrong; Stockfish found kept plans and fresh decisions equally good (0.12 and 0.13 a move). What would have
  helped is a better judgement of where the line was going: the evaluation (NNUE.md), not the ponder.

**For Matthew.**

- **(a)** Show the whole plan: after each depth, extend the PV through the table's moves where the search cut it.
  Display only: the logs' "Pv:" and the chat show the full expected line; the move played doesn't change. Small. *My
  pick, if you want to read plans in the logs; otherwise (c).*
- **(b)** Stockfish's rule: no table cutoffs at PV nodes, so the line comes out whole from the search itself, and
  the main line gets searched rather than taken on trust. This changes play, so it needs a match.
- **(c)** Leave it. Nothing here says the bot loses by changing its mind; it lost by keeping to a plan its
  evaluation overrated.

### The night's last game, DpEHbqlQ: the same pattern

Matthew asked for the evening's last game the same way: [RustBot2003 (2754) - matthewcarven (Black,
2546)](https://lichess.org/DpEHbqlQ), blitz 5+3, 20:30. A French (Rubinstein, Fort Knox; C10), book to move 10,
then 48 engine moves. **Lost: mated at move 59.** It left the book already at **-1.02** (TURNS.md's "worse straight
out of the book"), and was in the balance only to move 28.

| | 61InZpXn (caiporaBot) | DpEHbqlQ (RustBot2003) |
|---|---:|---:|
| engine moves (with one before them) | 85 (84) | 48 (47) |
| ponder hit rate | 58% (49 of 84) | 55% (26 of 47) |
| on a hit, played the old line's next move | 34 of 43 (79%) | 19 of 21 (90%) |
| on a hit, played the table's move | 39 of 49 | 21 of 26 |
| on a hit, the line at the old depth less 2 had exactly the old score | 34 of 49 | 14 of 26 |
| new line follows the old, shifted two: plies agreeing / could agree (mean) | 3.2 / 8.9 | 4.7 / 9.6 |
| time to the old search's depth less 2: hit / miss (median) | 78 / 860 ms | 132 / 2,026 ms |
| the first search, on an empty table, to depth 13 | 2.7 s | 3.3 s |
| on a miss, played the move planned for the other reply | 5 of 35 | 3 of 21 |
| plies of our line the game then followed: 3+ / longest | 34 of 84 / 7 | 19 of 47 / 10 |
| hits that went back to a shallower depth after `ponderhit` | 0 of 49 | 0 of 26 |
| in the balance: moves, our score | 8-48, -0.17 to -3.06 | 12-28, -1.02 to -3.52 |
| in the balance, score change after a hit (moves; fell 0.30+ / rose 0.30+) | **-1.97** (22; 4 / 0) | **-1.86** (8; 3 / 0) |
| in the balance, score change after a miss | -0.92 (19; 2 / 2) | -0.64 (9; 1 / 0) |
| plan changed by the deeper search, in the balance | 4 (none a tactic) | 1 (25...Rhf8, -0.08 at the switch; not a tactic) |
| the endgame tables | the last move only | never (mated with 12 pieces) |
| Stockfish, in the balance: our score less its score (mean) | **+0.19** (ours kinder; +0.26 in moves 16-25) | -0.06 (the two agree) |
| Stockfish's cost of our moves in the balance: after a hit / after a miss (a move) | 0.14 / 0.05 (22 / 19 moves) | 0.09 / 0.31 (8 / 9) |
| our moves that were Stockfish's | 56 of 85 | 22 of 48 |
| -1.00 or worse for us from then on: Stockfish / ours | move 44 / 45 | move 17 / 17 |

The second game keeps its plan the same way, a little more strongly: through the table on 90% of hits, and most of
our score's fall in the balance came after hits (17...Bxe5 after 17.dxe5, -0.58; 28...Qe8 after 28.Bd2, -0.77). But
here **Stockfish agrees with our scores** on average (-1.10 at move 11, from the book; ours 0.06 the gloomier over
moves 12-28), so the fall was real, not a misjudgement: the book left us worse, and our own moves cost 2.72 by Stockfish's count over moves
23-28 (24...cxb2+ 0.67, 25...Rhf8 1.12, 26...Rab8 0.35, 27...Rb5 0.43). Those came while White carried out a plan we
didn't forecast: twice we expected 26.Rf3 and 27.Rf3, and White pushed its h-pawn instead, h5 then h6 (our score
-0.29, -0.74), on to h7 and h8. The lines we did predict came true for longer here (8-10 plies on 6 moves), as forced
sequences do. Closest to TURNS.md's "worse straight out of the book", then "out-calculated" (not tested its way,
two plies deeper).

Not measured: two games are two games, both lost to stronger bots. The tables above are their 133 engine moves, not a
sample of the bot's play; `plan.py` runs on any game in the DEBUG log (30 days kept) for more.

## Scripts and data

`scripts/ponder-scope/` (plain Python. `extract.py` needs python-chess: use the bot venv's
python):

- `ponderlog.py`, `ponderlog2.py`, `ponderlog3.py`, `clocks.py <log>...`: the bot's DEBUG log
  (`bot/lichess-bot/lichess_bot_auto_logs/lichess-bot.log.<date>`). For each search: its kind,
  its `info` lines, how it ended. The tables in "What a hit does in the bot" and "What's lost"
  come from these.
- `extract.py <log> samples.json 12`: the sample positions (`samples.json`, kept).
- `uci.py`, `experiment.py hits|misses|trace|p2 <first> <last> <out.jsonl> [ponder threads]`: the
  lab runs. They use the Release build under `src/ChessBruteforcer.Engine/bin` (or
  `PONDER_SCOPE_ENGINE`) and `bot/eval/h1024-3m.nnue`. In a worktree, set `PONDER_SCOPE_NET` to the
  main checkout's copy. `p2` replays the trace's positions from the same table (each position's own
  depth K from `results/trace.jsonl`) and hits at 0.75, 1, 1.5 and 2 S with `PonderFinish` on and off.
- `summarise.py <hits.jsonl> <misses.jsonl>...`, `simulate.py <trace.jsonl>`, `p2.py <p2.jsonl>`: the
  tables. `p2.py` sets the p2 run beside the replay of the same positions.
- `results/`: the runs behind this page (hits, misses, misses with an 8-thread ponder,
  traces, P2's hits).
- `scripts/ponder-match.py`: a match through python-chess, as lichess-bot plays. Plain: ponder on
  against off. `--arms "<A options>;<B options>"`: both sides ponder, differing by UCI options (P2's
  `PonderFinish=true;PonderFinish=false`). `--sprt 0,10` stops once the SPRT decides.
- `p4.py sample|run|summary|same` (P4): the bot's own searches (`results/p4-samples.json`, 297 of them) searched as it
  searched them, counting the cut depths and the moves they changed (`results/p4*.jsonl`); `same` checks two builds
  search a fixed depth node for node alike. The parsers (`uci.py`, `ponderlog.py`, `ponderlog3.py`) read the
  `lowerbound` line since P4, and don't count it, nor P1's line sent again, as a finished depth.
- `scripts/build-match.py <tag> <old commit>`: this checkout's engine against an earlier commit's, as the evaluation
  SPRTs were played (10+0.1, 5 at a time, SPRT [0, 10]); P4's match. Logs in `games/builds/` in the main checkout.
- `p5.py sample|bench|summary` (P5): runs of searches one game's engine ran in a row, from the bot's logs
  (`results/p5-samples.json`: 40 runs of 8), searched to a fixed depth on one thread by every arm at once (HistoryKept
  values, and `--old <exe>`, an earlier build that must match the cleared arm node for node); the summary compares each
  arm's nodes with the cleared arm's, search by search (`results/p5-d12.jsonl`: depth 12).
- `p6.py run <old exe> <new exe> <out dir>|summary <dir>` (P6): the misses lab with an 8-thread ponder, old build
  against new at Hash 256 and 171, rotated position by position, and its tables (`results/p6/`). `experiment.py` takes
  `PONDER_SCOPE_HASH` (default 256) for it. `p6-mutate.py`: the table's mutation battery (each mutant built and run
  against the 43 table, thread and UCI generation tests; the source restored after each).
- `misscheck.py <log>... [--since] [--until] [--label]` (P6 in the bot): misses from the bot's DEBUG log against the
  ponder's length. It shows that a log can't measure P6: the position swamps it. `replay.py <samples> <out> <old exe>
  <p6 exe> <net> [first] [last]`: the bot's own misses replayed on the old table, P6, and P6 at Hash 171
  (`replay-samples.json`: 60 misses from 10-20; `results/replay.jsonl`); `replaysum.py` makes its tables. The
  engines must sit on a short path (Windows' 260-character limit: copy them to e.g. %TEMP%\cbr\old and \new).
- `plan.py <DEBUG log> <game id> [--json out]` ("Does the bot keep a plan?"): one game's searches from the DEBUG log,
  each move's PV beside the one before, the ponder, where the plan changed and why, and the score's path by hit and
  miss. Reads the log only; needs python-chess (the bot venv's python, `-I`). `results/plan/<id>.txt` and `.json` are
  its output. `plan-sf.py run|summary <plan json> <out.jsonl> [--depth 18]`: Stockfish 16 (WSL, one thread, nice 19)
  on every position of the game, beside our own scores; run it only with the bot off.
