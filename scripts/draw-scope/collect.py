"""Collect the bot's games and draw-offer events into one JSON for the draw-offer scoping.

    python -I collect.py <bot folder> <out.json>

From bot/games/*.pgn (lichess-bot's records): headers, our colour, and per ply the mover, our evaluation
(the %eval lichess-bot writes at the end of the PV line, White's view, in centipawns; mate as +-100000),
the pieces on the board before the move, and the mover's clock after it.
From bot/logs/bot-*.log: the Lichess system chat lines (offers / declines / accepted) per game, each with
our last "Evaluation:" and move number for that game at the time. Lines naming a token are never read out.
"""
import glob
import io
import json
import os
import re
import sys

import chess
import chess.pgn

bot, out = sys.argv[1], sys.argv[2]
ME = "matthewcarven"


def read_text(path):
    data = open(path, "rb").read()
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return data.decode("cp1252")


def node_eval(node):
    """Our evaluation for the move that led to `node`: on the node itself, or at the end of a PV variation
    that starts with the same move from the parent."""
    parent = node.parent
    for var in parent.variations[1:]:
        if var.move != node.move:
            continue
        end = var
        while end.variations:
            end = end.variations[0]
        sc = end.eval()
        if sc is not None:
            return sc, end.eval_depth()
    sc = node.eval()
    if sc is not None:
        return sc, node.eval_depth()
    return None, None


def score_cp(pov):
    w = pov.white()
    if w.is_mate():
        return 100000 if w.mate() > 0 else -100000
    return w.score()


games = []
for path in sorted(glob.glob(os.path.join(bot, "games", "*.pgn"))):
    g =chess.pgn.read_game(io.StringIO(read_text(path)))
    if g is None:
        continue
    h = g.headers
    if h.get("White") == ME:
        colour = "white"
    elif h.get("Black") == ME:
        colour = "black"
    else:
        continue
    opp = "Black" if colour == "white" else "White"
    us = "White" if colour == "white" else "Black"
    rec = {
        "id": h.get("GameId") or h.get("Site", "").rsplit("/", 1)[-1],
        "file": os.path.basename(path),
        "date": h.get("UTCDate"), "time": h.get("UTCTime"),
        "event": h.get("Event"), "tc": h.get("TimeControl"),
        "termination": h.get("Termination"), "result": h.get("Result"),
        "colour": colour, "opponent": h.get(opp),
        "opp_title": h.get(opp + "Title"),
        "our_elo": int(h[us + "Elo"]) if h.get(us + "Elo", "?").isdigit() else None,
        "opp_elo": int(h[opp + "Elo"]) if h.get(opp + "Elo", "?").isdigit() else None,
        "our_diff": int(h[us + "RatingDiff"]) if re.fullmatch(r"[+-]?\d+", h.get(us + "RatingDiff", "")) else None,
        "plies": [],
    }
    board = g.board()
    node = g
    ply = 0
    while node.variations:
        nxt = node.variations[0]
        pieces = chess.popcount(board.occupied)
        mover = "white" if board.turn == chess.WHITE else "black"
        ev, depth = node_eval(nxt) if mover == colour else (None, None)
        clk = nxt.clock()
        rec["plies"].append([mover == colour, score_cp(ev) if ev is not None else None, depth, pieces,
                             round(clk, 1) if clk is not None else None])
        board.push(nxt.move)
        node = nxt
        ply += 1
    rec["final_pieces"] = chess.popcount(board.occupied)
    games.append(rec)

# The logs: per game, the system chat lines about draws, with our last evaluation for that game.
url = re.compile(r"https://lichess\.org/(\w{8})/(white|black) \[player\] lichess: (.*)$")
search = re.compile(r"Searching for .* for game (\w{8})")
book = re.compile(r"Got move \S+ from book .* for game (\w{8})")
events = {}
current = None
last_eval = {}
move_no = None
last_move_no = {}
seen = set()
for path in sorted(glob.glob(os.path.join(bot, "logs", "bot-*.log"))):
    for line in read_text(path).splitlines():
        low = line.lower()
        if "token" in low or "authoriz" in low:
            continue
        m = re.search(r"INFO move: (\d+)$", line)
        if m:
            move_no = int(m.group(1))
            continue
        m = search.search(line) or book.search(line)
        if m:
            current = m.group(1)
            seen.add(current)
            last_move_no[current] = move_no
            if "from book" in line:
                last_eval[current] = None
            continue
        m = re.search(r"INFO Evaluation: (.*)$", line)
        if m and current:
            last_eval[current] = m.group(1).strip()
            continue
        m = url.search(line)
        if m:
            gid, col, msg = m.groups()
            if "draw" not in msg.lower():
                continue
            events.setdefault(gid, []).append({
                "t": line[:19], "colour": col, "msg": msg,
                "our_eval": last_eval.get(gid), "move": last_move_no.get(gid)})

json.dump({"games": games, "events": events, "seen": sorted(seen)},open(out, "w", encoding="utf-8"), separators=(",", ":"))
print(len(games), "games;", len(events), "games with draw chat;",
      sum(len(v) for v in events.values()), "draw lines")
