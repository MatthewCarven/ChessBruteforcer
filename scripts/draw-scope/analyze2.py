"""Draw-offer scoping, second pass: by era, at today's K, the accept side, Lichess's offer spacing, and rule options.

    python -I analyze2.py data.json

Era: "early" = the bot's first days on the hand-made / Texel evaluation (~1950-2250 blitz); "networks" = from
nnue1's start on the bot, 2026-10-17 04:50 local = 2026-10-16 17:50 UTC (~2250-2650 blitz).
Rating points use Lichess's Glicko-2 expectation (opponent RD ~60) and K = 11.5, the median rating change per
point of (score - expected) in the established blitz games of 13-19 Oct, so every row is in today's points.
"""
import collections
import importlib.util
import json
import statistics
import sys

spec = importlib.util.spec_from_file_location("an", __file__.replace("analyze2.py", "analyze.py"))
an = importlib.util.module_from_spec(spec)
spec.loader.exec_module(an)
S, E, done, games, events = an.S, an.E, an.done, an.games, an.events
KF = 11.5
era = an.era


def pts(gs, draw=False):
    return sum(KF * ((0.5 if draw else S(g)) - E(g)) for g in gs)


def table(title, header, rows, note=None):
    print()
    print("### " + title)
    if note:
        print(note)
    print("| " + " | ".join(header) + " |")
    print("|" + "---|" * len(header))
    for r in rows:
        print("| " + " | ".join(str(x) for x in r) + " |")


print("eras:", collections.Counter(era(g) for g in done))
for e_ in ("early", "networks"):
    gs = [g for g in done if era(g) == e_]
    print(e_, "W/D/L", an.wdl(gs), "our elo median", statistics.median(g["our_elo"] for g in gs if an.pool(g) == "blitz"),
          "draws by agreement-ish (Normal 1/2 with >=1 draw chat)", sum(1 for g in gs if S(g) == 0.5 and g["id"] in events))

# A. Stretches by era, N and pieces.
rows = []
for e_ in ("early", "networks"):
    pool_ = [g for g in done if era(g) == e_]
    for N in (10, 20, 30):
        trig = [(g, an.stretch(g, N)) for g in pool_]
        trig = [(g, t) for g, t in trig if t]
        pcs = collections.Counter(an.pc_bucket(t[1]) for g, t in trig)
        w, d_, l = an.wdl([g for g, t in trig])
        rows.append([e_, len(pool_), N, f"{len(trig)} ({100 * len(trig) / len(pool_):.0f}%)",
                     pcs["a. <=5 (tables)"], pcs["b. 6-10"], pcs["c. 11-20"], pcs["d. 21-32"], f"{w} / {d_} / {l}"])
table("Games with N of our moves in a row within +-0.15", ["era", "games", "N", "with a stretch", "<=5 pieces",
      "6-10", "11-20", "21-32", "W / D / L after"], rows)

# B. Outcomes by rating difference and era, at today's K.
for N in (10, 20):
    rows = []
    for e_ in ("early", "networks", "all"):
        trig = [(g, an.stretch(g, N)) for g in done if e_ == "all" or era(g) == e_]
        trig = [g for g, t in trig if t]
        groups = collections.defaultdict(list)
        for g in trig:
            groups[an.rd_bucket(g)].append(g)
        for k in sorted(groups) + ["all"]:
            gs = trig if k == "all" else groups[k]
            if not gs:
                continue
            w, d_, l = an.wdl(gs)
            rows.append([e_, k, len(gs), f"{w} / {d_} / {l}", f"{100 * sum(S(g) for g in gs) / len(gs):.0f}%",
                         f"{100 * sum(E(g) for g in gs) / len(gs):.0f}%", f"{pts(gs):+.1f}", f"{pts(gs, True):+.1f}",
                         f"{pts(gs, True) - pts(gs):+.1f}"])
    table(f"N = {N}: how the games with a level stretch ended, by rating difference (opponent - us)",
          ["era", "group", "games", "W / D / L", "score", "expected", "pts as played", "pts if drawn", "change"], rows)

# C. Network era: minutes left after a trigger, by pieces.
for N in (10, 20):
    trig = [(g, an.stretch(g, N)) for g in done if era(g) == "networks"]
    trig = [(g, t) for g, t in trig if t]
    rows = []
    for k in ("a. <=5 (tables)", "b. 6-10", "c. 11-20", "d. 21-32"):
        gs = [(g, t) for g, t in trig if an.pc_bucket(t[1]) == k]
        if not gs:
            continue
        res = [an.after(g, t[0]) for g, t in gs]
        rows.append([k, len(gs), sum(1 for b, w, n, m in res if b >= 150), sum(1 for b, w, n, m in res if w <= -150),
                     f"{statistics.median(n for b, w, n, m in res):.0f}", f"{max(n for b, w, n, m in res)}",
                     f"{statistics.median(m for b, w, n, m in res):.1f}", f"{sum(m for b, w, n, m in res):.0f}"])
    table(f"Networks era, N = {N}: what was left after the trigger", ["pieces", "games", "we later +1.50",
          "they later +1.50", "median plies left", "most plies left", "median minutes left", "total minutes"], rows)

# D. Lichess's spacing of offers: our offers' move numbers within a game.
byid = {g["id"]: g for g in games}
gaps = []
per_game = collections.Counter()
for gid, evs in events.items():
    g = byid.get(gid)
    if not g:
        continue
    me = g["colour"].capitalize()
    moves = [e["move"] for e in evs if e["msg"] == me + " offers draw" and e["move"] is not None]
    per_game[len(moves)] += 1 if moves else 0
    gaps += [b - a for a, b in zip(moves, moves[1:])]
print()
print("our offers: gaps in moves between successive offers in a game:", sorted(collections.Counter(gaps).items()))

# E. The accept side: their offers, mapped to plies.
def ply_of(g, move_no):
    """The ply index of our move at full move `move_no`."""
    return 2 * (move_no - 1) + (0 if g["colour"] == "white" else 1)


def last_evals(g, upto, n):
    out = []
    for p in reversed(g["plies"][:upto + 1]):
        if p[0] and p[1] is not None:
            out.append(an.ours_view(g, p[1]))
            if len(out) == n:
                break
    return out


rows = []
acc = []
for gid, evs in events.items():
    g = byid.get(gid)
    if not g or S(g) is None:
        continue
    me = g["colour"].capitalize()
    theirs = [e for e in evs if e["msg"].endswith("offers draw") and not e["msg"].startswith(me)]
    if not theirs:
        continue
    first = theirs[0]
    p = ply_of(g, first["move"]) if first["move"] else None
    if p is None or p >= len(g["plies"]):
        continue
    b, w, n, m = an.after(g, p)
    ev10 = last_evals(g, p, 10)
    level10 = len(ev10) == 10 and all(abs(x) <= 15 for x in ev10)
    accepted = any(e["msg"] == "Draw offer accepted" for e in evs) and S(g) == 0.5 and \
        not any(e["msg"] == me + " offers draw" for e in evs if e["t"] < first["t"])
    acc.append((g, p, n, m, level10, ev10[0] if ev10 else None, len(theirs)))
    rows.append([era(g), g["id"], g["opponent"], f"{g['opp_elo'] - g['our_elo']:+d}", first["move"],
                 g["plies"][p][3], first["our_eval"], "yes" if level10 else "no", len(theirs),
                 {1.0: "W", 0.5: "D", 0.0: "L"}[S(g)], g["termination"], n, f"{m:.1f}",
                 f"{KF * (0.5 - E(g)) - KF * (S(g) - E(g)):+.1f}"])
rows.sort(key=lambda r: (r[0], int(r[3])))
table("Their offers: each game, at their first offer", ["era", "game", "opponent", "opp - us", "move", "pieces",
      "our eval then", "last 10 of ours within +-0.15", "offers", "result", "how", "plies left", "minutes left",
      "pts change if we'd accepted"], rows)

# F. Rule options, simulated over the networks era (and all games): where would an offer go, and what is at stake?
def rule_trigger(g, N, band, max_pieces, min_rd, strict=False):
    if g["opp_elo"] - g["our_elo"] < min_rd:
        return None
    return an.stretch(g, N, band=band, strict=strict, max_pieces=max_pieces)


options = [
    ("today: 0 cp, 10 moves, <=10 pieces (no-score breaks)", dict(N=10, band=0, max_pieces=10, min_rd=-9999, strict=True)),
    ("tuned: 15 cp, 20 moves, any pieces", dict(N=20, band=15, max_pieces=32, min_rd=-9999)),
    ("tuned: 15 cp, 10 moves, <=10 pieces", dict(N=10, band=15, max_pieces=10, min_rd=-9999)),
    ("rating-aware: 15 cp, 20 moves, any pieces, opp >= us - 50", dict(N=20, band=15, max_pieces=32, min_rd=-50)),
    ("rating-aware: 15 cp, 10 moves, any pieces, opp >= us - 50", dict(N=10, band=15, max_pieces=32, min_rd=-50)),
    ("rating-aware: 0 cp, 10 moves, <=10 pieces, opp >= us - 50", dict(N=10, band=0, max_pieces=10, min_rd=-50, strict=True)),
]
for e_ in ("networks", "all"):
    rows = []
    for name, kw in options:
        trig = [(g, rule_trigger(g, **kw)) for g in done if e_ == "all" or era(g) == e_]
        trig = [(g, t) for g, t in trig if t]
        gs = [g for g, t in trig]
        w, d_, l = an.wdl(gs)
        mins = sum(an.after(g, t[0])[3] for g, t in trig)
        rows.append([name, len(gs), f"{w} / {d_} / {l}", f"{pts(gs, True) - pts(gs):+.1f}", f"{mins:.0f}",
                     f"{0.12 * (pts(gs, True) - pts(gs)):+.1f}", f"{0.12 * mins:.0f}"])
    table(f"Rule options, {e_} games: if every offer were accepted / at the 12% of games where bots accepted ours",
          ["rule", "games it fires in", "W / D / L as played", "pts change if accepted", "minutes saved if accepted",
           "pts change at 12%", "minutes at 12%"], rows)
# the time the bot spent playing at all, for scale
mins_all = []
for g in done:
    inc = an.inc_of(g)
    used = 0.0
    for side in (True, False):
        cl = [p[4] for p in g["plies"] if p[0] == side and p[4] is not None]
        if cl:
            base = int(g["tc"].split("+")[0]) if g["tc"] and "+" in g["tc"] else cl[0]
            used += base - cl[-1] + inc * len(cl)
    mins_all.append(used / 60)
for e_ in ("early", "networks"):
    ms = [m for g, m in zip(done, mins_all) if era(g) == e_]
    print(f"{e_}: {len(ms)} games, {sum(ms) / 60:.1f} hours of play, median {statistics.median(ms):.1f} min a game")
