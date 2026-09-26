# Worklog

## 2026-09-26: milestone 1, the foundation

**Decisions**

- 6 bits per square, 48 bytes per board:
  `[occupied][colour][type×3][spare]`. The spare bit is reserved (it must be
  0 for now) and is kept for castling or en passant. 4 bits (32 bytes) would
  fit 13 states, but 6 gives each bit a meaning, which suits the
  superposition work.
- C# / .NET 8, xunit for tests.
- Board files are headerless runs of 48-byte records, so dedupe is sort +
  unique and sorted files compress well.
- The first exact count covers material rules only. King adjacency and the
  rest are on the roadmap.

**Correction to the starting assumption**

"There can never be two queens on one side" isn't true: promotion makes up
to nine possible. The rule the checker uses is that promoted pieces needed ≤
missing pawns, with bishops counted by square colour. Getting this right
changes the tier 4 count by about 7 × 10⁸.

**Results**

- Exact survivors: 2³⁸⁴ → 13⁶⁴ ≈ 2.0e71 → 1.5e68 → 6.7e66 → **1.44e49**.
- The counter is checked against brute force (2×3 and 3×2 boards) and
  square-by-square DP (4×4, 3×5). They agree at every tier.
- `sample`: none of 20,000 raw collapses has a meaningful code on every
  square, and about 0.07% of meaningful-code collapses have the right kings.
  Both match the counted ratios.

**Direction (thesis)**

Map the minimum-spec game and its win/loss tree, then sort it by favourable
outcome, working backwards from the endgame. King vs king is a dead draw, so
the first real target is K+Q v K / K+R v K via retrograde analysis. It's
milestone 2 in TODO.md.

**Competition plan**

The engine will only ever play openly as software: local engine-vs-engine
matches first, then a labelled Lichess BOT account, then engine rating lists
(CCRL) and tournaments. Added as milestone 3 in TODO.md, together with the
UCI protocol it needs. Solved endgames will be stored as indexed outcomes
rather than 48-byte boards, because that is the storage that scales.
