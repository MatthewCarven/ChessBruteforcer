"""Is net B's held-back error really lower than net A's? Both scored as the engine computes them (train.int_scores) on
September's 4.47 M held-back positions, each at its own fitted K; then the per-position difference of squared errors:
its mean, standard error, and the same on each quarter of the positions and on every 10th game-ish slice.
Usage: paired-error.py <A.nnue> <B.nnue>"""
import pathlib
import sys

import numpy as np

repo = pathlib.Path(__file__).resolve().parents[3]   # the checkout this script is in
sys.path.insert(0, str(repo / "scripts" / "nnue"))
import torch  # noqa: E402
import train  # noqa: E402
import records  # noqa: E402

h = train.held_back_records(train.DATA)
held = torch.from_numpy(np.ascontiguousarray(h).view(np.uint8).reshape(-1, records.SIZE)).cuda()
target = train.s10(torch.from_numpy(train.side_to_move_score(h)).cuda().to(torch.float32))
per = []
for path in sys.argv[1:3]:
    e = train.int_scores(path, held)
    k, err = train.fit_k(e, target)
    s = 1 / (1 + 10 ** (-(k * e.double()) / 400))
    per.append(((s - target.double()) ** 2).cpu().numpy())
    print(f"{pathlib.Path(path).name}: {err:.6f} at K {k:.4f}")
d = per[1] - per[0]
n = len(d)
se = d.std(ddof=1) / np.sqrt(n)
print(f"{n:,} positions: B - A = {d.mean():+.6f} (standard error {se:.6f}, {abs(d.mean()) / se:.0f} standard errors); "
      f"B better on {(d < 0).mean():.1%} of positions, worse on {(d > 0).mean():.1%}")
for q in range(4):
    part = d[q * n // 4:(q + 1) * n // 4]
    print(f"  quarter {q + 1}: {part.mean():+.6f} ({part.mean() / per[0][q * n // 4:(q + 1) * n // 4].mean():+.1%})")
