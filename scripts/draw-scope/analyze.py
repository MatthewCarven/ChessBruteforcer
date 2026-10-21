"""Draw-offer scoping: level stretches in the bot's games, how they ended, and the rating points at stake.

    python -I analyze.py data.json [band_cp]

A "level stretch" of N moves: N of our engine moves in a row whose evaluation is within +-band (default 15 cp).
Moves with no evaluation (book moves; ponder hits that reported no score before P1) are skipped: they neither
count nor break a run. The trigger is the move on which the run first reaches N: where an offer would go.
"""
import collections
import json
import math
import statistics
import sys

d = json.load(open(sys.argv[1], encoding="utf-8"))
BAND = int(sys.argv[2]) if __name__ == "__main__" and len(sys.argv) > 2 else 15
games = d["games"]
events = d["events"]
G_RD = 1 / math.sqrt(1 + 3 * (60 / 173.7178) ** 2 / math.pi ** 2)   # Glicko g() for an opponent RD of ~60


def S(g):
    r = g["result"]
    if r == "1/2-1/2":
        return 0.5
    if r == "1-0":
        return 1.0 if g["colour"] == "white" else 0.0
    if r == "0-1":
        return 0.0 if g["colour"] == "white" else 1.0
    return None


def E(g):
    return 1 / (1 + 10 ** (-G_RD * (g["our_elo"] - g["opp_elo"]) / 400))


def pool(g):
    return g["event"].split()[1] if g["event"] else "?"


def ours_view(g, cp):
    return cp if g["colour"] == "white" else -cp


done = [g for g in games if S(g) is not None and g["our_elo"] and g["opp_elo"]]
print(f"finished games {len(done)} (of {len(games)}); band +-{BAND} cp; every opponent a BOT: "
      f"{all(g['opp_title'] == 'BOT' for g in done)}")

# K per pool and day, from the games whose rating change is well defined (|S - E| >= 0.2).
kday = collections.defaultdict(list)
for g in done:
    if g["event"].startswith("rated") and g["our_diff"] is not None and abs(S(g) - E(g)) >= 0.2:
        kday[(pool(g), g["date"])].append(g["our_diff"] / (S(g) - E(g)))
kpool = collections.defaultdict(list)
for (p, day), ks in kday.items():
    kpool[p].extend(ks)


def K(g):
    if g["our_diff"] is not None and abs(S(g) - E(g)) >= 0.2:
        return g["our_diff"] / (S(g) - E(g))
    ks = kday.get((pool(g), g["date"])) or kpool.get(pool(g)) or [11.5]
    return statistics.median(ks)


def rd_bucket(g):
    x = g["opp_elo"] - g["our_elo"]
    if x < -150:
        return "a. opp 150+ weaker"
    if x < -50:
        return "b. opp 50-150 weaker"
    if x <= 50:
        return "c. within 50"
    if x <= 150:
        return "d. opp 50-150 stronger"
    return "e. opp 150+ stronger"


def pc_bucket(n):
    return "a. <=5 (tables)" if n <= 5 else "b. 6-10" if n <= 10 else "c. 11-20" if n <= 20 else "d. 21-32"


def inc_of(g):
    try:
        return int(g["tc"].split("+")[1])
    except Exception:
        return 0


def stretch(g, N, band=BAND, strict=False, max_pieces=99):
    """First trigger of an N-move level run: (ply index, pieces) or None."""
    run = 0
    for i, p in enumerate(g["plies"]):
        if not p[0]:
            continue
        cp = p[1]
        if cp is None:
            if strict and i >= 24:
                run = 0
            continue
        if abs(cp) <= band and p[3] <= max_pieces:
            run += 1
            if run >= N:
                return i, p[3]
        else:
            run = 0
    return None


def after(g, t):
    """What the game did after the trigger at ply t: our best and worst eval later (our view), plies left,
    minutes of play left (both clocks, plus increments)."""
    evs = [ours_view(g, p[1]) for p in g["plies"][t + 1:] if p[0] and p[1] is not None]
    best = max(evs) if evs else 0
    worst = min(evs) if evs else 0
    inc = inc_of(g)
    used = 0.0
    for side in (True, False):
        clk_t = None
        for p in g["plies"][:t + 1]:
            if p[0] == side and p[4] is not None:
                clk_t = p[4]
        later = [p for p in g["plies"][t + 1:] if p[0] == side and p[4] is not None]
        if clk_t is not None and later:
            used += clk_t - later[-1][4] + inc * len(later)
    return best, worst, len(g["plies"]) - t - 1, used / 60


def wdl(gs):
    c = collections.Counter(S(g) for g in gs)
    return c[1.0], c[0.5], c[0.0]


def table(title, header, rows):
    print()
    print("### " + title)
    print("| " + " | ".join(header) + " |")
    print("|" + "---|" * len(header))
    for r in rows:
        print("| " + " | ".join(str(x) for x in r) + " |")


# The bot's evaluation eras: "early" = its first days on the hand-made / Texel evaluation (~1950-2250 blitz);
# "networks" = from nnue1's start on the bot, 2026-10-17 04:50 local = 2026-10-16 17:50 UTC (~2250-2650 blitz).
NET = "2026.10.16 17:50"


def era(g):
    return "networks" if f"{g['date']} {g['time'][:5]}" >= NET else "early"


if __name__ == "__main__":
    # 1. How many games have a level stretch, by length and by the pieces on the board at the trigger.
    rows = []
    for N in (10, 20, 30):
        trig = [(g, stretch(g, N)) for g in done]
        trig = [(g, t) for g, t in trig if t]
        pcs = collections.Counter(pc_bucket(t[1]) for g, t in trig)
        rows.append([N, f"{len(trig)} ({100 * len(trig) / len(done):.0f}%)"] +
                    [pcs[b] for b in ("a. <=5 (tables)", "b. 6-10", "c. 11-20", "d. 21-32")])
    table(f"Games with N of our moves in a row within +-{BAND / 100:.2f} (of {len(done)} finished)",
          ["N", "games", "<=5 pieces (tables)", "6-10", "11-20", "21-32"], rows)

    # 2/3. How those games ended, by rating difference and by pieces, plus the rating points.
    for N in (10, 20, 30):
        trig = [(g, stretch(g, N)) for g in done]
        trig = [(g, t) for g, t in trig if t]
        for name, key in (("rating difference (opponent - us)", lambda g, t: rd_bucket(g)),
                          ("pieces at the trigger", lambda g, t: pc_bucket(t[1]))):
            groups = collections.defaultdict(list)
            for g, t in trig:
                groups[key(g, t)].append((g, t))
            rows = []
            tot = collections.Counter()
            for k in sorted(groups):
                gs = [g for g, t in groups[k]]
                w, dr, l = wdl(gs)
                score = sum(S(g) for g in gs) / len(gs)
                exp = sum(E(g) for g in gs) / len(gs)
                draw_pts = sum(K(g) * (0.5 - E(g)) for g in gs)          # what a draw would have scored
                act_pts = sum(K(g) * (S(g) - E(g)) for g in gs)          # what the games scored
                rows.append([k, len(gs), f"{w} / {dr} / {l}", f"{100 * score:.0f}%", f"{100 * exp:.0f}%",
                             f"{act_pts:+.0f}", f"{draw_pts:+.0f}", f"{draw_pts - act_pts:+.0f}"])
                tot["n"] += len(gs)
                tot["w"] += w
                tot["d"] += dr
                tot["l"] += l
                tot["act"] += act_pts
                tot["draw"] += draw_pts
                tot["s"] += sum(S(g) for g in gs)
                tot["e"] += sum(E(g) for g in gs)
            rows.append(["all", tot["n"], f"{tot['w']} / {tot['d']} / {tot['l']}", f"{100 * tot['s'] / tot['n']:.0f}%",
                         f"{100 * tot['e'] / tot['n']:.0f}%", f"{tot['act']:+.0f}", f"{tot['draw']:+.0f}",
                         f"{tot['draw'] - tot['act']:+.0f}"])
            table(f"N = {N}: outcome by {name}",
                  ["group", "games", "W / D / L", "score", "expected", "rating pts as played",
                   "pts if drawn at trigger", "change"], rows)

    # 4. After the trigger: does someone pull ahead, and how much play is left?
    for N in (10, 20):
        trig = [(g, stretch(g, N)) for g in done]
        trig = [(g, t) for g, t in trig if t]
        rows = []
        for k in ("a. <=5 (tables)", "b. 6-10", "c. 11-20", "d. 21-32"):
            gs = [(g, t) for g, t in trig if pc_bucket(t[1]) == k]
            if not gs:
                continue
            res = [after(g, t[0]) for g, t in gs]
            we_ahead = sum(1 for b, w, n, m in res if b >= 150)
            they_ahead = sum(1 for b, w, n, m in res if w <= -150)
            losses_time = sum(1 for g, t in gs if S(g) == 0 and g["termination"] == "Time forfeit")
            wins_time = sum(1 for g, t in gs if S(g) == 1 and g["termination"] == "Time forfeit")
            rows.append([k, len(gs), we_ahead, they_ahead, wins_time, losses_time,
                         f"{statistics.median(n for b, w, n, m in res):.0f}",
                         f"{statistics.median(m for b, w, n, m in res):.1f}",
                         f"{sum(m for b, w, n, m in res):.0f}"])
        table(f"N = {N}: after the trigger (eval later reached +-1.50 for us / them; minutes of play left)",
              ["pieces", "games", "we got +1.50", "they got +1.50", "won on time", "lost on time",
               "median plies left", "median minutes left", "total minutes"], rows)

    # 5. lichess-bot's own rule as configured: |score| <= 0 for 10 engine moves in a row, <= 10 pieces;
    #    a move with no score (pre-P1 ponder hit) breaks the run.
    fires = [(g, stretch(g, 10, band=0, strict=True, max_pieces=10)) for g in done]
    fires = [(g, t) for g, t in fires if t]
    lenient = [(g, stretch(g, 10, band=0, strict=False, max_pieces=10)) for g in done]
    lenient = [(g, t) for g, t in lenient if t]
    print()
    print(f"lichess-bot's rule (0 cp, 10 moves, <=10 pieces): fires in {len(fires)} games; "
          f"{len(lenient)} if no-score moves didn't break the run")
    print("  pieces at first fire:", dict(collections.Counter(pc_bucket(t[1]) for g, t in fires)))
    print("  outcome W/D/L:", wdl([g for g, t in fires]))

    # 6. The logs: offers both ways.
    seen = set(d["seen"])
    byid = {g["id"]: g for g in games}
    ours_offer = collections.Counter()
    theirs = collections.Counter()
    declined_games = []
    accepted_ours = []
    accepted_theirs = []
    offer_evals = []
    for gid, evs in events.items():
        g = byid.get(gid)
        if not g:
            continue
        me = g["colour"].capitalize()
        last_offer = None
        declined = 0
        for e in evs:
            msg = e["msg"]
            if msg.endswith("offers draw"):
                last_offer = "us" if msg.startswith(me) else "them"
                (ours_offer if last_offer == "us" else theirs)["offers"] += 1
                if last_offer == "them":
                    offer_evals.append((e["our_eval"], e["move"], g))
            elif msg.endswith("declines draw"):
                if msg.startswith(me):
                    theirs["declined by us"] += 1
                    declined += 1
                else:
                    ours_offer["declined by them"] += 1
            elif msg == "Draw offer accepted":
                if last_offer == "us":
                    ours_offer["accepted"] += 1
                    accepted_ours.append(g)
                else:
                    theirs["accepted by us"] += 1
                    accepted_theirs.append(g)
        if declined:
            declined_games.append((g, declined))
    print()
    print("games in the logs:", sum(g["id"] in seen for g in done), "of", len(done))
    print("our offers:", dict(ours_offer), " games where we offered:",
          sum(1 for gid, evs in events.items() if gid in byid and any(
              e["msg"] == byid[gid]["colour"].capitalize() + " offers draw" for e in evs)))
    print("their offers:", dict(theirs))
    print("games where we declined theirs:", len(declined_games), "W/D/L", wdl([g for g, n in declined_games]),
          " rating diff buckets:", dict(collections.Counter(rd_bucket(g) for g, n in declined_games)))
    for g, n in sorted(declined_games, key=lambda x: x[0]["opp_elo"] - x[0]["our_elo"]):
        print(f"  {g['id']} {g['date']} vs {g['opponent']} ({g['opp_elo'] - g['our_elo']:+d}) declined {n}x, "
              f"result {S(g)} {g['termination']}, pts as played {K(g) * (S(g) - E(g)):+.1f}, if drawn {K(g) * (0.5 - E(g)):+.1f}")
    ev_hist = collections.Counter()
    for ev, mv, g in offer_evals:
        if ev is None:
            ev_hist["no eval (book / ponder hit)"] += 1
            continue
        if ev.startswith("#"):
            ev_hist["mate " + ("for us" if not ev.startswith("#-") else "against us")] += 1
            continue
        x = float(ev)
        ev_hist["<= -1.50" if x <= -1.5 else "-1.49..-0.16" if x < -0.15 else "+-0.15" if x <= 0.15 else
                "+0.16..+1.49" if x < 1.5 else ">= +1.50"] += 1
    print("our eval when they offered:", dict(ev_hist))
    print("accepted (ours):", [(g["id"], g["opponent"], g["opp_elo"] - g["our_elo"]) for g in accepted_ours])
    print("accepted (theirs):", [(g["id"], g["opponent"], g["opp_elo"] - g["our_elo"]) for g in accepted_theirs])
    # which opponents offer (lichess-bot bots with the default rule?)
    offerers = collections.Counter(g["opponent"] for ev, mv, g in offer_evals)
    print("opponents who offered:", offerers.most_common(30))
