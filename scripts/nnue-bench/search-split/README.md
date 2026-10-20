# The rest of a node: the measuring scripts (2026-10-20)

What NNUE.md's "The rest of a node" and "Bitboards, measured" were measured
with (`../results-2026-10-20-split.txt` at 4ecea62 in the morning,
`../results-2026-10-20-bitboards.txt` at 180fe25 at noon): where a search
node's time goes outside the network (move generation, ordering, InCheck,
make and unmake, the table, the repetition check, the tablebase probe), and
what attack tables and bitboards would save against today's 64-square
board. One thread each, below-normal priority, timed by the thread's own
CPU time (QueryThreadCycleTime), so they can run beside the bot.

- `split-patch.py <scratch>/probe-src`: instruments a scratch copy of the
  engine (never the repo's own). `Hooks.cs` goes into it; the edits route
  each piece of a node's work through a hook in Search, Position,
  Evaluation, TranspositionTable, MoveGenerator and Tablebase, and wire in
  the prototype of decision K's (b) and (c) (`AttackTables` in Hooks.cs):
  a set per colour and piece type kept beside the squares, IsAttacked by
  attack tables (PEXT, 64-bit or 16-bit entries), generation from the sets
  in the lab's order or in today's. Every edit must match exactly, so a
  newer engine stops the script until the edits are brought up to date. It
  targets 180fe25; the morning's version for 4ecea62, with the quiescence
  levers' arms (`qsbreak`, `passchk`, `vcap`, `fullcap`, `fullqs`), is at
  commit 4dce15e.
- `split.cs` (`split.csproj`): bench's six positions at a fixed depth. Per
  position, a recording run notes every answer (each evaluation, InCheck,
  PickNext choice, table probe, repetition and tablebase answer, castling's
  attack tests, the move lists by kind), a check run confirms the network
  gives the recorded values, then rounds of arms. "none" is the search with
  no network work (evaluations replayed, no accumulator pushes); a removal
  arm (`-gen`, `-genneg`, `-gencap`, `-genevade`, `-castle`, `-chk`, `-tt`,
  ...) is "none" with one piece's answers replayed instead of computed; a
  doubling arm (`+gen`, `+chk`, ...) does one piece twice. Each removal arm
  must give the same nodes and read every replayed stream to its end, or
  the run stops. The prototype's arms give today's answers, so they run on
  the identical tree too (`sets`, `tables`, `tables16`, `bbo` without the
  network; `fulltables`, `fulltables16`, `fullbbo` with it; `vbb` and
  `vbbo` check the generator against today's), except `fullbb` and
  `fullbb16`: generation in the lab's order breaks PickNext's ties another
  way, so they run on their own tree, with the network on. Before timing,
  every arm runs twice at depth 6, so the JIT's tier-1 code is compiled
  from a profile of all of them: without that, a depth-5 trial read InCheck
  at twice its cost.
- `lab/lab.cs` (`lab/lab.csproj`): samples every n-th position the search
  generates moves for or asks InCheck about, builds each as the engine's
  Position and as a bitboard board (12 sets, PEXT attack tables), checks
  every answer equal (the same moves as a set, the same captures-only list,
  the same InCheck), then times both.

To run them (the noon runs, at commit 180fe25), from a scratch folder
holding a copy of this one:

```
git archive 180fe25 src | tar -x -C <scratch>/probe-src
python <scratch>/split-patch.py <scratch>/probe-src
dotnet build <scratch>/split.csproj -c Release -o <scratch>/out --disable-build-servers -p:UseSharedCompilation=false
dotnet build <scratch>/lab/lab.csproj -c Release -o <scratch>/lab/out --disable-build-servers -p:UseSharedCompilation=false
mkdir <scratch>/tb-empty
dotnet <scratch>/out/split.dll 9 6 <h1024-3m.nnue> <scratch>/tb-empty --arms full,none,-chk,-chkL,-illegal,-tt,-rep,-tb,-pick,-order,-gen,-genneg,-gencap,-genevade,-castle,+gen,+genqs,+genneg,+make,+chk,+score
dotnet <scratch>/out/split.dll 9 6 <h1024-3m.nnue> <scratch>/tb-empty --arms full,none,-chk,-gen,sets,tables,tables16,vbb,fulltables,fulltables16,fullbb,fullbb16
dotnet <scratch>/out/split.dll 9 6 <h1024-3m.nnue> <scratch>/tb-empty --hash 256 --arms full,none,-tt,fulltables,fulltables16,fullbb,fullbb16
dotnet <scratch>/out/split.dll 9 6 <h1024-3m.nnue> <scratch>/tb-empty --arms full,none,sets,tables,bbo,vbbo,fulltables,fullbbo,fullbb
dotnet <scratch>/lab/out/lab.dll <h1024-3m.nnue> 9 32 10
```

(With no `--arms`, every arm runs.) In the noon runs the wall-clock column
of an arm on its own tree was divided by the recorded tree's nodes, which
read `fullbb` ~9% low; the thread-time figures were right, and split.cs
divides by each arm's own nodes now.

(`git archive` from Git Bash; with a Windows path, `tar` reads `C:` as a
host, so `cd` into the scratch folder and use relative paths, or
`--force-local`.) The tables object is the bot's (no solving, no loading
mid-search) over an empty folder: past five pieces, or with castling rights,
it answers as the bot's does, and the recording counts the probes that would
have reached a table (806 of 1.57 M at depth 9, no hits).

The morning's runs 1-3 were made before the lab's sampling hooks (`OnGen`,
`OnChk`: a null check per call) were added; the scripts here have them.

`qs-battery.py <repo>`: the mutation battery run on the build that followed
(Matthew's K(a): quiescence generates only what it searches), 14 mutants of
the repo's own sources, each restored after its run. Its test filter names
classes: EngineTests.cs holds SearchTests, UciTests and four others, and a
filter on "EngineTests" runs none of them. A mutant that crashes a test
host can put a Windows dialog on the screen, and the run waits until it is
answered.
