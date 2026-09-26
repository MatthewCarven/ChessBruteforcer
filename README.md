# ChessBruteforcer

It's called a bruteforcer, but the point is *not* to brute-force: it's to
work out how small the space of real chess positions is compared with the
space of bit patterns that could store one, and to throw away the "null
space" as cheaply as possible.

Every position is stored in **48 bytes**: 64 squares × 6 bits.

```
bit 5      bit 4     bits 3..1      bit 0
occupied   colour    piece type     spare
           0=white   1=P 2=N 3=B    reserved (castling / en passant, later)
                     4=R 5=Q 6=K
```

Only 13 of the 64 codes mean anything: the all-zero empty square plus the 12
pieces. Squares are indexed a1 = 0 … h8 = 63 and packed as one big-endian
bit stream, so two positions are equal exactly when their 48 bytes are equal.

## Quick start

Needs the .NET 8 SDK.

```
dotnet test
dotnet run --project src/ChessBruteforcer.Cli -- ranges
dotnet run --project src/ChessBruteforcer.Cli -- check "4k3/8/8/8/8/8/PPPPPPPP/QQ2K3"
```

## The ranges

`ranges` prints how many 48-byte boards survive each tier of checks. The
counts are **exact**, computed with combinatorics rather than enumeration,
and take about two seconds:

| tier | filter | boards left | keeps 1 in |
| --- | --- | --- | --- |
| 0 | any 384 bits | 3.9 × 10¹¹⁵ (2³⁸⁴) | |
| 1 | every square holds a real code | 2.0 × 10⁷¹ (13⁶⁴) | 2 × 10⁴⁴ |
| 2 | exactly one king per side | 1.5 × 10⁶⁸ | 1,320 |
| 3 | no pawns on rank 1 or 8 | 6.7 × 10⁶⁶ | 22 |
| 4 | material explainable | **1.44 × 10⁴⁹** | 4.6 × 10¹⁷ |

The exact tier 4 figure is 14,423,091,582,949,015,177,507,873,800,760,355,551,992,125,713,728.

For scale, published estimates put the number of *truly reachable* positions
at around 10⁴⁴–10⁴⁶. That estimate also counts side to move and castling
rights, so it isn't a like-for-like comparison. Closing the rest of the gap
is what the later tiers in [TODO.md](TODO.md) are for.

### Why "no two queens" is the wrong rule

Two white queens is perfectly legal: a pawn promoted. The real rule is that
**extra pieces have to be paid for with missing pawns**. For each side:

```
promoted pieces needed = (Q-1)⁺ + (R-2)⁺ + (N-2)⁺ + (light B-1)⁺ + (dark B-1)⁺
promoted pieces needed ≤ 8 − pawns on board
```

Bishops are counted **by square colour**: two light-squared bishops already
mean one was promoted. The naive rule, "never more than you started with",
would give 2.1 × 10⁴⁰ at tier 4. Promotions multiply the space by about
700 million.

## Commands

```
ranges                         exact survivors at every tier
check <board>...               say whether boards are possible, and why not
show <board>                   diagram, FEN and hex for a board
sample [count] [seed]          collapse random boards from superposition and check them

file add <file> <board>...     append boards to a board file
file import <file> <text>      append every FEN / hex line of a text file ('#' = comment)
file list <file>               print each board as FEN (hex if meaningless)
file export <file> <text>      write the boards out as FEN lines
file check <file>              check every board, tallying where each one failed
file dedupe <file> [out]       sort and remove duplicates (in place by default)
file count <file>              number of boards
```

A `<board>` is a FEN (only the placement field is used) or 96 hex digits.

## Board files (`.cbb`)

A board file is nothing but 48-byte boards back to back: no header and no
separators.

- Board *n* is at byte offset 48·*n*, and the count is the length ÷ 48.
- Files concatenate with `cat`.
- Identical positions are identical bytes, so `dedupe` is just sort + unique.
- Sorted files put similar boards next to each other, which is what folder
  compression (NTFS/btrfs/zstd) feeds on.

Meaningless boards round-trip too, so a file can hold raw samples from the
superposition as well as real positions.

## Superposition: `Possibility/`

A C# port of the core of
[PythonBinaryPossibility](https://github.com/MatthewCarven/PythonBinaryPossibility):
`BinaryPossibility`, `BinaryRegister` (counts, entropy, lazy enumeration,
most-likely-first A\*, weighted collapse) and `BinaryRegisterGroup`. Counts
are `BigInteger`, because a board register has 2³⁸⁴ states.

`SuperposedBoard` is a 384-bit register laid out exactly like the packed
board. Collapse any bits and `CountValidCodeCompletions()` tells you how many
completions still put a real code on every square, working square by square
so it never enumerates. `sample` shows why the checks matter: 100% of boards
collapsed from raw superposition fail tier 1, and even among meaningful codes
only about 1 in 1,300 has the right kings.

## Layout

```
src/ChessBruteforcer.Core/
  Piece.cs, SquareCodec.cs      the 6-bit square code
  PackedBoard.cs, Fen.cs        48-byte boards, FEN and hex in and out
  BoardChecker.cs               tiered checks, reporting every violation
  MaterialRules.cs              starting armies and the promotion rule
  BoardGeometry.cs              back ranks and square colours (any size, for testing)
  BoardFile.cs                  .cbb read / write / append / dedupe
  RangeCounter.cs               exact per-tier counts
  Possibility/                  the BinaryPossibility port + SuperposedBoard
src/ChessBruteforcer.Cli/       the commands above
tests/ChessBruteforcer.Tests/   xunit
```

## How the counter is checked

The tier 4 count comes from a formula, so it's tested against code that does
the counting the slow way:

- **Brute force**: every one of the 13⁶ boards on 2×3 and 3×2 boards, run
  through the real checker, with army sizes small enough that the promotion
  rule actually matters.
- **Square-by-square dynamic programming** on 4×4 and 3×5 boards, which are
  too big to brute-force, tracking every running piece count.

Both agree with the counter exactly, at every tier, with and without
promotions.

See [TODO.md](TODO.md) for where this is going and
[WORKLOG.md](WORKLOG.md) for what has been done.
