# Piece pairs: what both bishops, both knights and both rooks are worth, and whether h1024-3m knows (2026-10-21)

**Headline: Stockfish puts the bishop pair at about half a pawn (+48 cp), the knight pair at about nothing (-10 to
+8 cp, depending on the method), and a second rook at 27 cp less than the first. h1024-3m has learned all three to
within 2 cp, so there is nothing to add to the net. sf1 has the bishop pair right and overvalues the second rook by
26 cp.**

Matthew's idea, bonuses for owning both rooks, both knights and both bishops, each on its own and all three
together, holds for the bishops: sf1 needed its hand-typed term, and the net learned the same thing by itself. For
the knights the bonus would be about zero. For the rooks it would have the wrong sign: the second rook is worth
less, not more, as Kaufman (1999) found in master games. As for all three together, these games can't tell it
apart from "a piece for pawns" (below).

The data is September's held-back positions: 4,465,944 quiet positions without mates from 175,885 games, never
trained on, each with Lichess's Stockfish score. All figures are centipawns from the point of view of the side
with the pair, with 95% intervals by game.

## The pairs

"Lean" is the scorer's figure minus Stockfish's, fitted on the same positions: + means it values the pair more
than Stockfish does. It is taken at the scorer's own overall scale (its fitted K; see "Scale" below). The column
"at K 1" gives h1024-3m's lean on its raw scores, as the engine uses them.

| | positions where only one side has it | Stockfish, like for like (a) | **Stockfish, regression (b)** | (b) at near-equal material | **h1024-3m's lean** | h1024-3m at K 1 | sf1's lean |
|---|---|---|---|---|---|---|---|
| bishop pair | 922,723 (21%) | +53 [+49, +57] | **+48 [+46, +51]** | +47 [+45, +49] | **-1 [-1, 0]** | -4 [-5, -3] | +1 [-1, +3] |
| knight pair | 798,265 (18%) | -10 [-14, -6] | **+8 [+5, +10]** | +7 [+5, +9] | **0 [-1, +1]** | -1 [-2, 0] | -3 [-5, -1] |
| rook pair | 494,851 (11%) | (no swap exists) | **-27 [-33, -20]** | -33 [-42, -25] | **+2 [-1, +5]** | +4 [+1, +8] | **+26 [+21, +32]** |
| all three, beyond the three pairs | 107,223 (2.4%) | | -37 [-43, -30] | -59 [-69, -48] | +4 [+1, +6] | +8 [+5, +11] | +22 [+16, +27] |
| all three pairs at once (the sum) | 107,223 (2.4%) | | -7 [-17, +2] | -38 [-51, -24] | +5 [+1, +10] | +8 [+3, +12] | +46 [+38, +54] |

By stage, each pair's term fitted on its own in each band of phase (24 = every piece on the board, 0 = kings and
pawns only):

| | every piece or nearly (phase 20-24) | the middle (12-19) | few pieces left (0-11) |
|---|---|---|---|
| bishop pair: positions | 581,314 | 248,818 | 92,591 |
| Stockfish | +37 [+35, +40] | +52 [+49, +56] | +78 [+72, +84] |
| h1024-3m's lean | 0 [-1, +1] | -1 [-2, 0] | -1 [-4, +2] |
| sf1's lean | 0 [-3, +2] | +4 [+1, +6] | +6 [+2, +11] |
| knight pair: positions | 538,391 | 196,345 | 63,529 |
| Stockfish | +8 [+5, +11] | +7 [+4, +11] | +5 [-1, +11] |
| h1024-3m's lean | 0 [-1, +1] | 0 [-1, +2] | -1 [-3, +2] |
| sf1's lean | -2 [-4, 0] | -5 [-7, -2] | +3 [-3, +8] |
| rook pair: positions | 34,769 | 254,920 | 205,162 |
| Stockfish | -33 [-50, -17] | -14 [-25, -4] | -25 [-32, -19] |
| h1024-3m's lean | +2 [-5, +9] | +1 [-4, +6] | +2 [-1, +6] |
| sf1's lean | +11 [-2, +24] | +4 [-5, +13] | +29 [+23, +35] |
| all three, beyond the pairs: positions | 81,563 | 25,438 | 222 (too few) |
| Stockfish | -41 [-48, -34] | +6 [-9, +20] | |
| h1024-3m's lean | +5 [+2, +8] | -2 [-8, +5] | |
| sf1's lean | +29 [+24, +35] | -25 [-37, -13] | |

The bishop pair grows as the board empties, from +37 to +78, and h1024-3m follows it within a centipawn in every
band. sf1's hand-typed pair (20 with every piece on, 84 with only kings and pawns, before its scale) follows it too,
a few cp generous late. Nowhere does the net lean on a pair by more than 2 cp, or by 5 on the all-three term.

## Like for like, in detail

Here the minor pieces differ in one way only, and pawns, rooks and queens are equal, so material and stage are equal
by construction. BB v BN measures the bishop pair plus whatever a lone bishop is worth over a lone knight. The
single-minor B v N positions measure that difference, so the last two rows take it away (reweighted to the same
pawns, rooks and queens).

| | positions (games) | mean phase | Stockfish | the regression's figure | h1024-3m's lean | sf1's lean |
|---|---|---|---|---|---|---|
| BB v BN | 92,713 (13,420) | 17.6 | +53 [+51, +56] | +57 | +1 [0, +2] | +4 [+2, +6] |
| NN v BN | 57,111 (8,161) | 17.9 | -10 [-13, -7] | -1 | +1 [0, +2] | +2 [0, +5] |
| BB v NN | 21,491 (3,091) | 18.0 | +62 [+57, +67] | +58 | -1 [-3, +1] | +2 [-3, +5] |
| BBN v BNN (the usual bishop-for-knight trade) | 267,095 (39,740) | 21.3 | +39 [+37, +40] | +47 | 0 [-1, 0] | +4 [+3, +5] |
| B v N | 109,636 (13,217) | 12.5 | +2 [0, +5] | +12 | +1 [0, +2] | +6 [+4, +9] |
| bishop pair alone: (BB v BN) - (B v N) | | | +53 [+49, +57] | | 0 [-1, +2] | -5 [-8, -2] |
| knight pair alone: (NN v BN) + (B v N) | | | -10 [-14, -6] | | +1 [-1, +3] | +13 [+9, +16] |

## Where the methods disagree, and why

- **The knight pair: -10 like for like, +8 by regression.** Two-minor positions pin down only two sums: the pairs
  together, bishop + knight = +43 (BB v BN + NN v BN), and twice the bishop's edge over a knight plus the bishop
  pair minus the knight pair = +62 (BB v NN). How +43 splits between the two pairs depends on how much a lone
  bishop beats a lone knight at that stage. If they are equal (as B v N says, +2, though those positions come later
  in the game), the knight pair is -10. If the bishop is ~9 cp better (as the regression has it there), the knight
  pair is about 0. The regression's +8 comes from fitting every configuration at once, and it misses NN v BN by 9
  cp. Either way the knight pair is within 10 cp of nothing, and the net's lean is 0 to +1 by both methods.
- **All three: -37 over all positions, -59 at near-equal material.** Wherever one side has all three pairs and the
  other doesn't, the other side is almost always a minor piece down. Over all positions the commonest cases are a
  piece for one pawn (27%) and a piece up outright (22%). At near-equal material they are a piece for two pawns
  (82%; the rule allows a pawn either way) or for three (11%). So this term measures how Stockfish prices a piece
  against pawns early in the game (a little below what the per-piece values say), not pairs working together.
  These games hold almost no positions with all three pairs against fewer at equal material. Even so, h1024-3m
  leans only +3 to +5 there.
- **The rook pair: -27 over all positions, -33 at near-equal material.** These agree within their intervals. The
  positions behind it are mostly a rook against a minor piece (the exchange, often with a pawn back) or against two
  minors.
- **Plain centipawns against the squashed scale.** A fit on centipawns held to +-1000 weights won positions most,
  and it moves some terms by 10-20 cp: the bishop pair is +51 over all positions and +64 at near-equal material; the
  knight pair -2 and +9; all three -2 and -75. The squashed scale is the trainer's own, and it is the one the tables
  use. On plain centipawns h1024-3m leans a little more (bishop pair -7, all three +11). That comes from positions
  far from equal, where the trainer's scale rightly cares little about tens of centipawns.
- **Scale.** h1024-3m scores everything about 10% smaller than Stockfish (fitted K 1.112). Its pawn is 87 cp where
  Stockfish's is 99, and its knight 341 against 389. So on its raw scores (the "at K 1" column) every term reads
  about 10% low: the bishop pair -4, the rook pair +4. That isn't about pairs, and a uniform scale doesn't change
  which move the search prefers. sf1 is about a fifth smaller (K 1.271).
- **The intervals.** The regression's clustered intervals agree with 20 bootstrap refits over games within the
  refits' own noise: the bootstrap's spread is 0.9 to 1.2 times the sandwich's (rook pair 4.2 against 3.4 cp). If in
  doubt, widen them by a fifth. Nothing above changes.

## Decision for Matthew: piece pairs

**Matthew's choice (2026-10-21): (b) and (c) go on the long-term TODO**, to try should the other levers run out
("the closer we fit to real life"). Nothing is built now.

- **(a) Nothing to do (recommended).** h1024-3m has learned the bishop pair, the knight pair's near-nothing and the
  second rook's discount, at every stage: lean -1, 0 and +2 cp. Its squashed hidden units do what the per-square sum
  in sf1 couldn't. Cost: nothing.
- (b) A bonus term beside the net, or pair flags as extra inputs. The most either could correct is the lean
  measured here: about 2 cp on a pair, and 5 cp for all three in 2.4% of positions. That is far below the ~12 Elo
  that a 2,000-game match can resolve. Cost: a bonus is an engine change with tests and a match (the bot off ~2 h).
  Inputs mean a new input layout in the trainer and the engine, a ~12 h retrain on the 1660, and the match. Not
  worth it on these numbers.
- (c) sf1 only: give sf1 a rook-pair term and retune it. sf1 leans +26 [+21, +32] on the second rook (it has no such
  term) and is otherwise close. Cost: a Texel round (~1 h) and a match against sf1 (~2 h, the bot off). This is
  only worth doing if sf1 is ever going to play again; the bot plays h1024-3m.

## How it was measured

- **Positions**: games\nnue\2026-09\held-back.bin (every game numbered 0 mod 50, never trained on), mates left out
  as the held-back error leaves them out. These are quiet positions: after move 8, more than 5 pieces, not in check,
  and neither the move before nor the move played a capture or a promotion.
- **A pair** is two or more of the piece on one side. Two bishops on squares of one colour never occur here, and
  three of a piece (by promotion) occur in 14 positions in all.
- **(a) Like for like**: the mean of s(cp) = 1/(1+10^(-cp/400)) over the positions, with both sides to move weighted
  alike, turned back into cp. Intervals come from 400 bootstrap replicates over games.
- **(b) Regression**: Stockfish's score on each side's pawns, knights, bishops, rooks and queens (white minus black,
  each with a full-board and a bare-board value blended by phase, as sf1 does), white's edge and the tempo, plus one
  term per pair (white has it minus black has it) and one for all three at once. It is fitted as the tuner fits
  (least squares of s(model) against s(score), so every term comes out in cp), over all positions and over the
  65% at near-equal material (1-3-3-5-9 counts within a pawn). A plain least-squares fit on cp held to +-1000 is the
  check. A pair's term is what the second piece adds beyond the first, which is exactly what a bonus would add.
  Intervals are the sandwich, clustered by game.
- **The scorers**: h1024-3m (bot\eval\h1024-3m.nnue) as the engine computes it: train.py's int_scores on the GPU, a
  held-back error of 0.012659 at K 1, matching its training log. sf1 (bot\eval\eval-sf1.txt) through train.py's
  numbers_eval. Each gets the same fits on its own scores, at K 1 and times its fitted K.
- **Not controlled**: who played better. A player who keeps the bishop pair may be the one outplaying the other, and
  Stockfish's score then carries a little of that. This applies to Stockfish's worth, not to the lean: the net and
  Stockfish see the same positions.
- **Script**: scripts/nnue/piece-pairs.py (`features`, `net`, `report`). A run takes about 7 minutes of laptop CPU
  and a minute of the GPU; this one ran with the bot off and nothing else running. The full output is
  scripts/piece-pairs-2026-09-full.md.
