# TODO / roadmap

If you're asking "where are we?", this is the answer. Top to bottom is
roughly the order of work. Tick items off here and log them in
[WORKLOG.md](WORKLOG.md).

## Done: milestone 1, the foundation

- [x] 6-bit square code, 48-byte `PackedBoard`, FEN and hex in and out
- [x] Tiered checker: meaningless codes, king count, pawns on back ranks,
      promotion-aware material (bishops by square colour)
- [x] `.cbb` board files: add, import, list, export, check, sort + dedupe
- [x] Exact per-tier counter, cross-checked by brute force and by DP
- [x] C# port of the BinaryPossibility core, plus `SuperposedBoard`
- [x] CLI: `ranges`, `check`, `show`, `sample`, `file …`

## Next: milestone 2, the minimum-spec endgame tree (thesis core)

The plan: take the smallest game that can actually be won, map its whole
tree, record win/loss, and sort it by favourable outcome. Endgames matter
most, so work **backwards from the end** (retrograde analysis, which is how
endgame tablebases are built) rather than forwards from the start.

- King vs king on its own is always a draw, so there is nothing to rank.
  The smallest sets with real wins are **three pieces**: K+Q v K and K+R v K
  (then K+P v K, the first one with promotion in it).
- 3 pieces × 64 squares × side to move ≈ 500k raw positions, small enough
  to solve completely and hold in memory.

Steps:

- [ ] Position = packed board + side to move (+ later castling / en
      passant using the spare bit)
- [ ] Legal move generator (pseudo-legal moves, then filter out moves that
      leave your own king in check), tested with perft on standard positions
- [ ] "Un-move" generator for going backwards
- [ ] Retrograde solver for a given material set: mark checkmates and
      stalemates, then iterate backwards, labelling every position
      WIN-in-n / LOSS-in-n / DRAW
- [ ] Store results as sorted `.cbb` + a parallel outcome byte per record,
      so the files dedupe and compress the same way
- [ ] Commands: `solve KQvK`, `probe <fen>` (outcome + best move), `line <fen>`
      (the best play all the way to mate)
- [ ] "Sort the tree by favourable outcome": order each position's moves by
      result (fastest win first, slowest loss last) and export the principal
      tree from any starting position
- [ ] Use symmetry (mirroring / rotation, 8-fold without pawns) to cut
      storage about 8×
- [ ] Grow the material set: KRvK → KBNvK → KPvK → 4 pieces …

## Later: tighter null-space tiers

Each of these is a necessary condition for a legal position that is still
cheap to check:

- [ ] Kings on adjacent squares (impossible)
- [ ] Side to move cannot capture the enemy king (needs move generation, see above)
- [ ] Promotions need captures: a pawn has to get past the enemy pawn on its
      file, and a pawn that changes file captured something. Bound
      promotions and pawn file changes by the opponent's missing pieces
- [ ] Pawn structure: e.g. doubled pawns need a capture per extra pawn on a file
- [ ] Bishops trapped behind unmoved pawns (e.g. a bishop on c1 with b2 and
      d2 pawns at home cannot have left, and cannot be a promoted piece either)
- [ ] Extend `RangeCounter` to count each new tier exactly, or estimate it by
      sampling when an exact count is out of reach
- [ ] Castling rights and en passant in the spare bit, with their placement
      rules (a rook with rights must be on its corner, the king on e1/e8, an
      en passant pawn on rank 4/5 with empty squares behind it)

## Later: storage at scale

- [ ] External-sort `dedupe` (merge sorted chunks) for files bigger than RAM
- [ ] `file merge a.cbb b.cbb` for sorted-file union
- [ ] Benchmark compression of sorted vs unsorted `.cbb` (zstd, NTFS
      compression)
- [ ] Storing *games*, not just positions: a start board + move list, or
      chains of board hashes

## Later: superposition

- [ ] Count tier 2+ completions of a partially collapsed `SuperposedBoard`
      (at the moment only tier 1 is counted per square)
- [ ] Weighted superposition over *pieces* rather than bits (e.g. "this
      square is a knight or empty"), which fits the checker far better than
      raw bits
- [ ] Port the rest of PythonBinaryPossibility if it turns out to be useful
      (trees, glitch, entropy tools)
