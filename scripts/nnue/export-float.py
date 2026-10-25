"""A net's float weights, for training or quantising elsewhere: an --output-buckets checkpoint as a numpy .npz and a
plain-words note of the layout, the forward pass and the training.

    python scripts/nnue/export-float.py <run .pt> <out dir>

Writes <out dir>/<run>.npz and <out dir>/<run>-layout.md. Untrained buckets take their neighbour's row, as the .nnue
export fills them (train.fill_untrained). Then a check: the float forward pass written in the note, over random
positions, against the run's .nnue (beside the .pt) as nnue_reference evaluates it, the int16 net the engine runs;
and what rounding each layer alone as the export does costs. Run it with the venv's python (the .pt needs torch to
load; it's this trainer's own file, so weights_only=False).
"""
import pathlib
import sys

import numpy as np

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import train  # noqa: E402
import nnue_reference  # noqa: E402

NOTE = """# {run}: float weights, and how to read them

768 -> {h}x2 -> {k} output buckets, squared clipped ReLU, trained at full precision (float32) on Lichess
positions scored by Stockfish ({months}). Every array in `{run}.npz` is float32.

| array | shape | what it is |
|---|---|---|
| `ft_weight` | (768, {h}) | the first layer: row i is input i's column of {h} weights |
| `ft_bias` | ({h},) | the first layer's bias, added once per side |
| `out_weight` | ({k}, {h2}) | one output row per bucket: the first {h} weights read the side to move's half, the last {h} the other side's |
| `out_bias` | ({k},) | one output bias per bucket |

## The inputs

768 = 2 colours x 6 piece kinds x 64 squares, seen from each side in turn. Squares are numbered a1 = 0, b1 = 1,
... h1 = 7, a2 = 8, ... h8 = 63. Kinds are 1 pawn, 2 knight, 3 bishop, 4 rook, 5 queen, 6 king. For a piece of
colour c (0 white, 1 black) and kind k on square s, seen from side p (0 white, 1 black):

    input = ((0 if c == p else 6) + k - 1) * 64 + (s if p == 0 else s ^ 56)

So each side sees its own pieces in inputs 0-383 and the other side's in 384-767, with the board flipped
(s ^ 56 swaps rank 1 and rank 8) when the side is black. Pawns never stand on ranks 1 and 8, so those inputs
stay 0.

## The forward pass

    acc[p] = ft_bias + sum of ft_weight[input(piece, p)] over the pieces on the board      (for p = white, black)
    x      = concatenate(acc[side to move], acc[other side])                                 ({h2} numbers)
    x      = clip(x, 0, 1) ** 2                                                              (squared clipped ReLU)
    b      = (n - 1) * {k} // 32        with n the pieces on the board, kings included
    y      = out_weight[b] @ x + out_bias[b]
    score  = 400 * y   centipawns, for the side to move

As a win chance: 1 / (1 + 10 ** (-score / 400)). The buckets split 2-32 pieces evenly.

## What it was trained on, and how

- Positions from Lichess games with Lichess's own Stockfish analysis, scored by that analysis, {passes}
  passes. Only positions with 6 or more pieces (our engine's endgame tables handle the rest), so
  {filled_note}, and nothing below 6 pieces was ever trained.
- Mate scores left out. The loss: (s(score) - s(stockfish))^2 with s(x) = 1 / (1 + 10 ** (-x / 400)).
- AdamW, learning rate {lr}, times {gamma} after passes {drops}; batches of {batch:,}; weight decay
  {wd}. After every step, ft_weight, ft_bias and out_weight are clamped to +-{clamp} (for our 16-bit export).
- Held back (positions never trained on), the mean squared miss in win chance with the scale K fitted:
  **{float_err} at full precision** (a typical miss of {miss:.1f} points of win chance). Our engine's
  16-bit version of the same net: {int_err}.

## Checked

The forward pass above, in float, against our engine's 16-bit version of this net over {n_check} random
positions: mean difference {mean_diff:.1f} centipawns, largest {max_diff:.0f}. Rounding one layer at a time
in float, as our export rounds it: the output weights at steps of 1/{qb} cost {out_cost:.1f} centipawns on
average ({out_max:.0f} at most); the first layer at steps of 1/{qa}, {ft_cost:.1f} ({ft_max:.0f}). A wrong
reading of the layout would be off by hundreds, not by rounding's few.
"""


def forward(npz, fen):
    """The note's forward pass, float64: the score in centipawns for the side to move."""
    pieces, side = nnue_reference.parse_fen(fen)
    acc = [npz["ft_bias"].astype(np.float64) + npz["ft_weight"][
        [nnue_reference.feature(c, k, s, p) for c, k, s in pieces]].astype(np.float64).sum(axis=0) for p in (0, 1)]
    x = np.clip(np.concatenate((acc[side], acc[1 - side])), 0, 1) ** 2
    b = nnue_reference.bucket(len(pieces), len(npz["out_bias"]))
    return 400 * (npz["out_weight"][b].astype(np.float64) @ x + float(npz["out_bias"][b]))


def main(argv):
    if len(argv) != 2:
        print(__doc__)
        return 1
    import torch
    pt, out = pathlib.Path(argv[0]), pathlib.Path(argv[1])
    run = pt.stem
    saved = torch.load(pt, map_location="cpu", weights_only=False)
    state = {k: np.asarray(v) for k, v in saved["state"].items()}
    if not train.has_buckets(state):
        raise SystemExit(f"{pt}: not an --output-buckets checkpoint")
    ow, ob, filled = train.fill_untrained(state["ow"], state["ob"], saved["bucket_positions"])
    arrays = dict(ft_weight=state["fw"][:train.INPUTS], ft_bias=state["fb"], out_weight=ow, out_bias=ob)
    arrays = {k: np.asarray(v, np.float32) for k, v in arrays.items()}
    out.mkdir(parents=True, exist_ok=True)
    np.savez(out / f"{run}.npz", **arrays)
    npz = dict(np.load(out / f"{run}.npz"))

    net = nnue_reference.read_nnue(pt.with_suffix(".nnue"))
    rng = np.random.default_rng(20261024)
    fens = list(nnue_reference.KNOWN)
    while len(fens) < 1000:   # random_fen can place 33 pieces, which no bucket covers (as buckets_fixture skips)
        fen = nnue_reference.random_fen(rng)
        if len(nnue_reference.parse_fen(fen)[0]) <= nnue_reference.MAX_PIECES:
            fens.append(fen)
    base = np.array([forward(npz, f) for f in fens])
    diffs = np.abs(base - np.array([nnue_reference.evaluate(net, f) for f in fens]))
    qa, qb = net["qa"], net["qb"]
    rounded_out = dict(npz, out_weight=np.round(npz["out_weight"] * qb) / qb)
    rounded_ft = dict(npz, ft_weight=np.round(npz["ft_weight"] * qa) / qa, ft_bias=np.round(npz["ft_bias"] * qa) / qa)
    out_cost = np.abs(np.array([forward(rounded_out, f) for f in fens]) - base)
    ft_cost = np.abs(np.array([forward(rounded_ft, f) for f in fens]) - base)

    log = pt.with_suffix(".log").read_text(encoding="utf-8", errors="replace").splitlines()
    last = next(l for l in reversed(log) if " pass " in l and "held back" in l)
    float_err = float(last.split("held back ")[1].split(",")[1].split(" at K ")[0])
    int_line = next((l for l in reversed(log) if "as the engine computes it" in l), "")
    int_err = int_line.split(", ")[-1] if int_line else "(not in the log)"
    args = saved.get("args", {})
    months = ", ".join(sorted([pathlib.Path(args["data"]).name] + list(args.get("add_data") or []))) \
        if "data" in args else "see its log"
    h, k = arrays["ft_bias"].shape[0], arrays["out_bias"].shape[0]
    filled_note = "; ".join(f"bucket {b} copies bucket {near}'s row" for b, near in filled) or "every bucket was trained"
    (out / f"{run}-layout.md").write_text(NOTE.format(
        run=run, h=h, h2=2 * h, k=k, months=months, passes=args.get("passes", "?"), filled_note=filled_note,
        lr=args.get("lr", "?"), gamma=args.get("lr_gamma", "?"), drops=str(args.get("lr_drops", "?")).replace("/", ", "),
        batch=args.get("batch", 0), wd=args.get("weight_decay", "?"), clamp=train.CLAMP,
        float_err=f"{float_err:.6f}", miss=100 * float_err ** 0.5, int_err=int_err, n_check=len(fens),
        mean_diff=diffs.mean(), max_diff=diffs.max(), qa=qa, qb=qb, out_cost=out_cost.mean(), out_max=out_cost.max(),
        ft_cost=ft_cost.mean(), ft_max=ft_cost.max()), encoding="utf-8")
    for name, a in arrays.items():
        print(f"{name:11} {str(a.shape):12} min {a.min():+.4f} max {a.max():+.4f}")
    print(f"filled: {filled}; held back {float_err:.6f} float, {int_err} int16")
    print(f"float against the .nnue over {len(fens)} positions: mean {diffs.mean():.2f} cp, max {diffs.max():.0f} cp; "
          f"rounding alone, output {out_cost.mean():.2f} cp, first layer {ft_cost.mean():.2f} cp")
    print(f"wrote {out / (run + '.npz')} and {out / (run + '-layout.md')}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
