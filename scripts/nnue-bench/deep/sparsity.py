"""How many of the first small layer's inputs x = (v v + 127) / 255 are zero on real positions, for a .nnue's feature
layer (version 1 or 2): held-back records of September, from the side to move's view and the other's. Pairs too,
since the sparse pass would take inputs two at a time. Usage: sparsity.py <net.nnue> [positions]"""
import pathlib
import sys

import numpy as np

repo = pathlib.Path(__file__).resolve().parents[3]   # the checkout this script is in
sys.path.insert(0, str(repo / "scripts" / "nnue"))
sys.path.insert(0, str(repo / "scripts" / "nnue-reference"))
import nnue_reference  # noqa: E402
import records  # noqa: E402

net = nnue_reference.read_nnue(sys.argv[1])
n = int(sys.argv[2]) if len(sys.argv) > 2 else 2000
held = records.read(str(records.held_back_file(r"C:\Users\Admin\Desktop\-=Programming=-\Chess Bruteforcer\games\nnue\2026-09")))
rng = np.random.default_rng(1)
sample = held[rng.choice(len(held), n, replace=False)]
zeros = pairs = total = 0
per_position = []
for r in sample:
    pieces, side = nnue_reference.parse_fen(records.to_fen(r))
    v = np.concatenate([np.clip(nnue_reference.accumulator(net, pieces, p), 0, net["qa"]) for p in (side, 1 - side)])
    x = (v.astype(np.int64) ** 2 + net["qa"] // 2) // net["qa"]
    nz = x != 0
    zeros += int((~nz).sum())
    pair_nz = nz[0::2] | nz[1::2]
    pairs += int(pair_nz.sum())
    total += len(x)
    per_position.append(int(nz.sum()))
h = net["hidden"]
print(f"{n} positions, {2 * h} inputs each: {zeros / total:.1%} zero; non-zero inputs a position: median "
      f"{int(np.median(per_position))}, 90th {int(np.percentile(per_position, 90))}; non-zero pairs {pairs / (total / 2):.1%} "
      f"(~{pairs / n:.0f} of {h} a position)")
