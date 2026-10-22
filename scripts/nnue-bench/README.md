# The network's speed (NNUE session 3, 2026-10-16)

How fast the engine searches with a network against today's evaluation. Unlike
the cost probe (`scripts/nnue-probe/`), this is the engine's own code, as it
plays.

- `pst-net.cs` writes a piece-square net: a 768 -> H x2 -> 1 network that
  computes an evaluation file's material and piece-square values
  (`Network.FromPieceSquares`). A net of a given size costs the same per
  operation whatever its weights, and this one gives a search tree like a
  real evaluation's. Random weights don't: the capture search explodes.
- `bench.ps1` runs the engine's `bench <depth> --eval <file>` for each arm in
  turn, round by round, one process per run, and prints the median speed
  against the first arm.
- `costs.cs` times the accumulator update a move and one evaluation, apart
  from any search tree.

Run from this folder, after building the engine into a folder of its own
(so a rebuild of the repo can't change it mid-run):

```
dotnet build ../../src/ChessBruteforcer.Engine -c Release -o <dir>
dotnet run -c Release pst-net.cs ../../bot/eval/eval-sf1.txt <dir>/pst-sf1-256.nnue 256
pwsh -File bench.ps1 -Engine <dir>/ChessBruteforcer.Engine.dll -Depth 9 -Rounds 8 -Arms sf1=../../bot/eval/eval-sf1.txt,net256=<dir>/pst-sf1-256.nnue
dotnet run -c Release costs.cs ../../bot/eval/eval-sf1.txt 7
```

Results: `results-2026-10-16.txt`. With session 2's trained net, the same
`bench.ps1` line with its file in place of the piece-square net gives the
speed on its own tree.

`results-2026-10-20.txt`: the trained nets h512-3m and h1024-3m, and where a
node's time goes at 1024 (forward pass, accumulator updates, the rest),
from a probe that replays a search's evaluations on the identical tree, plus
a kernel lab for AVX-512 and 64-byte alignment (NNUE.md "Speed at 1024").
Beside the bot, time a below-normal thread by its own CPU time
(QueryThreadCycleTime): wall-clock time includes its waits for a CPU.

`results-2026-10-20-split.txt`: where the rest of a node goes at 1024 once
the network's speed steps were in (ordering, generation, InCheck, make and
unmake, the table, the repetition and tablebase checks), two quiescence
levers measured, and a bitboard lab (NNUE.md "The rest of a node"). The
scripts are in `search-split/` (README there): hooks into a scratch copy of
the engine, removal arms that replay one piece's answers on the identical
tree.

Two builds, or two settings of one, on one net (2026-10-20, the AVX-512 and
lazy-update A/Bs): an arm can name its own engine and environment, e.g.
`-Arms "old=<net>;engine=<old dll>,new256=<net>;engine=<new dll>;env:CB_NNUE_SIMD=256,new=<net>;engine=<new dll>"`.
`CB_NNUE_SIMD` (256, 128 or 0) holds the engine to 256 bits, to 128 (the
path for CPUs without AVX2, such as the desktop's; 2026-10-23) or to one
number at a time; unset, it uses the widest the CPU has. `bench` prints the
width it ran at. simd128/bench-simd128.ps1 is the desktop's old-against-new
run, from its own folder on the share. bench.ps1 also prints speed by
the process's CPU time, less its start-up; beside the bot that column was
noisier than the wall clock, so read both, and pair rounds for a small gain.

`CB_ATTACKS` (pext, magic or rays; bitboards session 1, NNUE.md) picks how
the engine finds attacks: unset, PEXT tables where the CPU does PEXT in
hardware and magic multiplication elsewhere; `rays` is the ray walk from
before the sets, which leaves the sets' upkeep alone in an A/B. `bench`
prints the path it ran on (`attacks: pext`).
