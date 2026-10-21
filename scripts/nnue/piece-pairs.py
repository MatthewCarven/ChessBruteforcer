"""Piece pairs in September's held-back positions: what Lichess's Stockfish says owning both bishops, both knights,
both rooks, and all three pairs at once is worth at equal material and equal stage, and whether a net and sf1 lean
on it (their score minus Stockfish's). Analysis only; scripts/piece-pairs-2026-09.md is the report.

    python scripts/nnue/piece-pairs.py features <work dir>            counts, pair flags and sf1's scores (numpy)
    python scripts/nnue/piece-pairs.py net <work dir> <.nnue> <name>  a net's scores as the engine computes them (GPU)
    python scripts/nnue/piece-pairs.py report <work dir> <name> [--replicates 400] [--check-se 0]

Run with scripts/nnue/.venv's python. The positions are train.py's held-back set (games\\nnue\\2026-09\\held-back.bin
in the main checkout), mates left out, as the held-back error leaves them out. The report goes to
<work dir>/report-<name>.md as well as the screen.

Two ways to the worth of a pair, both from the side with the pair's point of view:
  (a) like for like: positions where the minor pieces differ in one way only (two bishops against bishop and knight,
      say) and pawns, rooks and queens are equal, so material and stage are equal by construction. The worth is the
      mean of s(cp) over them, both sides to move weighted alike, turned back into centipawns.
  (b) a regression: Stockfish's score on each side's piece counts (white minus black, each tapered by phase as sf1
      is), white's edge and the tempo (tapered too), and one term per pair (white has it minus black has it) and one
      for all three. Fitted on the squashed scale as the tuner fits (least squares of s(model) against s(cp), so the
      terms come out in centipawns), and by plain least squares on centipawns held to +-1000 as a check. Over all
      positions, and over those at near-equal material (1-3-3-5-9 counts within a pawn), where holding the score to
      +-1000 hardly bites.
A scorer's lean is its term minus Stockfish's, fitted on the same positions: at K 1 (its scores as the engine uses
them) and at its fitted K (its scores times K, so an overall shrink towards 0 doesn't count as a lean).
Intervals are 95%: (a) by bootstrap over games (Poisson weights per game), (b) by the sandwich clustered by game,
since the positions of one game are not independent (--check-se N refits Stockfish's N times by bootstrap to check it).
"""
import argparse
import math
import os
import pathlib
import sys
import time

for _name in ("OPENBLAS_NUM_THREADS", "OMP_NUM_THREADS", "MKL_NUM_THREADS"):
    os.environ.setdefault(_name, "4")

import numpy as np  # noqa: E402

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import records  # noqa: E402
import train  # noqa: E402

CHUNK = 1 << 18
KINDS = ["pawn", "knight", "bishop", "rook", "queen"]       # counts[:, side, kind - 1]
VALUES = np.array([1, 3, 3, 5, 9])                           # for "near-equal material" only
PAIRS = [("bishop pair", 2), ("knight pair", 1), ("rook pair", 3)]   # (name, index into KINDS)
TERMS = [p for p, _ in PAIRS] + ["all three"]
STAGES = [("phase 20-24", 20, 24), ("phase 12-19", 12, 19), ("phase 0-11", 0, 11)]
LN10 = math.log(10)
SLOPE0 = LN10 / 1600                                          # ds/dcp at 0: 1 cp is 0.00144 of s there
CLIP = 1000.0
MIN_ROWS = 300                                                # a term needs this many positions to be fitted


def s(x):
    return 1 / (1 + np.power(10.0, -np.asarray(x, np.float64) / 400))


def cp_of(m):
    """The centipawns whose s is m."""
    return 400 * np.log10(m / (1 - m))


# ---- features and scores ----

def features(work):
    held = train.held_back_records(train.DATA)
    n = len(held)
    counts = np.zeros((n, 2, 5), np.uint8)
    same_colour = np.zeros((n, 2), bool)     # two or more bishops, all on squares of one colour
    sf1 = np.zeros(n, np.int32)
    numbers = train.load_numbers(train.NUMBERS[0])
    sq = np.arange(64)
    light = (((sq >> 3) + (sq & 7)) & 1) == 1
    t0 = time.time()
    for at in range(0, n, CHUNK):
        b = records.boards(held[at:at + CHUNK])
        part = slice(at, at + len(b))
        for side in (0, 1):
            for kind in range(1, 6):
                counts[part, side, kind - 1] = (b == side * 8 + kind).sum(1)
            bishops = b == side * 8 + 3
            on_light, on_dark = (bishops & light).sum(1), (bishops & ~light).sum(1)
            same_colour[part, side] = (on_light + on_dark >= 2) & ((on_light == 0) | (on_dark == 0))
        sf1[part] = train.numbers_eval(b, numbers)
        print(f"  {at + len(b):,} of {n:,} ({time.time() - t0:.0f} s)", flush=True)
    np.savez(work / "features.npz", game=held["game"], btm=records.black_to_move(held), sf=held["score"],
             result=held["result"], counts=counts, same_colour=same_colour, sf1=sf1)
    print(f"wrote {work / 'features.npz'}: {n:,} positions from {len(np.unique(held['game'])):,} games")


def net(work, path, name):
    import torch
    held = train.held_back_records(train.DATA)
    raw = torch.from_numpy(np.ascontiguousarray(held).view(np.uint8).reshape(-1, records.SIZE)).cuda()
    e = train.int_scores(path, raw)
    target = train.s10(torch.from_numpy(train.side_to_move_score(held)).cuda().to(torch.float32))
    k, err = train.fit_k(e, target)
    print(f"{path}: held back {train.error_at(e, target, 1.0):.6f} at K 1, {err:.6f} at K {k:.4f}")
    e = e.cpu().numpy().astype(np.int32)
    white = np.where(records.black_to_move(held), -e, e).astype(np.int16)
    np.save(work / f"net-{name}.npy", white)
    print(f"wrote {work / f'net-{name}.npy'}: {len(white):,} scores, white's view")


# ---- the data, as the report reads it ----

class Data:
    def __init__(self, work, name):
        f = np.load(work / "features.npz")
        self.game = f["game"]
        self.btm = f["btm"]
        c = f["counts"].astype(np.int16)
        self.c = c
        self.n = len(c)
        self.same_colour = f["same_colour"]
        _, self.g = np.unique(self.game, return_inverse=True)
        self.wtm = np.where(self.btm, -1.0, 1.0)
        self.pair = np.stack([c[:, :, k] >= 2 for _, k in PAIRS], 2)          # (n, side, pair)
        self.all3 = self.pair.all(2)                                          # (n, side)
        phase = (c[:, :, 1] + c[:, :, 2] + 2 * c[:, :, 3] + 4 * c[:, :, 4]).sum(1)
        self.phase = np.minimum(phase, train.MAX_PHASE)
        self.mg = self.phase / train.MAX_PHASE
        self.eg = 1 - self.mg
        self.d = (c[:, 0, :] - c[:, 1, :]).astype(np.float64)
        self.near = np.abs(self.d @ VALUES) <= 1
        self.dterm = np.column_stack([self.pair[:, 0, j].astype(np.int8) - self.pair[:, 1, j]
                                      for j in range(len(PAIRS))]
                                     + [self.all3[:, 0].astype(np.int8) - self.all3[:, 1]]).astype(np.float64)
        self.scores = {"Stockfish": f["sf"].astype(np.float64), "sf1": f["sf1"].astype(np.float64)}
        if name:
            v = np.load(work / f"net-{name}.npy").astype(np.float64)
            if len(v) != self.n:
                raise ValueError(f"net-{name}.npy has {len(v):,} scores for {self.n:,} positions")
            self.scores[name] = v


class Rows:
    """The positions a fit runs on, with their games numbered 0..G-1 for the clustering."""

    def __init__(self, D, mask=None):
        self.idx = np.arange(D.n) if mask is None else np.nonzero(mask)[0]
        _, self.g = np.unique(D.g[self.idx], return_inverse=True)
        self.G = int(self.g.max()) + 1


def design(D, R, stages=False):
    """The regression's columns on these rows: (X, names). Pair terms are flat, or one per stage with stages."""
    i = R.idx
    mg, eg = D.mg[i], D.eg[i]
    cols, names = [], []

    def add(name, v):
        if np.count_nonzero(v) >= MIN_ROWS:
            names.append(name)
            cols.append(v)

    add("white's edge (mg)", mg)
    add("white's edge (eg)", eg)
    add("tempo (mg)", D.wtm[i] * mg)
    add("tempo (eg)", D.wtm[i] * eg)
    for k, kind in enumerate(KINDS):
        add(f"{kind} (mg)", D.d[i, k] * mg)
        add(f"{kind} (eg)", D.d[i, k] * eg)
    for j, term in enumerate(TERMS):
        v = D.dterm[i, j]
        if stages:
            for label, lo, hi in STAGES:
                add(f"{term}, {label}", v * ((D.phase[i] >= lo) & (D.phase[i] <= hi)))
        else:
            add(term, v)
    return np.column_stack(cols), names


# ---- fits, with influence by game for the clustered sandwich ----

def gram(X, d=None):
    A = np.zeros((X.shape[1], X.shape[1]))
    for at in range(0, len(X), 1 << 20):
        x = X[at:at + (1 << 20)]
        if d is not None:
            x = x * d[at:at + (1 << 20), None]
        A += x.T @ x
    return A


def by_game(R, X, e):
    """Per game, the sum of X_i e_i: (G, p)."""
    U = np.empty((R.G, X.shape[1]))
    for j in range(X.shape[1]):
        U[:, j] = np.bincount(R.g, weights=X[:, j] * e, minlength=R.G)
    return U


def ols(R, X, y):
    """(beta, influence (p, G)): beta's variance is influence @ influence.T, clustered by game."""
    A = gram(X)
    beta = np.linalg.solve(A, X.T @ y)
    return beta, np.linalg.inv(A) @ by_game(R, X, y - X @ beta).T


def texel(R, X, y, beta0, w=None, iters=40):
    """Least squares of s(X beta) against s(y), Gauss-Newton with halving: (beta, influence (p, G))."""
    t = s(y)
    w = np.ones(len(y)) if w is None else w
    beta = beta0.copy()

    def loss_of(b):
        return float((w * (s(X @ b) - t) ** 2).sum() / w.sum())

    loss = loss_of(beta)
    for _ in range(iters):
        p = s(X @ beta)
        dz = p * (1 - p) * (LN10 / 400)
        step = np.linalg.solve(gram(X, dz * np.sqrt(w)), X.T @ (w * dz * (t - p)))
        lam = 1.0
        while True:
            nb = beta + lam * step
            nl = loss_of(nb)
            if nl <= loss or lam < 1e-3:
                break
            lam /= 2
        beta, loss = nb, nl
        if np.abs(lam * step).max() < 1e-3:
            break
    p = s(X @ beta)
    dz = p * (1 - p) * (LN10 / 400)
    A = gram(X, dz * np.sqrt(w))
    return beta, np.linalg.inv(A) @ by_game(R, X, w * dz * (t - p)).T


def fit_all(D, R, X, K):
    """Every fit the tables need on these rows and columns."""
    sf = D.scores["Stockfish"][R.idx]
    o = ols(R, X, np.clip(sf, -CLIP, CLIP))
    out = {"Stockfish": dict(t=texel(R, X, sf, o[0]), o=o)}
    for nm in list(D.scores)[1:]:
        y = D.scores[nm][R.idx]
        t1 = texel(R, X, y, out["Stockfish"]["t"][0])
        out[nm] = dict(t1=t1, tk=texel(R, X, y * K[nm], t1[0]), o=ols(R, X, np.clip(y * K[nm], -CLIP, CLIP)),
                       e=ols(R, X, s(y * K[nm]) - s(sf)))
    return out


def est(fit, c, minus=None):
    """c . beta (minus another fit's) with its 95% interval, from the clustered influence: (value, low, high, se)."""
    beta, infl = fit
    if minus is not None:
        beta, infl = beta - minus[0], infl - minus[1]
    v = float(c @ beta)
    se = float(np.sqrt(((c @ infl) ** 2).sum()))
    return v, v - 1.96 * se, v + 1.96 * se, se


def fmt(v, lo, hi, *_):
    return f"{v:+.0f} [{lo:+.0f}, {hi:+.0f}]"


def contrasts(names):
    """(label, vector) for each pair, the all-three term, and all three pairs at once (the sum), where fitted."""
    out = []
    for term in TERMS:
        if term in names:
            c = np.zeros(len(names))
            c[names.index(term)] = 1
            out.append((term if term != "all three" else "all three, beyond the three pairs", c))
    if all(t in names for t in TERMS):
        c = np.zeros(len(names))
        c[[names.index(t) for t in TERMS]] = 1
        out.append(("all three pairs at once (the sum)", c))
    return out


# ---- like for like ----

LIKE = [  # (label, the pair side's minors (bishops, knights), the other side's)
    ("BB v BN", (2, 0), (1, 1)),
    ("NN v BN", (0, 2), (1, 1)),
    ("BB v NN", (2, 0), (0, 2)),
    ("BBN v BNN", (2, 1), (1, 2)),
    ("B v N", (1, 0), (0, 1)),
]
KEYS = 9 * 4 * 3 * 2


def like_subset(D, mine, theirs):
    """Positions where one side's minors are `mine` and the other's `theirs`, pawns, rooks and queens equal: their
    indices, +1 where white is the `mine` side (-1 black), and a stratum key (the other material, who is to move)."""
    c = D.c
    equal = (c[:, 0, 0] == c[:, 1, 0]) & (c[:, 0, 3] == c[:, 1, 3]) & (c[:, 0, 4] == c[:, 1, 4])

    def has(side, cfg):
        return (c[:, side, 2] == cfg[0]) & (c[:, side, 1] == cfg[1])

    w_mine = equal & has(0, mine) & has(1, theirs)
    b_mine = equal & has(1, mine) & has(0, theirs)
    idx = np.nonzero(w_mine | b_mine)[0]
    sign = np.where(w_mine[idx], 1.0, -1.0)
    mine_to_move = (D.wtm[idx] * sign) > 0
    pawns = np.minimum(c[idx, 0, 0], 8)
    rooks = np.minimum(c[idx, 0, 3], 3)
    queens = np.minimum(c[idx, 0, 4], 2)
    key = ((pawns * 4 + rooks) * 3 + queens) * 2 + mine_to_move
    return idx, sign, key.astype(np.int64)


def target_of(key, w):
    """A subset's own spread over the other material, each with both sides to move weighted alike."""
    sig = np.bincount(key // 2, weights=w, minlength=KEYS // 2)
    return np.repeat(sig / 2, 2)


def strat_m(vals, key, w, target):
    num = np.bincount(key, weights=w * vals, minlength=KEYS)
    den = np.bincount(key, weights=w, minlength=KEYS)
    ok = (den > 0) & (target > 0)
    return float((num[ok] / den[ok] * target[ok]).sum() / target[ok].sum())


# ---- the report ----

def report(work, name, replicates, check_se):
    t0 = time.time()
    D = Data(work, name)
    names = list(D.scores)
    scorers = names[1:]
    out = []

    def say(line=""):
        print(line, flush=True)
        out.append(line)

    def progress(text):
        print(f"  {text} ({time.time() - t0:.0f} s)", flush=True)

    say(f"# piece-pairs: {D.n:,} positions from {int(D.g.max()) + 1:,} games, {time.strftime('%Y-%m-%d %H:%M')}")
    say(f"two same-coloured bishops on one side: {int(D.same_colour.any(1).sum()):,} positions (they'd count as a pair)")
    say(f"three or more: bishops {int((D.c[:, :, 2] >= 3).any(1).sum()):,}, knights "
        f"{int((D.c[:, :, 1] >= 3).any(1).sum()):,}, rooks {int((D.c[:, :, 3] >= 3).any(1).sum()):,} positions")
    say(f"near-equal material (1-3-3-5-9 within a pawn): {int(D.near.sum()):,} positions ({D.near.mean():.1%})")

    target = s(D.scores["Stockfish"])
    K = {"Stockfish": 1.0}
    for nm in scorers:
        k, err = train.fit_k(D.scores[nm], target)
        K[nm] = k
        say(f"{nm}: held-back error {train.error_at(D.scores[nm], target, 1.0):.6f} at K 1, {err:.6f} at K {k:.4f}")

    say()
    say("## Who holds the pairs")
    say()
    say("| | white has it | black has it | both | neither | one side only: positions | games | mean phase | "
        "of them at near-equal material |")
    say("|---|---|---|---|---|---|---|---|---|")
    flags = [(p, D.pair[:, :, j]) for j, (p, _) in enumerate(PAIRS)] + [("all three", D.all3)]
    for p, fl in flags:
        w_, b_ = fl[:, 0], fl[:, 1]
        one = w_ != b_
        say(f"| {p} | {w_.mean():.1%} | {b_.mean():.1%} | {(w_ & b_).mean():.1%} | {(~w_ & ~b_).mean():.1%} | "
            f"{int(one.sum()):,} | {len(np.unique(D.g[one])):,} | {D.phase[one].mean():.1f} | "
            f"{int((one & D.near).sum()):,} |")
    say()
    say("one side only, by stage (positions):")
    for label, lo, hi in STAGES:
        inb = (D.phase >= lo) & (D.phase <= hi)
        say(f"  {label}: {int(inb.sum()):,} positions; " + ", ".join(
            f"{p} {int(((fl[:, 0] != fl[:, 1]) & inb).sum()):,}" for p, fl in flags))
    say()
    say("what else differs where only one side has it: the commonest material differences from that side's view "
        "(P N B R Q, + = it has more), all positions | near-equal material:")
    for j, term in enumerate(TERMS):
        one = D.dterm[:, j] != 0
        parts = []
        for mask in (one, one & D.near):
            diff = (D.d[mask] * D.dterm[mask, j][:, None]).astype(np.int16)
            kinds, counts = np.unique(diff, axis=0, return_counts=True)
            top = np.argsort(-counts)[:4]
            parts.append(", ".join(
                (" ".join(f"{'PNBRQ'[k]}{v:+d}" for k, v in enumerate(kinds[t]) if v) or "equal")
                + f" {counts[t] / counts.sum():.0%}" for t in top))
        say(f"  {term}: {parts[0]} | {parts[1]}")
    corr = np.corrcoef(D.dterm.T)
    say()
    say("correlation of the white-minus-black terms (bishop pair, knight pair, rook pair, all three):")
    for row in corr:
        say("  " + "  ".join(f"{v:+.2f}" for v in row))

    # (b) the regressions: all positions, near-equal material, and by stage
    sets = {}
    for label, mask, stages in (("all", None, False), ("near", D.near, False), ("stages", None, True)):
        R = Rows(D, mask)
        X, xn = design(D, R, stages)
        progress(f"fits on {label}: {len(R.idx):,} positions, {X.shape[1]} terms")
        sets[label] = dict(R=R, names=xn, fits=fit_all(D, R, X, K))
        if label == "all":
            bsf = sets[label]["fits"]["Stockfish"]["t"][0]
            minor_cols = [j for j, n_ in enumerate(xn) if n_.startswith(("knight", "bishop")) or n_ in TERMS]
            implied = {}
            for lab, mine, theirs in LIKE:
                idx, sign, _ = like_subset(D, mine, theirs)
                implied[lab] = float((sign * (X[np.searchsorted(R.idx, idx)][:, minor_cols] @ bsf[minor_cols])).mean())
            if check_se:
                bs = []
                rng = np.random.default_rng(1)
                for r in range(check_se):
                    wg = rng.poisson(1.0, R.G).astype(np.float64)
                    bs.append(texel(R, X, D.scores["Stockfish"], bsf, w=wg[R.g])[0])
                    progress(f"bootstrap refit {r + 1}/{check_se}")
                sets[label]["boot"] = np.array(bs)
        del X

    A, N_, S_ = sets["all"], sets["near"], sets["stages"]
    say()
    say("## (b) Every term of the regression over all positions: Stockfish (cp inside s) [95%], the same model fitted "
        "to the others' scores at K 1, and Stockfish by least squares on cp held to +-1000")
    say()
    say("| term | Stockfish | " + " | ".join(scorers) + " | Stockfish, cp held |")
    say("|---|---|" + "---|" * len(scorers) + "---|")
    for j, xn in enumerate(A["names"]):
        c = np.zeros(len(A["names"]))
        c[j] = 1
        row = [fmt(*est(A["fits"]["Stockfish"]["t"], c))] + [f"{A['fits'][nm]['t1'][0][j]:+.0f}" for nm in scorers]
        row.append(fmt(*est(A["fits"]["Stockfish"]["o"], c)))
        say(f"| {xn} | " + " | ".join(row) + " |")

    say()
    say("## (b) Stockfish's worth of each pair, cp [95%]")
    say()
    say("| | all positions, squashed fit | near-equal material, squashed fit | near-equal material, cp held to "
        "+-1000 | all positions, cp held to +-1000 |")
    say("|---|---|---|---|---|")
    cn = dict(contrasts(N_["names"]))
    for label, c in contrasts(A["names"]):
        row = [fmt(*est(A["fits"]["Stockfish"]["t"], c))]
        if label in cn:
            row += [fmt(*est(N_["fits"]["Stockfish"]["t"], cn[label])), fmt(*est(N_["fits"]["Stockfish"]["o"], cn[label]))]
        else:
            row += ["too few", "too few"]
        row.append(fmt(*est(A["fits"]["Stockfish"]["o"], c)))
        say(f"| {label} | " + " | ".join(row) + " |")
    if "boot" in A:
        say()
        say(f"the sandwich's standard errors against {len(A['boot'])} bootstrap refits over games (all positions):")
        for label, c in contrasts(A["names"]):
            say(f"  {label}: sandwich {est(A['fits']['Stockfish']['t'], c)[3]:.2f} cp, bootstrap "
                f"{np.std(A['boot'] @ c, ddof=1):.2f} cp")

    for nm in scorers:
        say()
        say(f"## (b) {nm}'s lean (its term minus Stockfish's), cp [95%]; K {K[nm]:.4f}")
        say()
        say("| | its term at K 1 | lean at K 1 | lean at K | near-equal material: lean at K | near-equal, cp held "
            "to +-1000: lean at K | the squashed error at K, x 1000 (cp at an even score) |")
        say("|---|---|---|---|---|---|---|")
        sfa, f = A["fits"]["Stockfish"], A["fits"][nm]
        for label, c in contrasts(A["names"]):
            v, lo, hi, _ = est(f["e"], c)
            row = [f"{float(c @ f['t1'][0]):+.0f}", fmt(*est(f["t1"], c, sfa["t"])), fmt(*est(f["tk"], c, sfa["t"]))]
            if label in cn:
                fn, sfn = N_["fits"][nm], N_["fits"]["Stockfish"]
                row += [fmt(*est(fn["tk"], cn[label], sfn["t"])), fmt(*est(fn["o"], cn[label], sfn["o"]))]
            else:
                row += ["too few", "too few"]
            row.append(f"{1000 * v:+.2f} [{1000 * lo:+.2f}, {1000 * hi:+.2f}] ({v / SLOPE0:+.0f})")
            say(f"| {label} | " + " | ".join(row) + " |")

    say()
    say("## (b) By stage: each term fitted separately in each band of phase (24 = every piece on), cp [95%]; leans at K")
    say()
    say("| term | positions, one side only | Stockfish | " + " | ".join(f"{nm}'s lean" for nm in scorers) + " |")
    say("|---|---|---|" + "---|" * len(scorers))
    for j, xn in enumerate(S_["names"]):
        if not xn.startswith(tuple(TERMS)):
            continue
        c = np.zeros(len(S_["names"]))
        c[j] = 1
        term = next(t for t in TERMS if xn.startswith(t))
        lo, hi = next((lo, hi) for lab, lo, hi in STAGES if xn.endswith(lab))
        cnt = int(((D.dterm[:, TERMS.index(term)] != 0) & (D.phase >= lo) & (D.phase <= hi)).sum())
        sfs = S_["fits"]["Stockfish"]["t"]
        row = [f"{cnt:,}", fmt(*est(sfs, c))] + [fmt(*est(S_["fits"][nm]["tk"], c, sfs)) for nm in scorers]
        say(f"| {xn} | " + " | ".join(row) + " |")

    # (a) like for like
    say()
    say(f"## (a) Like for like (pawns, rooks, queens equal; {replicates} bootstrap replicates over games)")
    subsets = {lab: like_subset(D, mine, theirs) for lab, mine, theirs in LIKE}
    rng = np.random.default_rng(20261021)
    reps = {}

    def estimates(wg):
        res = {}
        for lab, (idx, sign, key) in subsets.items():
            w = np.ones(len(idx)) if wg is None else wg[D.g[idx]].astype(np.float64)
            tgt = target_of(key, w)
            for nm in names:
                res[(lab, nm)] = cp_of(strat_m(s(sign * D.scores[nm][idx] * K[nm]), key, w, tgt))
            if lab in ("BB v BN", "NN v BN"):
                ridx, rsign, rkey = subsets["B v N"]
                rw = np.ones(len(ridx)) if wg is None else wg[D.g[ridx]].astype(np.float64)
                for nm in names:
                    res[(lab, nm, "ref")] = cp_of(strat_m(s(rsign * D.scores[nm][ridx] * K[nm]), rkey, rw, tgt))
        return res

    point = estimates(None)
    for r in range(replicates):
        for k, v in estimates(rng.poisson(1.0, int(D.g.max()) + 1)).items():
            reps.setdefault(k, []).append(v)
        if (r + 1) % 100 == 0:
            progress(f"bootstrap {r + 1}/{replicates}")
    replicate_dicts = [dict(zip(reps, col)) for col in zip(*reps.values())]

    def ci(f):
        """f of the point estimates, with the 2.5th and 97.5th percentiles of f over the replicates."""
        vals = np.array([f(d) for d in replicate_dicts])
        return f(point), np.percentile(vals, 2.5), np.percentile(vals, 97.5)

    say()
    say("Worth from the pair side's view, cp (the mean of s over the positions, both sides to move alike, back into "
        "cp); scorers at their K. The regression's figure is its minor-piece and pair terms on the same positions.")
    say()
    say("| | positions | games | mean phase | Stockfish | regression's figure | "
        + " | ".join(f"{nm}'s lean" for nm in scorers) + " |")
    say("|---|---|---|---|---|---|" + "---|" * len(scorers))
    for lab, (idx, sign, key) in subsets.items():
        row = [f"{len(idx):,}", f"{len(np.unique(D.g[idx])):,}", f"{D.phase[idx].mean():.1f}",
               fmt(*ci(lambda d, lab=lab: d[(lab, "Stockfish")])), f"{implied[lab]:+.0f}"]
        row += [fmt(*ci(lambda d, lab=lab, nm=nm: d[(lab, nm)] - d[(lab, "Stockfish")])) for nm in scorers]
        say(f"| {lab} | " + " | ".join(row) + " |")
    say()
    say("Less the bishop-against-knight difference (B v N, reweighted to the same pawns, rooks and queens): what is "
        "left is the pair's own worth, + favouring the pair.")
    say()
    say("| | Stockfish | " + " | ".join(f"{nm}'s lean" for nm in scorers) + " |")
    say("|---|---|" + "---|" * len(scorers))
    for lab, sgn, what in (("BB v BN", 1, "bishop pair: (BB v BN) - (B v N)"),
                           ("NN v BN", -1, "knight pair: (NN v BN) + (B v N)")):
        def alone(d, lab=lab, sgn=sgn, nm="Stockfish"):
            return d[(lab, nm)] - sgn * d[(lab, nm, "ref")]
        row = [fmt(*ci(alone))] + [fmt(*ci(lambda d, nm=nm, alone=alone: alone(d, nm=nm) - alone(d)))
                                   for nm in scorers]
        say(f"| {what} | " + " | ".join(row) + " |")

    say()
    say(f"({time.time() - t0:.0f} s)")
    (work / f"report-{name}.md").write_text("\n".join(out) + "\n", encoding="utf-8", newline="\n")


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="command", required=True)
    f = sub.add_parser("features")
    f.add_argument("work", type=pathlib.Path)
    n = sub.add_parser("net")
    n.add_argument("work", type=pathlib.Path)
    n.add_argument("nnue")
    n.add_argument("name")
    r = sub.add_parser("report")
    r.add_argument("work", type=pathlib.Path)
    r.add_argument("name", nargs="?", default="")
    r.add_argument("--replicates", type=int, default=400)
    r.add_argument("--check-se", type=int, default=0)
    args = ap.parse_args(argv)
    args.work.mkdir(parents=True, exist_ok=True)
    if args.command == "features":
        features(args.work)
    elif args.command == "net":
        net(args.work, args.nnue, args.name)
    else:
        report(args.work, args.name, args.replicates, args.check_se)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
