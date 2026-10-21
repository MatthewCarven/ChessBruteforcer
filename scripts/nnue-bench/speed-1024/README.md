# Speed at 1024: the measuring scripts (2026-10-20)

What NNUE.md's "Speed at 1024" and `../results-2026-10-20.txt` were measured
with: where a node's time goes with a 1024-hidden net, and what AVX-512, 64-byte
alignment and a lazy update would be worth, before any of them was built. One
thread each, below-normal priority, so they can run beside the bot.

- `costs-nets.cs <numbers .txt> <rounds> <name>=<net .nnue> ...`: `../costs.cs`
  with trained nets as the arms. Tree-free: an update a move (Kiwipete perft 4
  by make, with a net minus without) and an evaluation.
- `probe.cs <depth> <rounds> <name>=<net .nnue> ...`: the split of a node inside
  the search. A copy of the engine with hooks records every evaluation of a
  search and replays them, so arms without the forward pass, or without the
  updates too, search the identical tree (checked: same nodes, every value
  equal). Also an ideal lazy arm (skips exactly the pushes no evaluation
  needed, every evaluation checked exact), and counts: pushes, evaluations,
  illegal makes. Timed by the thread's own CPU time (QueryThreadCycleTime), so
  waits for a CPU beside the bot don't count.
- `probelog.cs <depth> <records a position> <net .nnue> <out .bin>`: logs a real
  search's accumulator pushes and evaluations for the lab.
- `lab.cs <net .nnue> <log .bin> <rounds>`: replays that log with each kernel
  (256 or 512 bits; plain .NET arrays as the engine had them, or memory 0, 16 or
  32 bytes past a 64-byte boundary). No engine code; every variant must agree on
  a checksum.
- `align.cs <net .nnue>`: where the engine's own arrays land against 64-byte
  boundaries (by reflection).
- `uci-ab.py <net .nnue> <rounds> <movetime ms> <name>=<engine dll>[|ENV=VALUE] ...`:
  8 threads as the bot runs the engine (UCI: EvalFile, Threads 8, Hash 256 MB),
  each build in turn, one engine at a time, below-normal priority; nodes a
  second from the engine's own info lines on bench's six positions. With the
  bot off: it would share the CPU with the bot's 8 threads otherwise.

The probe needs its copy of the engine: the sources as they were before the
speed steps (commit 619c703; the hooks don't apply to the lazy stack), made in a
scratch folder, with `probe.cs` and `probelog.cs` beside it:

```
git archive 619c703 src | tar -x -C <scratch>/probe-src
cd <scratch>/probe-src && git apply <this folder>/probe-instrumentation.patch
copy <this folder>\Probe-hooks.cs <scratch>\probe-src\src\ChessBruteforcer.Core\Engine\Nnue\Probe.cs
copy <this folder>\probe.cs <this folder>\probelog.cs <scratch>\
dotnet run -c Release --disable-build-servers <scratch>/probe.cs 9 6 h512=<h512-3m.nnue> h1024=<h1024-3m.nnue>
```

(`--disable-build-servers` leaves no MSBuild or compiler server holding memory
afterwards, which matters with the bot playing.)
