"""Do opponent-specific weaknesses exist in the bot's games?  P8's data question (OPPONENTS.md).

    python -I analyze.py <record.csv> [--nnue] [--no-forfeits] [--sims N]

The record is collect.py's. Each game's result is modelled the way BayesElo does it, on Lichess's rating scale:
    our edge  d = S[net] + speed term + a (white +1, black -1) - opponent's rating + b[opponent] (+ c[opponent, net])
    P(win) = f(d - D), P(loss) = f(-d - D), P(draw) = the rest,  f(x) = 1 / (1 + 10^(-x / 400))
S is each net's strength (fitted), D the draw width. b is the opponent-specific part: how much better (+) or worse
(-) we do against that bot than its rating says, drawn from N(0, tau^2); c is how that changes with the net, from
N(0, sigma^2). tau and sigma are what P8 needs: tau says whether weaknesses exist, sigma whether a net choice could
exploit them. Both are fitted by maximum likelihood with b and c integrated out (Gauss-Hermite), and profiled for
their 95% intervals. The same c, with the games grouped by opening (the family, "Sicilian Defense"; or the ECO
volume, A-E) instead of by net, asks whether results against a bot follow the opening. A model-free check beside it:
the covariance of two games' surprises against one bot, same opening against other opening, same net against other
net. Then who repeats an opening, and how often the learner's book (ours.bin) played. --nnue keeps only the
networks' games; --no-forfeits drops time forfeits.
"""
import csv
import math
import sys
from collections import Counter, defaultdict

import numpy as np
from scipy.optimize import minimize
from scipy.special import logsumexp
from scipy.stats import chi2

args = sys.argv[1:]
rows = list(csv.DictReader(open(args[0], encoding="utf-8")))
NNUE_ONLY = "--nnue" in args
NO_FORFEITS = "--no-forfeits" in args
SIMS = int(args[args.index("--sims") + 1]) if "--sims" in args else 400
NETS_NNUE = ("nnue1", "nnue1-h512", "h512-3m", "h1024-3m")

games = []
for r in rows:
    if r["score"] == "" or not r["opp_elo"].isdigit():
        continue
    if NO_FORFEITS and r["termination"] == "Time forfeit":
        continue
    if NNUE_ONLY and r["net"] not in NETS_NNUE:
        continue
    games.append(r)

nets = sorted({g["net"] for g in games}, key=lambda n: ["built-in", "eval-sf1"].index(n) if n in ("built-in", "eval-sf1")
              else 2 + NETS_NNUE.index(n) if n in NETS_NNUE else 9)
opps = sorted({g["opponent"] for g in games})
speed_of = {"bullet": 1, "rapid": 2, "classical": 2}
n = len(games)
net_i = np.array([nets.index(g["net"]) for g in games])
opp_i = np.array([opps.index(g["opponent"]) for g in games])
spd_i = np.array([speed_of.get(g["speed"].lower(), 0) for g in games])
col = np.array([1.0 if g["colour"] == "white" else -1.0 for g in games])
ropp = np.array([float(g["opp_elo"]) for g in games])
score = np.array([float(g["score"]) for g in games])
win, draw, loss = score == 1.0, score == 0.5, score == 0.0
J, K = len(opps), len(nets)
family = [g["opening"].split(":")[0].strip() or "?" for g in games]     # "Sicilian Defense: Najdorf" -> Sicilian
volume = [g["eco"][:1] or "?" for g in games]                            # ECO A-E


def make_cells(keys):
    """Cells of (opponent, key): each game's cell, how many, each cell's opponent, and the bots seen in 2+ keys."""
    of = {}
    idx = np.array([of.setdefault((o, k), len(of)) for o, k in zip(opp_i.tolist(), keys)])
    owner = np.array([o for (o, k), c in sorted(of.items(), key=lambda kv: kv[1])])
    multi = sum(1 for j in range(J) if len({k for (o, k) in of if o == j}) > 1)
    repeat = sum(1 for c in np.bincount(idx) if c > 1)
    return idx, len(of), owner, multi, repeat


CELLS_NET = make_cells(net_i.tolist())

LN10_400 = math.log(10) / 400


def log_f(x):
    return -np.logaddexp(0.0, -x * LN10_400)


def log_p(d, D):
    """log P(our result) for edges d (any shape whose first axis is the game), draw width D."""
    lw, ll = log_f(d - D), log_f(-d - D)
    pw, pl = np.exp(lw), np.exp(ll)
    pd = np.clip(1.0 - pw - pl, 1e-12, 1.0)
    shape = (-1,) + (1,) * (d.ndim - 1)
    return np.where(win.reshape(shape), lw, np.where(loss.reshape(shape), ll, np.log(pd)))


def fixed_edge(theta):
    S = theta[:K]
    sp = np.array([0.0, theta[K], theta[K + 1]])
    a = theta[K + 2]
    return S[net_i] + sp[spd_i] + a * col - ropp


GH_X, GH_W = np.polynomial.hermite.hermgauss(40)
GH_LW = np.log(GH_W / math.sqrt(math.pi))
GH2_X, GH2_W = np.polynomial.hermite.hermgauss(20)
GH2_LW = np.log(GH2_W / math.sqrt(math.pi))


def nll(theta, tau, sigma=0.0, cells=None):
    d0 = fixed_edge(theta)
    D = math.exp(theta[K + 3])
    if tau == 0 and sigma == 0:
        return -log_p(d0, D).sum()
    if sigma == 0:
        b = math.sqrt(2) * tau * GH_X                                 # (Q,)
        lp = log_p(d0[:, None] + b[None, :], D)                        # (n, Q)
        per_opp = np.zeros((J, len(b)))
        np.add.at(per_opp, opp_i, lp)
        return -logsumexp(per_opp + GH_LW[None, :], axis=1).sum()
    b = math.sqrt(2) * tau * GH2_X if tau > 0 else np.zeros(1)
    lwb = GH2_LW if tau > 0 else np.zeros(1)
    cell_i, C, cell_opp = (cells or CELLS_NET)[:3]
    c = math.sqrt(2) * sigma * GH2_X
    lp = log_p(d0[:, None, None] + b[None, :, None] + c[None, None, :], D)   # (n, Q, R)
    per_cell = np.zeros((C, len(b), len(c)))
    np.add.at(per_cell, cell_i, lp)
    cell_l = logsumexp(per_cell + GH2_LW[None, None, :], axis=2)              # (C, Q)
    per_opp = np.zeros((J, len(b)))
    np.add.at(per_opp, cell_opp, cell_l)
    return -logsumexp(per_opp + lwb[None, :], axis=1).sum()


def fit(tau, sigma=0.0, start=None, cells=None):
    x0 = start if start is not None else np.concatenate([np.full(K, 400.0), [0.0, 0.0, 15.0, math.log(60.0)]])
    res = minimize(nll, x0, args=(tau, sigma, cells), method="L-BFGS-B")
    if start is None:                         # polish the first fit; the profile's later fits start next to theirs
        res = minimize(nll, res.x, args=(tau, sigma, cells), method="Nelder-Mead",
                       options={"maxiter": 4000, "xatol": 0.01, "fatol": 1e-4})
    return res.x, res.fun


def interval(grid, nlls, best):
    """The 95% profile-likelihood interval: where 2 * (nll - best) <= 3.84."""
    inside = [g for g, v in zip(grid, nlls) if 2 * (v - best) <= 3.841]
    return min(inside), max(inside)


def expected(theta, extra=0.0):
    d = fixed_edge(theta) + extra
    D = math.exp(theta[K + 3])
    pw, pl = np.exp(log_f(d - D)), np.exp(log_f(-d - D))
    pd = 1 - pw - pl
    e = pw + 0.5 * pd
    v = pw + 0.25 * pd - e * e
    return e, v, pw, pd, pl


print(f"{n} games, {J} opponents, nets {nets}" + (" (networks only)" if NNUE_ONLY else "")
      + (", no time forfeits" if NO_FORFEITS else ""))
print("games per opponent:", sorted(Counter(Counter(opp_i.tolist()).values()).items()))
print(f"results: +{win.sum()} ={draw.sum()} -{loss.sum()}  (score {score.mean():.3f})")

# 1. No opponent effects: the nets' strengths, the draw width, the colour and speed terms.
theta0, nll0 = fit(0.0)
print("\nModel without opponent effects:")
for k, name in enumerate(nets):
    cnt = (net_i == k).sum()
    print(f"  S[{name}] = {theta0[k]:7.0f}  ({cnt} games, mean opponent {ropp[net_i == k].mean():.0f})")
print(f"  bullet {theta0[K]:+.0f}, rapid/classical {theta0[K + 1]:+.0f}, white +-{theta0[K + 2]:.0f}, "
      f"draw width D {math.exp(theta0[K + 3]):.0f}")
e0, v0, *_ = expected(theta0)
print(f"  mean per-game score variance {v0.mean():.3f} (draws in: Matthew's 0.2), observed (s - E)^2 {((score - e0) ** 2).mean():.3f}")

# 2. tau: opponent effects.
grid = [0, 10, 20, 30, 40, 50, 60, 70, 80, 100, 120, 150, 200, 250]
prof, start = [], theta0
for t in grid:
    th, v = fit(float(t), 0.0, start)
    prof.append(v)
    start = th
best = min(prof)
tau_hat = grid[prof.index(best)]
lo, hi = interval(grid, prof, best)
lr = 2 * (prof[0] - best)
p = 0.5 * chi2.sf(lr, 1) if lr > 0 else 0.5
print(f"\nOpponent effects: tau (the SD of 'this bot is weak/strong against us beyond its rating'):")
print("  profile  " + "  ".join(f"{t}:{2 * (v - best):.2f}" for t, v in zip(grid, prof)))
print(f"  tau = {tau_hat} Elo, 95% interval [{lo}, {hi}]; against tau = 0: LR {lr:.2f}, p = {p:.3f}")

# 3. Per-opponent: raw z and the shrunk estimate, and what chance alone gives (parametric bootstrap at tau = 0).
O = np.bincount(opp_i, weights=score, minlength=J)
E = np.bincount(opp_i, weights=e0, minlength=J)
V = np.bincount(opp_i, weights=v0, minlength=J)
N = np.bincount(opp_i, minlength=J)
z = (O - E) / np.sqrt(V)
many = N >= 3
rng = np.random.default_rng(8)
_, _, pw0, pd0, _ = expected(theta0)
sim_count, sim_max = [], []
for _ in range(SIMS):
    u = rng.random(n)
    s = np.where(u < pw0, 1.0, np.where(u < pw0 + pd0, 0.5, 0.0))
    zs = (np.bincount(opp_i, weights=s, minlength=J) - E) / np.sqrt(V)
    sim_count.append(int((np.abs(zs[many]) > 1.96).sum()))
    sim_max.append(float(np.abs(zs[many]).max()))
obs_count = int((np.abs(z[many]) > 1.96).sum())
obs_max = float(np.abs(z[many]).max())
print(f"\nOpponents with 3+ games: {many.sum()}; |z| > 1.96 for {obs_count} "
      f"(chance alone: {np.mean(sim_count):.1f}, 95% of runs <= {np.percentile(sim_count, 95):.0f}); "
      f"largest |z| {obs_max:.2f} (chance: median {np.median(sim_max):.2f}, 95% <= {np.percentile(sim_max, 95):.2f})")
print(f"  sum of z^2 over them {float((z[many] ** 2).sum()):.1f} on {many.sum()} opponents "
      f"(chance: mean {many.sum()}, chi-square p = {chi2.sf(float((z[many] ** 2).sum()), many.sum()):.3f})")

order = np.argsort(-np.abs(z) * many)
print("\n  the most surprising opponents (3+ games): n, score, expected, z, Elo off the rating (raw)")
for j in order[:12]:
    if not many[j]:
        continue
    games_j = [g for g in games if g["opponent"] == opps[j]]
    netc = Counter(g["net"] for g in games_j)
    raw = 400 * math.log10(max(min(O[j] / N[j], 0.97), 0.03) / (1 - max(min(O[j] / N[j], 0.97), 0.03))) \
        - 400 * math.log10(E[j] / N[j] / (1 - E[j] / N[j]))
    print(f"  {opps[j]:22s} {N[j]:3d}  {O[j]:5.1f}  {E[j]:5.1f}  {z[j]:+5.2f}  {raw:+5.0f}   "
          + ", ".join(f"{k} {v}" for k, v in netc.most_common()))

# 4. sigma: does the opponent effect change with the net?  (The thing a per-opponent net choice would use.)  And
# with the opening (the thing a per-opponent book would use): the same model, the cells by opening instead of by net.
grid_s = [0, 25, 50, 75, 100, 150, 200, 300]
for label, keys in (("net", net_i.tolist()), ("opening family", family), ("ECO volume", volume)):
    cells = make_cells(keys)
    prof_s, start = [], theta0
    for s_ in grid_s:
        th, v = fit(float(tau_hat), float(s_), start, cells)
        prof_s.append(v)
        start = th
    best_s = min(prof_s)
    sig_hat = grid_s[prof_s.index(best_s)]
    lo_s, hi_s = interval(grid_s, prof_s, best_s)
    lr_s = 2 * (prof_s[0] - best_s)
    p_s = 0.5 * chi2.sf(lr_s, 1) if lr_s > 0 else 0.5
    print(f"\n{label} x opponent (tau held at {tau_hat}): {len(set(keys))} kinds; {cells[3]} bots met "
          f"on two or more; {cells[4]} (bot, {label}) cells with 2+ games")
    print("  profile  " + "  ".join(f"{t}:{2 * (v - best_s):.2f}" for t, v in zip(grid_s, prof_s)))
    print(f"  sigma = {sig_hat} Elo, 95% interval [{lo_s}, {hi_s}]; against sigma = 0: LR {lr_s:.2f}, p = {p_s:.3f}")

# 4b. The same question without a model: two games against one bot, each result's surprise (score - expected).
# If the opening matters against a bot, two games in the same opening family agree more (a higher covariance) than
# two in different families; likewise for the net. The bot's common part adds to every pair alike, so the differences
# are what count. 95% intervals by resampling bots.
by_opp = defaultdict(list)
for i in range(n):
    by_opp[int(opp_i[i])].append(i)
resid = score - e0
kinds = ("same opening, same net", "same opening, other net", "other opening, same net", "other opening, other net")


def pair_cov(bots):
    acc = {k: [0.0, 0] for k in kinds}
    for j in bots:
        idx = by_opp[j]
        for a in range(len(idx)):
            for b in range(a + 1, len(idx)):
                i, k = idx[a], idx[b]
                key = kinds[(0 if family[i] == family[k] else 2) + (0 if net_i[i] == net_i[k] else 1)]
                acc[key][0] += resid[i] * resid[k]
                acc[key][1] += 1
    cov = {k: v[0] / v[1] if v[1] else float("nan") for k, v in acc.items()}
    same_open = (acc[kinds[0]][0] + acc[kinds[1]][0]) / max(acc[kinds[0]][1] + acc[kinds[1]][1], 1)
    other_open = (acc[kinds[2]][0] + acc[kinds[3]][0]) / max(acc[kinds[2]][1] + acc[kinds[3]][1], 1)
    same_net = (acc[kinds[0]][0] + acc[kinds[2]][0]) / max(acc[kinds[0]][1] + acc[kinds[2]][1], 1)
    other_net = (acc[kinds[1]][0] + acc[kinds[3]][0]) / max(acc[kinds[1]][1] + acc[kinds[3]][1], 1)
    return cov, {k: v[1] for k, v in acc.items()}, same_open - other_open, same_net - other_net


bots = list(by_opp)
cov, counts, d_open, d_net = pair_cov(bots)
boot = np.array([pair_cov(rng.choice(bots, size=len(bots)).tolist())[2:] for _ in range(SIMS // 2)])
slope = float(np.mean([(expected(theta0, h)[0] - expected(theta0, -h)[0]) / (2 * h) for h in (5.0,)]))
print("\nPairs of games against one bot, the covariance of their surprises (x 1000):")
for k in kinds:
    print(f"  {k:26s} {counts[k]:5d} pairs  {1000 * cov[k]:+6.1f}")
for label, d, col_ in (("opening", d_open, 0), ("net", d_net, 1)):
    lo, hi = np.percentile(boot[:, col_], [2.5, 97.5])
    elo = math.copysign(math.sqrt(abs(d)), d) / slope if slope > 0 else float("nan")
    print(f"  same {label} minus other {label}: {1000 * d:+.1f} [{1000 * lo:+.1f}, {1000 * hi:+.1f}]"
          f"  (as an SD in Elo: {elo:+.0f})")

# 5. Openings: do bots repeat themselves?
by = defaultdict(list)
for g in games:
    by[(g["opponent"], g["colour"])].append(g)
pairs = same8 = same12 = same16 = 0
for key, gs in by.items():
    for i in range(len(gs)):
        for k in range(i + 1, len(gs)):
            a, b = gs[i]["line"].split(), gs[k]["line"].split()
            pairs += 1
            same8 += a[:8] == b[:8]
            same12 += a[:12] == b[:12]
            same16 += a[:16] == b[:16]
print(f"\nOpenings: {pairs} pairs of games against the same bot with the same colours; identical for "
      f"8 plies {same8} ({same8 / max(pairs, 1):.0%}), 12 plies {same12} ({same12 / max(pairs, 1):.0%}), "
      f"16 plies {same16} ({same16 / max(pairs, 1):.0%})")
# Who varies?  In each such pair, walk the moves while they agree: every move made from the same position by the
# same side is a chance to repeat, and the first difference is either theirs or ours.
diverge = Counter()
chances = Counter()
per_bot = defaultdict(lambda: [0, 0])          # their moves from a position met before: [chances, different]
for key, gs in by.items():
    for i in range(len(gs)):
        for k in range(i + 1, len(gs)):
            a, b = gs[i]["line"].split(), gs[k]["line"].split()
            ours = 0 if gs[i]["colour"] == "white" else 1
            for ply in range(min(len(a), len(b))):
                side = "ours" if ply % 2 == ours else "theirs"
                chances[side] += 1
                if side == "theirs":
                    per_bot[key[0]][0] += 1
                if a[ply] != b[ply]:
                    diverge[side] += 1
                    if side == "theirs":
                        per_bot[key[0]][1] += 1
                    break
            else:
                diverge["never (20 plies)"] += 1
print("  the first difference in each pair:", dict(diverge))
for side in ("ours", "theirs"):
    print(f"  {side}: {chances[side]} moves from a position met before, {diverge[side]} different "
          f"({diverge[side] / max(chances[side], 1):.0%} a move)")
seen4 = {bot: v for bot, v in per_bot.items() if v[0] >= 4}
always = sum(1 for v in seen4.values() if v[1] == 0)
print(f"  bots with 4+ such moves: {len(seen4)}; {always} of them never varied, "
      f"{sum(1 for v in seen4.values() if v[1] / v[0] >= 0.25)} varied on a quarter or more")
first_out = []
for g in games:
    ours_from_book = int(g["book_main"]) + int(g["book_ours"]) + int(g["book_wide"])
    first_out.append(ours_from_book)
print(f"  our book moves a game: median {np.median(first_out):.0f}, mean {np.mean(first_out):.1f}")
# The learner's book (ours.bin) at work: games with a move from it, and against a bot met before.
met = Counter()
use = defaultdict(lambda: [0, 0, 0, 0])      # net: games, with an ours move, repeat-bot games, those with one
for g in sorted(games, key=lambda g: g["utc"]):
    u = use[g["net"]]
    u[0] += 1
    u[1] += int(g["book_ours"]) > 0
    if met[g["opponent"]]:
        u[2] += 1
        u[3] += int(g["book_ours"]) > 0
    met[g["opponent"]] += 1
print("  games with a move from the learner's book (ours.bin), by net; and among games against a bot met before:")
for net_name in nets:
    u = use[net_name]
    print(f"    {net_name:12s} {u[1]:3d} of {u[0]:3d} ({u[1] / max(u[0], 1):.0%});  {u[3]:3d} of {u[2]:3d} "
          f"({u[3] / max(u[2], 1):.0%})")
