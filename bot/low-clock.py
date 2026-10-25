"""How often the bot runs low on its clock, when, and what it costs: from the clocks lichess.org writes into each PGN.

    python bot\\low-clock.py [games folder] [--since YYYY-MM-DD]

Reads every .pgn in bot\\games (the bot's own games, one a file; read only). The clock after each ply is its [%clk]
(whole seconds); the pieces on the board are 32 less the captures so far (the mainline's SAN moves with an "x").
Variations (the bot's lines, in brackets) are skipped.

By time control (bullet / blitz / rapid and classical, each with and without increment):
  - how many games the bot drops below 30 s and below 10 s, at what move and with how many pieces on; the same for
    the opponents; and the opponent's clock at the moment the bot first drops below 30 s;
  - the score in games that reach low time against games that don't, beside the score the ratings expect;
  - both clocks at moves 10, 20, ... (same games), and seconds a move by phase (pieces on the board);
  - time losses: each one, with the clock the loser had after its last move (a lot left = it stopped answering,
    a stall or a crash, not a scramble).
--since is a local date (the PGN's UTCDate/UTCTime are converted, as in move-times.py).
"""
import csv
import datetime
import os
import re
import statistics
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ME = "matthewcarven"
TAG = re.compile(r'^\[(\w+) "(.*)"\]$', re.M)
CLK = re.compile(r"\[%clk (\d+):(\d\d):(\d\d(?:\.\d+)?)\]")
TOKEN = re.compile(r"\{[^}]*\}|\(|\)|[^\s(){}]+")
MOVE_NUMBER = re.compile(r"^\d+\.+$")
RESULTS = {"1-0", "0-1", "1/2-1/2", "*"}
PHASES = ((32, 29), (28, 21), (20, 13), (12, 7), (6, 0))   # pieces on the board before the move


def read(path):
    """One game: its tags, and its mainline as [(san, clock after the ply in seconds or None)]."""
    with open(path, encoding="utf-8", errors="replace") as f:
        text = f.read()
    tags = dict(TAG.findall(text))
    body = text[text.find("\n\n"):] if "\n\n" in text else text
    plies, depth = [], 0
    for tok in TOKEN.findall(body):
        if tok == "(":
            depth += 1
        elif tok == ")":
            depth -= 1
        elif depth:
            continue
        elif tok.startswith("{"):
            m = CLK.search(tok)
            if m and plies and plies[-1][1] is None:
                plies[-1] = (plies[-1][0], int(m[1]) * 3600 + int(m[2]) * 60 + float(m[3]))
        elif not MOVE_NUMBER.match(tok) and tok not in RESULTS and not tok.startswith("$"):
            plies.append((tok, None))
    return tags, plies


def game_record(path, record):
    tags, plies = read(path)
    if ME not in (tags.get("White", "").lower(), tags.get("Black", "").lower()):
        return None
    tc = tags.get("TimeControl", "")
    if "+" not in tc or len(plies) < 4 or any(c is None for _, c in plies):
        return None
    base, inc = (int(x) for x in tc.split("+"))
    event = tags.get("Event", "")
    speed = next((s for s in ("bullet", "blitz", "rapid", "classical") if s in event), None)
    if speed is None:
        return None
    us = 0 if tags["White"].lower() == ME else 1
    clocks = [c for _, c in plies]
    pieces, caps = [], 0
    for san, _ in plies:
        pieces.append(32 - caps)          # before the ply
        caps += "x" in san
    result = tags.get("Result", "*")
    score = {"1-0": 1.0, "0-1": 0.0, "1/2-1/2": 0.5}.get(result)
    if score is not None and us == 1:
        score = 1 - score
    try:
        our_elo = int(tags["WhiteElo" if us == 0 else "BlackElo"])
        opp_elo = int(tags["BlackElo" if us == 0 else "WhiteElo"])
    except (KeyError, ValueError):
        our_elo = opp_elo = None
    utc = datetime.datetime.strptime(f"{tags.get('UTCDate', '')} {tags.get('UTCTime', '')}", "%Y.%m.%d %H:%M:%S")
    rec = record.get(tags.get("GameId", ""), {})
    return {"id": tags.get("GameId", os.path.basename(path)), "tags": tags, "base": base, "inc": inc,
            "speed": speed, "us": us, "clocks": clocks, "pieces": pieces, "score": score,
            "termination": tags.get("Termination", ""), "our_elo": our_elo, "opp_elo": opp_elo,
            "rated": "rated" in event, "start": utc.replace(tzinfo=datetime.timezone.utc),
            "beside": rec.get("beside", ""), "plies": len(plies)}


def tc_class(g):
    speed = "rapid+" if g["speed"] in ("rapid", "classical") else g["speed"]
    return f"{speed} {'+inc' if g['inc'] else 'no inc'}"


def first_below(g, side, limit):
    """The first ply after which `side`'s clock is below `limit` seconds (None: never)."""
    for i in range(side, len(g["clocks"]), 2):
        if g["clocks"][i] < limit:
            return i
    return None


def spent(g):
    """[(ply, seconds)] for every ply from each side's second move on (lichess starts the clocks after both moved)."""
    c = g["clocks"]
    return [(i, c[i - 2] + g["inc"] - c[i]) for i in range(2, len(c))]


def expected(g):
    if g["our_elo"] is None or not g["rated"] or g["our_elo"] == 3000:   # 3000: Lichess's provisional start
        return None
    return 1 / (1 + 10 ** ((g["opp_elo"] - g["our_elo"]) / 400))


def pct(n, d):
    return f"{100 * n / d:.0f}%" if d else "-"


def med(xs, fmt="{:.0f}"):
    return fmt.format(statistics.median(xs)) if xs else "-"


def score_row(label, sel):
    scored = [g for g in sel if g["score"] is not None]
    rated = [g for g in scored if expected(g) is not None]
    if not scored:
        return f"| {label} | 0 | - | - | - |"
    s = 100 * statistics.mean(g["score"] for g in scored)
    if rated:
        diff = 100 * statistics.mean(g["score"] - expected(g) for g in rated)
        exp = 100 * statistics.mean(expected(g) for g in rated)
        return f"| {label} | {len(scored)} | {s:.0f}% | {exp:.0f}% | {diff:+.0f} |"
    return f"| {label} | {len(scored)} | {s:.0f}% | - | - |"


def main(args):
    folder = os.path.join(HERE, "games")
    since = None
    rest = list(args)
    if "--since" in rest:
        i = rest.index("--since")
        since = datetime.datetime.strptime(rest[i + 1], "%Y-%m-%d").astimezone()
        del rest[i:i + 2]
    if rest:
        folder = rest[0]
    record = {}
    if os.path.exists(os.path.join(folder, "record.csv")):
        with open(os.path.join(folder, "record.csv"), encoding="utf-8", newline="") as f:
            record = {r["id"]: r for r in csv.DictReader(f)}

    games = []
    for name in sorted(os.listdir(folder)):
        if name.endswith(".pgn"):
            g = game_record(os.path.join(folder, name), record)
            if g and (since is None or g["start"] >= since):
                games.append(g)
    played = [g for g in games if g["termination"] not in ("Abandoned", "Unterminated") and g["plies"] >= 20]
    classes = sorted({tc_class(g) for g in played}, key=lambda c: (["bullet", "blitz", "rapid+"].index(c.split()[0]), c))

    print(f"{len(games)} games with clocks in {folder}; {len(played)} played out (10+ moves, not aborted)")
    print()
    print("## How often each side gets low, and when (first time below the line)\n")
    print("| clock | games | ours <30 s | move | pieces | theirs <30 s | ours <10 s | move | pieces | theirs <10 s "
          "| their clock at our first <30 s | they had more |")
    print("|---|---|---|---|---|---|---|---|---|---|---|---|")
    for c in classes + ["all"]:
        sel = [g for g in played if c == "all" or tc_class(g) == c]
        row = [c, str(len(sel))]
        opp_at = []
        for limit in (30, 10):
            ours = [(g, first_below(g, g["us"], limit)) for g in sel if g["base"] > limit]
            hit = [(g, i) for g, i in ours if i is not None]
            theirs = [g for g in sel if g["base"] > limit and first_below(g, 1 - g["us"], limit) is not None]
            row += [f"{len(hit)} ({pct(len(hit), len(ours))})", med([i // 2 + 1 for _, i in hit]),
                    med([g["pieces"][i] for g, i in hit]), f"{len(theirs)} ({pct(len(theirs), len(ours))})"]
            if limit == 30:
                opp_at = [g["clocks"][i - 1] for g, i in hit if i >= 1]
        row += [f"{med(opp_at)} s", pct(sum(o > 30 for o in opp_at), len(opp_at))]
        print("| " + " | ".join(row) + " |")
    print("\n(move: the bot's move number then, median; pieces: on the board then, median; a line at or above the "
          "starting clock is left out, so 30+x games count for neither and 0:30-0:59 starts not for 30 s)")

    print("\n## Score by how low the bot's clock got (played-out games)\n")
    print("Expected: from the two ratings (rated games, the provisional 3000 left out); diff: score less expected, "
          "in points of %, over the rated games.\n")
    for c in classes + ["all"]:
        sel = [g for g in played if c == "all" or tc_class(g) == c]
        print(f"### {c}\n")
        print("| bot's lowest clock | games | score | expected | diff |")
        print("|---|---|---|---|---|")
        low10 = [g for g in sel if g["base"] > 10 and first_below(g, g["us"], 10) is not None]
        low30 = [g for g in sel if g["base"] > 30 and first_below(g, g["us"], 30) is not None and g not in low10]
        rest_ = [g for g in sel if g not in low10 and g not in low30]
        print(score_row("below 10 s", low10))
        print(score_row("10-30 s", low30))
        print(score_row("never below 30 s", rest_))
        long_ = [g for g in sel if g["plies"] >= 100]
        print(score_row("60+ moves only: below 30 s", [g for g in long_ if g in low10 or g in low30]))
        print(score_row("60+ moves only: never below 30 s", [g for g in long_ if g not in low10 and g not in low30]))
        print()

    print("## Both clocks at move N (seconds, median; same games, those that reach move N)\n")
    marks = (10, 20, 30, 40, 50, 60, 80)
    print("| clock | " + " | ".join(f"move {m}" for m in marks) + " |")
    print("|---|" + "---|" * len(marks))
    for c in classes:
        sel = [g for g in played if tc_class(g) == c]
        cells = []
        for m in marks:
            pairs = []
            for g in sel:
                i = 2 * (m - 1) + g["us"]     # the bot's m-th move
                j = 2 * (m - 1) + 1 - g["us"]
                if max(i, j) < len(g["clocks"]):
                    pairs.append((g["clocks"][i], g["clocks"][j]))
            if len(pairs) >= 5:
                cells.append(f"{med([p[0] for p in pairs])} v {med([p[1] for p in pairs])} ({len(pairs)})")
            else:
                cells.append("-")
        print(f"| {c} | " + " | ".join(cells) + " |")
    print("\n(ours v theirs, after each side's move N, increment included; games in brackets)")

    print("\n## Seconds a move by phase (pieces on the board before the move), mean (median)\n")
    print("| clock | " + " | ".join(f"{hi}-{lo} pieces" for hi, lo in PHASES) + " | share of all our time used in "
          "the first 20 moves |")
    print("|---|" + "---|" * (len(PHASES) + 1))
    for c in classes:
        sel = [g for g in played if tc_class(g) == c]
        cells = []
        for hi, lo in PHASES:
            ours = [s for g in sel for i, s in spent(g) if i % 2 == g["us"] and lo <= g["pieces"][i] <= hi]
            theirs = [s for g in sel for i, s in spent(g) if i % 2 != g["us"] and lo <= g["pieces"][i] <= hi]
            if len(ours) >= 20:
                cells.append(f"{statistics.mean(ours):.1f} ({statistics.median(ours):.0f}) v "
                             f"{statistics.mean(theirs):.1f} ({statistics.median(theirs):.0f})")
            else:
                cells.append("-")
        early = []
        for g in sel:
            ours = [(i, s) for i, s in spent(g) if i % 2 == g["us"]]
            total = sum(s for _, s in ours)
            if total > 0 and len(ours) >= 40:
                early.append(100 * sum(s for i, s in ours if i // 2 + 1 <= 20) / total)
        cells.append(f"{med(early)}% (games of 40+ moves: {len(early)})")
        print(f"| {c} | " + " | ".join(cells) + " |")
    print("\n(ours v theirs; book moves are instant and the tables' (5 pieces or fewer) nearly so)")

    print("\n## Time losses\n")
    print("| game | date (UTC) | clock | lost by | score | move | pieces | loser's clock after its last move "
          "| winner's clock | other games on |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    for g in games:
        if g["termination"] != "Time forfeit":
            continue
        n = len(g["clocks"])
        loser = n % 2                      # the side to move when the clock ran out
        loser_last = g["clocks"][loser::2][-1]
        winner_last = g["clocks"][1 - loser::2][-1]
        who = "us" if loser == g["us"] else "them"
        score = {1.0: "1", 0.5: "1/2", 0.0: "0"}.get(g["score"], "?")
        clock = f"{g['base'] / 60:g}" if g["base"] >= 60 else f"{g['base']}s"
        print(f"| {g['id']} | {g['start']:%m-%d %H:%M} | {clock}+{g['inc']} | {who} | {score} | {n // 2 + 1} | {g['pieces'][-1]} | {loser_last:.0f} s | "
              f"{winner_last:.0f} s | {g['beside'] or '?'} |")
    print("\n(lost by: the side whose clock ran out, the side to move at the end; a loser with plenty left after its "
          "last move stopped answering: a stall or a crash, not a scramble)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
