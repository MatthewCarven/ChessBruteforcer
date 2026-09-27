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

**Estimate so far:** v0.2 and v0.3 play the same chess (v0.3 only fixes time
handling). Together they score +25 -13 =2 against Stockfish-2000 (65%), about
+100 Elo, so **roughly 2100 on Stockfish's scale**. That needs hundreds of
games, not tens, to pin down.

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
