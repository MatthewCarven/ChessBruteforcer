# The rest of a node: the measuring scripts (2026-10-20)

What NNUE.md's "The rest of a node" and `../results-2026-10-20-split.txt` were
measured with: where a search node's time goes outside the network (move
generation, ordering, InCheck, make and unmake, the table, the repetition
check, the tablebase probe), what two quiescence levers are worth, and what
bitboards would cost against today's 64-square board. One thread each,
below-normal priority, timed by the thread's own CPU time
(QueryThreadCycleTime), so they can run beside the bot.

- `split-patch.py <scratch>/probe-src`: instruments a scratch copy of the
  engine (never the repo's own). `Hooks.cs` goes into it; the edits route
  each piece of a node's work through a hook in Search, Position,
  Evaluation, TranspositionTable and MoveGenerator, and add a captures-only
  generator for quiescence (the lever's prototype). Every edit must match
  exactly, so a newer engine stops the script until the edits are brought
  up to date.
- `split.cs` (`split.csproj`): bench's six positions at a fixed depth. Per
  position, a recording run notes every answer (each evaluation, InCheck,
  PickNext choice, table probe, repetition and tablebase answer, the move
  lists), a check run confirms the network gives the recorded values, then
  rounds of arms. "none" is the search with no network work (evaluations
  replayed, no accumulator pushes); a removal arm (`-gen`, `-chk`, `-tt`,
  ...) is "none" with one piece's answers replayed instead of computed; a
  doubling arm (`+gen`, `+chk`, ...) does one piece twice. Each removal arm
  must give the same nodes and read every replayed stream to its end, or
  the run stops. Arms marked "free" (`fullcap`, `fullqs`, the network on)
  run on their own tree. Before timing, every arm runs twice at depth 6, so
  the JIT's tier-1 code is compiled from a profile of all of them: without
  that, a depth-5 trial read InCheck at twice its cost.
- `lab/lab.cs` (`lab/lab.csproj`): samples every n-th position the search
  generates moves for or asks InCheck about, builds each as the engine's
  Position and as a bitboard board (12 sets, PEXT attack tables), checks
  every answer equal (the same moves as a set, the same captures-only list,
  the same InCheck), then times both.

To run them (the probe was made from commit 4ecea62), from a scratch folder
holding a copy of this one:

```
git archive 4ecea62 src | tar -x -C <scratch>/probe-src
python <scratch>/split-patch.py <scratch>/probe-src
dotnet build <scratch>/split.csproj -c Release -o <scratch>/out --disable-build-servers -p:UseSharedCompilation=false
dotnet build <scratch>/lab/lab.csproj -c Release -o <scratch>/lab/out --disable-build-servers -p:UseSharedCompilation=false
mkdir <scratch>/tb-empty
dotnet <scratch>/out/split.dll 9 6 <h1024-3m.nnue> <scratch>/tb-empty --tsv run1.tsv
dotnet <scratch>/out/split.dll 9 6 <h1024-3m.nnue> <scratch>/tb-empty --arms full,none,qsbreak,passchk,vcap,fullcap,fullqs
dotnet <scratch>/out/split.dll 9 6 <h1024-3m.nnue> <scratch>/tb-empty --hash 256 --arms full,none,-tt,fullqs
dotnet <scratch>/lab/out/lab.dll <h1024-3m.nnue> 9 32 10
```

(`git archive` from Git Bash; with a Windows path, `tar` reads `C:` as a
host, so `cd` into the scratch folder and use relative paths, or
`--force-local`.) The tables object is the bot's (no solving, no loading
mid-search) over an empty folder: past five pieces, or with castling rights,
it answers as the bot's does, and the recording counts the probes that would
have reached a table (806 of 1.57 M at depth 9, no hits).

Runs 1-3 were made before the lab's sampling hooks (`OnGen`, `OnChk`: a
null check per call) were added; the scripts here have them.

`qs-battery.py <repo>`: the mutation battery run on the build that followed
(Matthew's K(a): quiescence generates only what it searches), 14 mutants of
the repo's own sources, each restored after its run. Its test filter names
classes: EngineTests.cs holds SearchTests, UciTests and four others, and a
filter on "EngineTests" runs none of them. A mutant that crashes a test
host can put a Windows dialog on the screen, and the run waits until it is
answered.
