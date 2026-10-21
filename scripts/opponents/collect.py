"""One row per game the bot has played, with the setup that played it: the backfill of P8's game record.

    python -I collect.py <bot folder> <out.csv>

From bot/games/*.pgn (lichess-bot's records, headers only): the game, the opponent, the ratings, the result.
From bot/logs/bot-*.log (lichess-bot's INFO log, read in order): which evaluation the engine loaded at each start
(the engine's "info string evaluation" line during "Checking engine configuration"; no such line = the built-in
numbers), so each game gets the setup of the start that played it; who sent the challenge; how many of our moves came
from each book; and how many other games were running beside it. Lines naming a token are never read.
"""
import csv
import glob
import os
import re
import sys

bot, out = sys.argv[1], sys.argv[2]
ME = "matthewcarven"


def read_text(path):
    data = open(path, "rb").read()
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return data.decode("cp1252")


# The logs, in order: starts, nets, games.
starts = []           # (local time, net name, sha)
game = {}             # id -> facts from the log
running = set()       # games in progress at this point of the log
net, sha = None, None
checking = False
r_check = re.compile(r"^(\S+ \S+),\d+ .*INFO Checking engine configuration")
r_ok = re.compile(r"^(\S+ \S+),\d+ .*INFO Engine configuration OK")
r_net = re.compile(r"info string evaluation: network .*\\\\([^\\]+?)\.nnue \((.*?), sha256 ([0-9a-f]+)\)")
r_txt = re.compile(r"info string evaluation from .*\\\\([^\\]+?)'")
r_accept = re.compile(r"INFO Accept .* challenge from .* \((\w{8})\)$")
r_ours = re.compile(r"INFO Challenge id is (\w{8})\.")
r_start = re.compile(r"^(\S+ \S+),\d+ .*INFO \+\+\+ https://lichess\.org/(\w{8})/(white|black)")
r_over = re.compile(r"^(\S+ \S+),\d+ .*INFO --- https://lichess\.org/(\w{8})/\w+ Game over")
r_book = re.compile(r"INFO Got move \S+ from book .*\\([^\\]+)\.bin for game (\w{8})")
challenged_by = {}
for path in sorted(glob.glob(os.path.join(bot, "logs", "bot-*.log"))):
    for line in read_text(path).splitlines():
        low = line.lower()
        if "token" in low or "authoriz" in low:
            continue
        m = r_check.search(line)
        if m:
            checking, net, sha = True, "built-in", ""
            start_time = m.group(1)
            running.clear()      # a new start: games of the last one are over or will be rejoined below
            continue
        if checking:
            m = r_net.search(line)
            if m:
                net, sha = m.group(1), m.group(3)
                continue
            m = r_txt.search(line)
            if m:
                net, sha = m.group(1).rsplit(".", 1)[0], ""
                continue
            m = r_ok.search(line)
            if m:
                checking = False
                starts.append((start_time, net, sha))
            continue
        m = r_accept.search(line)
        if m:
            challenged_by[m.group(1)] = "them"
            continue
        m = r_ours.search(line)
        if m:
            challenged_by[m.group(1)] = "us"
            continue
        m = r_start.search(line)
        if m:
            gid = m.group(2)
            g = game.setdefault(gid, {"net": net, "sha": sha, "bot_start": start_time, "log_start": m.group(1),
                                      "beside": 0, "books": {}, "segments": 0})
            g["segments"] += 1
            if g["net"] != net:
                g["net"] = g["net"] + "+" + net      # rejoined after a restart on another net
            for other in running:
                game[other]["beside"] = max(game[other]["beside"], len(running))
            g["beside"] = max(g["beside"], len(running))
            running.add(gid)
            continue
        m = r_over.search(line)
        if m:
            running.discard(m.group(2))
            continue
        m = r_book.search(line)
        if m and m.group(2) in game:
            b = game[m.group(2)]["books"]
            b[m.group(1)] = b.get(m.group(1), 0) + 1

# The games.
hdr = re.compile(r'^\[(\w+) "(.*)"\]\s*$', re.M)
rows = []
for path in sorted(glob.glob(os.path.join(bot, "games", "*.pgn"))):
    text = read_text(path)
    h = dict(hdr.findall(text))
    if h.get("White") == ME:
        colour, us, them = "white", "White", "Black"
    elif h.get("Black") == ME:
        colour, us, them = "black", "Black", "White"
    else:
        continue
    gid = h.get("GameId") or h.get("Site", "").rsplit("/", 1)[-1]
    res = h.get("Result")
    score = {"1-0": 1.0, "0-1": 0.0, "1/2-1/2": 0.5}.get(res)
    if score is not None and colour == "black":
        score = 1.0 - score
    tags = list(hdr.finditer(text))
    body = text[tags[-1].end():] if tags else text
    body = re.sub(r"\{[^}]*\}", " ", body)
    while "(" in body:                        # our PVs, which run past the game's last move
        body, n = re.subn(r"\([^()]*\)", " ", body)
        if not n:
            break
    nums = [int(n) for n in re.findall(r"(\d+)\.", body)]
    sans = [t for t in re.sub(r"\d+\.(\.\.)?", " ", body).split() if t not in ("1-0", "0-1", "1/2-1/2", "*")]
    g = game.get(gid, {})
    books = g.get("books", {})
    ev = h.get("Event", "")
    rows.append({
        "id": gid,
        "utc": h.get("UTCDate", "").replace(".", "-") + " " + h.get("UTCTime", ""),
        "rated": "rated" if ev.startswith("rated") else "casual",
        "speed": ev.split()[1] if len(ev.split()) > 2 else "",
        "tc": h.get("TimeControl", ""),
        "colour": colour,
        "opponent": h.get(them, ""),
        "opp_title": h.get(them + "Title", ""),
        "our_elo": h.get(us + "Elo", ""),
        "opp_elo": h.get(them + "Elo", ""),
        "our_diff": h.get(us + "RatingDiff", ""),
        "score": "" if score is None else score,
        "termination": h.get("Termination", ""),
        "moves": max(nums) if nums else 0,
        "challenger": challenged_by.get(gid, ""),
        "net": g.get("net", ""),
        "net_sha": g.get("sha", ""),
        "bot_start": g.get("bot_start", ""),
        "log_start": g.get("log_start", ""),
        "beside": g.get("beside", ""),
        "book_main": books.get("lichess-2200", 0) + books.get("lichess-2026-09-2200", 0),
        "book_ours": books.get("ours", 0),
        "book_wide": books.get("lichess-2200-wide", 0),
        "eco": h.get("ECO", ""),
        "opening": h.get("Opening", ""),
        "line": " ".join(sans[:20]),                    # the first 20 plies, to see who repeats an opening
    })
rows.sort(key=lambda r: r["utc"])
with open(out, "w", newline="", encoding="utf-8") as f:
    w = csv.DictWriter(f, fieldnames=list(rows[0]))
    w.writeheader()
    w.writerows(rows)
print(len(rows), "games;", sum(1 for r in rows if r["net"]), "matched to a start;", len(starts), "starts")
for s in starts:
    print("  start", *s)
