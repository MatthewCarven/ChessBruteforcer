# piece-pairs: 4,465,944 positions from 175,885 games, 2026-10-21 01:26

The full output of `scripts/nnue/piece-pairs.py report <work dir> h1024-3m --replicates 400 --check-se 20`, as it
printed it; scripts/piece-pairs-2026-09.md is the report built from it.

two same-coloured bishops on one side: 0 positions (they'd count as a pair)
three or more: bishops 2, knights 3, rooks 9 positions
near-equal material (1-3-3-5-9 within a pawn): 2,903,356 positions (65.0%)
sf1: held-back error 0.031460 at K 1, 0.030396 at K 1.2705
h1024-3m: held-back error 0.012659 at K 1, 0.012432 at K 1.1117

## Who holds the pairs

| | white has it | black has it | both | neither | one side only: positions | games | mean phase | of them at near-equal material |
|---|---|---|---|---|---|---|---|---|
| bishop pair | 31.4% | 32.4% | 21.5% | 57.8% | 922,723 | 98,436 | 18.5 | 603,712 |
| knight pair | 28.0% | 27.7% | 18.9% | 63.2% | 798,265 | 89,821 | 19.0 | 528,459 |
| rook pair | 69.4% | 69.0% | 63.7% | 25.3% | 494,851 | 54,341 | 13.2 | 98,085 |
| all three | 14.2% | 14.3% | 13.1% | 84.5% | 107,223 | 19,184 | 21.2 | 14,203 |

one side only, by stage (positions):
  phase 20-24: 1,918,224 positions; bishop pair 581,314, knight pair 538,391, rook pair 34,769, all three 81,563
  phase 12-19: 1,212,654 positions; bishop pair 248,818, knight pair 196,345, rook pair 254,920, all three 25,438
  phase 0-11: 1,335,066 positions; bishop pair 92,591, knight pair 63,529, rook pair 205,162, all three 222

what else differs where only one side has it: the commonest material differences from that side's view (P N B R Q, + = it has more), all positions | near-equal material:
  bishop pair: N-1 B+1 39%, P-1 N-1 B+1 9%, P+1 N-1 B+1 7%, P-1 B+1 4% | N-1 B+1 60%, P-1 N-1 B+1 13%, P+1 N-1 B+1 11%, N-2 B+2 4%
  knight pair: N+1 B-1 41%, P+1 N+1 B-1 8%, P-1 N+1 B-1 7%, P-1 N+1 4% | N+1 B-1 61%, P+1 N+1 B-1 13%, P-1 N+1 B-1 10%, N+2 B-2 4%
  rook pair: B-1 R+1 6%, N-1 R+1 6%, P+1 B-1 R+1 5%, P+1 N-1 R+1 5% | P-1 B-1 R+1 16%, P-1 N-1 R+1 14%, P+1 N-1 B-1 R+1 9%, N-1 B-1 R+1 7%
  all three: P-1 N+1 15%, N+1 12%, P-1 B+1 12%, B+1 10% | P-2 N+1 46%, P-2 B+1 36%, P-3 N+1 6%, P-3 B+1 5%

correlation of the white-minus-black terms (bishop pair, knight pair, rook pair, all three):
  +1.00  -0.51  -0.08  +0.16
  -0.51  +1.00  -0.06  +0.21
  -0.08  -0.06  +1.00  +0.05
  +0.16  +0.21  +0.05  +1.00

## (b) Every term of the regression over all positions: Stockfish (cp inside s) [95%], the same model fitted to the others' scores at K 1, and Stockfish by least squares on cp held to +-1000

| term | Stockfish | sf1 | h1024-3m | Stockfish, cp held |
|---|---|---|---|---|
| white's edge (mg) | +29 [+28, +30] | +21 | +27 | +35 [+34, +36] |
| white's edge (eg) | -10 [-11, -8] | -5 | -9 | -10 [-13, -8] |
| tempo (mg) | +28 [+28, +28] | -4 | +27 | +44 [+43, +44] |
| tempo (eg) | +23 [+22, +23] | -2 | +19 | +29 [+28, +29] |
| pawn (mg) | +99 [+97, +100] | +71 | +87 | +99 [+97, +100] |
| pawn (eg) | +185 [+183, +187] | +150 | +174 | +184 [+182, +186] |
| knight (mg) | +389 [+384, +394] | +267 | +341 | +407 [+402, +411] |
| knight (eg) | +539 [+532, +546] | +425 | +501 | +377 [+372, +382] |
| bishop (mg) | +394 [+389, +399] | +267 | +344 | +410 [+406, +414] |
| bishop (eg) | +558 [+551, +566] | +445 | +521 | +405 [+400, +410] |
| rook (mg) | +549 [+538, +561] | +345 | +476 | +536 [+526, +547] |
| rook (eg) | +915 [+904, +926] | +720 | +852 | +670 [+664, +677] |
| queen (mg) | +1124 [+1094, +1154] | +918 | +988 | +820 [+806, +834] |
| queen (eg) | +1713 [+1681, +1744] | +1236 | +1603 | +1293 [+1278, +1308] |
| bishop pair | +48 [+46, +51] | +40 | +45 | +51 [+48, +54] |
| knight pair | +8 [+5, +10] | +4 | +7 | -2 [-5, +1] |
| rook pair | -27 [-33, -20] | +0 | -22 | -27 [-34, -21] |
| all three | -37 [-43, -30] | -13 | -29 | -2 [-8, +3] |

## (b) Stockfish's worth of each pair, cp [95%]

| | all positions, squashed fit | near-equal material, squashed fit | near-equal material, cp held to +-1000 | all positions, cp held to +-1000 |
|---|---|---|---|---|
| bishop pair | +48 [+46, +51] | +47 [+45, +49] | +64 [+61, +67] | +51 [+48, +54] |
| knight pair | +8 [+5, +10] | +7 [+5, +9] | +9 [+6, +13] | -2 [-5, +1] |
| rook pair | -27 [-33, -20] | -33 [-42, -25] | -43 [-55, -31] | -27 [-34, -21] |
| all three, beyond the three pairs | -37 [-43, -30] | -59 [-69, -48] | -75 [-90, -60] | -2 [-8, +3] |
| all three pairs at once (the sum) | -7 [-17, +2] | -38 [-51, -24] | -44 [-64, -25] | +20 [+11, +30] |

the sandwich's standard errors against 20 bootstrap refits over games (all positions):
  bishop pair: sandwich 1.09 cp, bootstrap 1.22 cp
  knight pair: sandwich 1.07 cp, bootstrap 0.94 cp
  rook pair: sandwich 3.40 cp, bootstrap 4.23 cp
  all three, beyond the three pairs: sandwich 3.19 cp, bootstrap 3.55 cp
  all three pairs at once (the sum): sandwich 4.83 cp, bootstrap 5.83 cp

## (b) sf1's lean (its term minus Stockfish's), cp [95%]; K 1.2705

| | its term at K 1 | lean at K 1 | lean at K | near-equal material: lean at K | near-equal, cp held to +-1000: lean at K | the squashed error at K, x 1000 (cp at an even score) |
|---|---|---|---|---|---|---|
| bishop pair | +40 | -8 [-10, -6] | +1 [-1, +3] | +2 [-0, +3] | -11 [-14, -8] | +1.82 [+0.21, +3.42] (+1) |
| knight pair | +4 | -4 [-6, -2] | -3 [-5, -1] | -2 [-4, -0] | -4 [-7, -1] | -2.62 [-4.25, -0.98] (-2) |
| rook pair | +0 | +27 [+21, +33] | +26 [+21, +32] | +32 [+25, +39] | +42 [+31, +53] | +6.02 [+3.43, +8.62] (+4) |
| all three, beyond the three pairs | -13 | +23 [+18, +29] | +22 [+16, +27] | +44 [+35, +52] | +59 [+47, +72] | -4.18 [-6.53, -1.83] (-3) |
| all three pairs at once (the sum) | +31 | +38 [+30, +46] | +46 [+38, +54] | +75 [+64, +87] | +86 [+70, +103] | +1.05 [-2.87, +4.96] (+1) |

## (b) h1024-3m's lean (its term minus Stockfish's), cp [95%]; K 1.1117

| | its term at K 1 | lean at K 1 | lean at K | near-equal material: lean at K | near-equal, cp held to +-1000: lean at K | the squashed error at K, x 1000 (cp at an even score) |
|---|---|---|---|---|---|---|
| bishop pair | +45 | -4 [-5, -3] | -1 [-1, +0] | -0 [-1, +0] | -7 [-8, -5] | +0.25 [-0.56, +1.06] (+0) |
| knight pair | +7 | -1 [-2, -0] | -0 [-1, +1] | +0 [-1, +1] | -2 [-3, -0] | -0.17 [-0.97, +0.63] (-0) |
| rook pair | -22 | +4 [+1, +8] | +2 [-1, +5] | +3 [-1, +7] | +5 [-2, +11] | +1.03 [-0.44, +2.50] (+1) |
| all three, beyond the three pairs | -29 | +8 [+5, +11] | +4 [+1, +6] | +3 [-1, +8] | +11 [+5, +18] | -2.05 [-3.30, -0.79] (-1) |
| all three pairs at once (the sum) | +0 | +8 [+3, +12] | +5 [+1, +10] | +6 [+0, +12] | +8 [-2, +17] | -0.94 [-3.07, +1.19] (-1) |

## (b) By stage: each term fitted separately in each band of phase (24 = every piece on), cp [95%]; leans at K

| term | positions, one side only | Stockfish | sf1's lean | h1024-3m's lean |
|---|---|---|---|---|
| bishop pair, phase 20-24 | 581,314 | +37 [+35, +40] | -0 [-3, +2] | -0 [-1, +1] |
| bishop pair, phase 12-19 | 248,818 | +52 [+49, +56] | +4 [+1, +6] | -1 [-2, +0] |
| bishop pair, phase 0-11 | 92,591 | +78 [+72, +84] | +6 [+2, +11] | -1 [-4, +2] |
| knight pair, phase 20-24 | 538,391 | +8 [+5, +11] | -2 [-4, -0] | -0 [-1, +1] |
| knight pair, phase 12-19 | 196,345 | +7 [+4, +11] | -5 [-7, -2] | +0 [-1, +2] |
| knight pair, phase 0-11 | 63,529 | +5 [-1, +11] | +3 [-3, +8] | -1 [-3, +2] |
| rook pair, phase 20-24 | 34,769 | -33 [-50, -17] | +11 [-2, +24] | +2 [-5, +9] |
| rook pair, phase 12-19 | 254,920 | -14 [-25, -4] | +4 [-5, +13] | +1 [-4, +6] |
| rook pair, phase 0-11 | 205,162 | -25 [-32, -19] | +29 [+23, +35] | +2 [-1, +6] |
| all three, phase 20-24 | 81,563 | -41 [-48, -34] | +29 [+24, +35] | +5 [+2, +8] |
| all three, phase 12-19 | 25,438 | +6 [-9, +20] | -25 [-37, -13] | -2 [-8, +5] |

## (a) Like for like (pawns, rooks, queens equal; 400 bootstrap replicates over games)

Worth from the pair side's view, cp (the mean of s over the positions, both sides to move alike, back into cp); scorers at their K. The regression's figure is its minor-piece and pair terms on the same positions.

| | positions | games | mean phase | Stockfish | regression's figure | sf1's lean | h1024-3m's lean |
|---|---|---|---|---|---|---|---|
| BB v BN | 92,713 | 13,420 | 17.6 | +53 [+51, +56] | +57 | +4 [+2, +6] | +1 [+0, +2] |
| NN v BN | 57,111 | 8,161 | 17.9 | -10 [-13, -7] | -1 | +2 [-0, +5] | +1 [-0, +2] |
| BB v NN | 21,491 | 3,091 | 18.0 | +62 [+57, +67] | +58 | +2 [-3, +5] | -1 [-3, +1] |
| BBN v BNN | 267,095 | 39,740 | 21.3 | +39 [+37, +40] | +47 | +4 [+3, +5] | -0 [-1, +0] |
| B v N | 109,636 | 13,217 | 12.5 | +2 [+0, +5] | +12 | +6 [+4, +9] | +1 [-0, +2] |

Less the bishop-against-knight difference (B v N, reweighted to the same pawns, rooks and queens): what is left is the pair's own worth, + favouring the pair.

| | Stockfish | sf1's lean | h1024-3m's lean |
|---|---|---|---|
| bishop pair: (BB v BN) - (B v N) | +53 [+49, +57] | -5 [-8, -2] | +0 [-1, +2] |
| knight pair: (NN v BN) + (B v N) | -10 [-14, -6] | +13 [+9, +16] | +1 [-1, +3] |

(397 s)
