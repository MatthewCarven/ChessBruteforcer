"""What would choosing the net per opponent cost or gain?  P8's simulation (OPPONENTS.md).

    python -I simulate.py [--reps N] [--quick]

A world like the bot's: a pool of bots drawn at random by matchmaking (pool size and popularity fitted to how often
the bot has met each opponent so far), each with its own rating gap to us, and three setups ("arms") whose overall
strengths are the measured ones. Each bot also gets an arm-specific part c (N(0, sigma^2)): the "this bot is confused by
that net" effect P8 hopes for; the data put sigma at 0 with a 95% interval up to 100 Elo (analyze.py). A common part
b (N(0, tau^2), tau 80: the bot is weaker or stronger than its rating against all our setups) is in the world too,
since a learner must not mistake it for an arm effect.

Rules compared, each playing the same games (same opponents, colours and random numbers):
  best      always the best setup (today's: h1024-3m)
  oracle    the setup truly best against this bot (knows c: the ceiling, not a rule)
  matthew-E Matthew's rule: the least-used setup against this bot for its first E games, then the one with the best
            score against it so far (a loss pulls a setup down; ties go to the stronger setup)
  greedy-s  the setup with the best expected result after what we've seen (a 4-number Gaussian belief a bot: b and
            c for each arm, prior SDs tau and s, updated after each game), no deliberate exploration
  thompson-s the same belief, but a setup is picked by a draw from it (Thompson sampling)
  steerE-50 the best setup, another one at random in E% of games, and a switch to another for this bot once the belief
            (prior SD 50) puts it ahead of the best with 95% probability (Matthew's steer, 2026-10-22)
Reported per rule: Elo played (the mean, over games, of the chosen setup's true edge minus the best setup's: what the
rating would feel) and score points per 100 games against "best".
"""
import math
import sys

import numpy as np
from scipy.special import ndtr

args = sys.argv[1:]
REPS = int(args[args.index("--reps") + 1]) if "--reps" in args else 300
QUICK = "--quick" in args
STEER = (5, 10)                       # the steer rules: per cent of games given to another setup
LN10_400 = math.log(10) / 400
D, A, TAU = 88.0, 48.0, 80.0          # analyze.py: draw width, half the white edge, the opponent SD
OBS_HIST = {1: 67, 2: 37, 3: 21, 4: 24, 5: 13, 6: 15, 7: 5, 8: 6, 9: 4, 10: 1, 12: 1}   # 596 games, 194 bots


def f(x):
    return 1.0 / (1.0 + np.exp(-x * LN10_400))


def outcome_probs(d):
    pw, pl = f(d - D), f(-d - D)
    return pw, 1.0 - pw - pl, pl


def expect(d):
    pw, pd, _ = outcome_probs(d)
    return pw + 0.5 * pd


def slope_var(d):
    """dE/dd and the score variance at edge d."""
    pw, pd, pl = outcome_probs(d)
    e = pw + 0.5 * pd
    h = 1e-2
    return (expect(d + h) - expect(d - h)) / (2 * h), pw + 0.25 * pd - e * e


def pool_hist(rng, size, shape, games, reps=40):
    out = []
    for _ in range(reps):
        w = rng.gamma(shape, size=size)
        draws = rng.choice(size, size=games, p=w / w.sum())
        cnt = np.bincount(draws, minlength=size)
        cnt = cnt[cnt > 0]
        out.append((len(cnt), (cnt == 1).sum(), cnt.max()))
    return np.mean(out, axis=0)


def calibrate(rng):
    """Pool size and popularity spread that give the bot's own spread of games per opponent."""
    best = None
    for size in (250, 300, 400, 500, 700):
        for shape in (0.3, 0.5, 0.8, 1.2, 2.0):
            distinct, singles, mx = pool_hist(rng, size, shape, 596)
            err = ((distinct - 194) / 194) ** 2 + ((singles - 67) / 67) ** 2 + 0.25 * ((mx - 12) / 12) ** 2
            if best is None or err < best[0]:
                best = (err, size, shape, distinct, singles, mx)
    return best


def run(rng, arms, sigma, horizon, size, shape, priors, reps):
    """One batch of `reps` seasons; returns {rule: (Elo played, points per 100 games)} against "best"."""
    R, K = reps, len(arms)
    mu = np.array(arms, dtype=float)
    w = rng.gamma(shape, size=(R, size))
    w /= w.sum(axis=1, keepdims=True)
    cum = np.cumsum(w, axis=1)
    opp = np.array([np.searchsorted(cum[r], rng.random(horizon)) for r in range(R)]).clip(max=size - 1)
    gap = rng.normal(30.0, 190.0, size=(R, size))            # our rating-based edge on each bot (record: +31, SD ~190)
    b = rng.normal(0.0, TAU, size=(R, size))
    c = rng.normal(0.0, sigma, size=(R, size, K)) if sigma > 0 else np.zeros((R, size, K))
    col = np.where(rng.random((R, horizon)) < 0.5, 1.0, -1.0)
    u = rng.random((R, horizon))                               # common random numbers: one per game, all rules
    ex_u = rng.random((R, horizon, K + 1))                     # Thompson's draws
    rr = np.arange(R)

    rules = ["best", "oracle"] + [f"matthew-{e}" for e in (3, 6, 9)] \
        + [f"greedy-{s}" for s in priors] + [f"thompson-{s}" for s in priors] + [f"steer{e}-50" for e in STEER]
    elo = {r: 0.0 for r in rules}
    pts = {r: 0.0 for r in rules}
    # State: Matthew's counts and scores; the Gaussian beliefs (mean of b, c_1..c_K; covariance) per prior.
    n_used = {e: np.zeros((R, size, K)) for e in (3, 6, 9)}
    s_sum = {e: np.zeros((R, size, K)) for e in (3, 6, 9)}
    seen = {e: np.zeros((R, size)) for e in (3, 6, 9)}
    belief = {}
    for key in [f"{kind}-{s}" for kind in ("greedy", "thompson") for s in priors] + [f"steer{e}-50" for e in STEER]:
        s = int(key.rsplit("-", 1)[1])
        m = np.zeros((R, size, K + 1))
        P = np.zeros((R, size, K + 1, K + 1))
        P[:, :, 0, 0] = TAU ** 2
        for k in range(K):
            P[:, :, k + 1, k + 1] = max(s, 1e-3) ** 2
        belief[key] = (m, P)

    for t in range(horizon):
        j = opp[:, t]
        base = gap[rr, j] + A * col[:, t]
        true = base[:, None] + b[rr, j][:, None] + mu[None, :] + c[rr, j]          # (R, K): each arm's true edge
        e_true = expect(true)
        choice = {"best": np.zeros(R, dtype=int), "oracle": true.argmax(axis=1)}
        for e in (3, 6, 9):
            nu, ss = n_used[e][rr, j], s_sum[e][rr, j]
            explore = seen[e][rr, j] < e
            least = (nu + np.arange(K)[None, :] * 1e-3).argmin(axis=1)
            mean = np.where(nu > 0, ss / np.maximum(nu, 1), -1.0)
            top = (mean - np.arange(K)[None, :] * 1e-3).argmax(axis=1)
            choice[f"matthew-{e}"] = np.where(explore, least, top)
        for key, (m, P) in belief.items():
            mj, Pj = m[rr, j], P[rr, j]
            edge = mu[None, :] + mj[:, 1:]                                      # b is common: it drops out
            if key.startswith("thompson"):                                      # one draw of (b, c) from the belief
                L = np.linalg.cholesky(Pj + 1e-6 * np.eye(K + 1)[None])
                z = np.sqrt(2) * erfinv_vec(2 * ex_u[:, t] - 1)
                edge = mu[None, :] + (mj + np.einsum("rij,rj->ri", L, z))[:, 1:]
            if key.startswith("steer"):                                         # the best, others now and then,
                eps = int(key[5:].split("-")[0]) / 100                          # a switch once one is clearly ahead
                dm = edge - edge[:, :1]
                var = (np.diagonal(Pj, axis1=1, axis2=2)[:, 1:] + Pj[:, 1, 1][:, None]
                       - 2 * Pj[:, 1, 1:])
                clear = ndtr(dm / np.sqrt(np.maximum(var, 1e-9))) > 0.95
                clear[:, 0] = False
                pick = np.where(clear.any(axis=1), np.where(clear, dm, -np.inf).argmax(axis=1), 0)
                other = 1 + (ex_u[:, t, 1] * (K - 1)).astype(int).clip(max=K - 2)
                choice[key] = np.where(clear.any(axis=1), pick, np.where(ex_u[:, t, 0] < eps, other, 0))
                continue
            choice[key] = edge.argmax(axis=1)

        for rule, k in choice.items():
            d = true[rr, k]
            elo[rule] += float((d - true[:, 0]).mean())
            pts[rule] += float((e_true[rr, k] - e_true[:, 0]).mean())
            if rule in ("best", "oracle"):
                continue
            pw, pd, _ = outcome_probs(d)
            res = np.where(u[:, t] < pw, 1.0, np.where(u[:, t] < pw + pd, 0.5, 0.0))
            if rule.startswith("matthew"):
                e = int(rule.split("-")[1])
                n_used[e][rr, j, k] += 1
                s_sum[e][rr, j, k] += res
                seen[e][rr, j] += 1
                continue
            m, P = belief[rule]
            mj, Pj = m[rr, j], P[rr, j]
            H = np.zeros((R, K + 1))
            H[:, 0] = 1.0
            H[rr, k + 1] = 1.0
            d_lin = base + mu[k] + (H * mj).sum(axis=1)
            g, v = slope_var(d_lin)
            Hs = H * g[:, None]
            PHt = np.einsum("rij,rj->ri", Pj, Hs)
            S = (Hs * PHt).sum(axis=1) + v
            Kg = PHt / S[:, None]
            m[rr, j] = mj + Kg * (res - expect(d_lin))[:, None]
            P[rr, j] = Pj - Kg[:, :, None] * PHt[:, None, :]
    return {r: (elo[r] / horizon, 100 * pts[r] / horizon) for r in rules}


def erfinv_vec(x):
    from scipy.special import erfinv
    return erfinv(np.clip(x, -0.999999, 0.999999))


rng = np.random.default_rng(88)
err, size, shape, distinct, singles, mx = calibrate(rng)
print(f"pool: {size} bots, popularity gamma shape {shape}: 596 games meet {distinct:.0f} bots, {singles:.0f} once, "
      f"the most-met {mx:.0f} times (the bot: 194, 67, 12)")
scenarios = {
    "today's three (h1024-3m 0, h512-3m -41, nnue1-h512 -52)": [0.0, -41.0, -52.0],
    "a close second (0, -10, -41)": [0.0, -10.0, -41.0],
}
sigmas = (0, 50, 100) if QUICK else (0, 25, 50, 100, 150)
horizons = (600,) if QUICK else (600, 3000, 20000)
priors = (10, 25, 50)
for name, arms in scenarios.items():
    for horizon in horizons:
        if horizon > 3000 and arms != scenarios[next(iter(scenarios))]:
            continue                                         # the year-long run: today's three only
        sig = sigmas if horizon <= 3000 else (0, 50, 100)
        reps = REPS if horizon <= 3000 else max(REPS // 6, 20)
        print(f"\n{name}, {horizon} games ({reps} seasons each): Elo played / points per 100 games, against 'best'")
        results = {s_: run(rng, arms, float(s_), horizon, size, shape, priors, reps) for s_ in sig}
        print("  sigma (true)  " + "".join(f"{s_:>14}" for s_ in sig))
        for rule in results[sig[0]]:
            print(f"  {rule:13s} " + "".join(f"{results[s_][rule][0]:+7.1f}/{results[s_][rule][1]:+5.1f}" for s_ in sig))
