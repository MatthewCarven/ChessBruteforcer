# The NNUE cost probe (2026-10-16)

Scratch measurements behind NNUE.md, kept so they can be run again (after a
speed change to the search, say). Not engine code: the engine is untouched.

- `Probe.cs` counts evaluations and, by mode, adds work beside the real
  evaluation: the evaluation again (its cost), a 768 -> H x2 -> 1 SCReLU
  network, or HalfKP 256 x2 -> 32 -> 32 -> 1, with random weights and the
  accumulators kept up to date on every board change. Upper bounds (see
  NNUE.md).
- `patch.py` puts Probe.cs and two hooks (Evaluation.Evaluate, Position.Put)
  into a copy of the source in `tree/` (ignored by git).
- `bench.cs` runs the engine's six bench positions on one thread, every arm
  on the same tree, arms interleaved round by round.
- `count.cs` counts labelled positions per analysed game under the tuner's
  quiet rule; `july.py` counts July's analysed games from the index.

Run from this folder (Git Bash for the first three lines):

```
mkdir tree && git archive HEAD src | tar -x -C tree
python patch.py
dotnet run -c Release bench.cs 9 5 ../../bot/eval/eval-sf1.txt
dotnet run -c Release count.cs ../../games/tune/eval-games-2026-09.cbg 100000
python -I july.py "../../games/index/2026-07/parquet/part-*.parquet" <temp dir>
```

Results: `results-2026-10-16.txt`.
