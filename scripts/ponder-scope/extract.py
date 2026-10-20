"""Pick hit and miss sequences from the bot's log for the lab runs (bot venv python, for python-chess).
usage: python extract.py <log> <out.json> [count each]
"""
import json
import os
import sys

import chess

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from ponderlog import parse  # noqa: E402


def moves_of(position_line):
    w = position_line.split()
    return w[w.index("moves") + 1:] if "moves" in w else []


def board_of(position_line):
    w = position_line.split()
    if w[1] == "startpos":
        b = chess.Board()
    else:
        end = w.index("moves") if "moves" in w else len(w)
        b = chess.Board(" ".join(w[2:end]))
    for m in moves_of(position_line):
        b.push_uci(m)
    return b


def clock(go_line):
    w = go_line.split()
    keep = []
    for key in ("wtime", "btime", "winc", "binc"):
        if key in w:
            keep += [key, w[w.index(key) + 1]]
    return " ".join(keep)


def main():
    searches = parse([sys.argv[1]])
    count = int(sys.argv[3]) if len(sys.argv) > 3 else 12
    hits, misses = [], []
    for i, s in enumerate(searches):
        if s["kind"] != "ponder" or not s["end"] or not s["position"]:
            continue
        prev = next((t for t in reversed(searches[:i]) if t["pid"] == s["pid"]), None)
        if prev is None or not prev["position"]:
            continue
        a, p = moves_of(prev["position"]), moves_of(s["position"])
        if p[:-2] != a or len(a) < 16:
            continue
        board_p = board_of(s["position"])
        if len(board_p.piece_map()) < 12 or board_p.is_game_over():
            continue
        if prev["infos"] and prev["infos"][-1][1] <= 2:
            continue   # an endgame-table move
        row = {"A": prev["position"], "P": s["position"], "clockP": clock(s["go"]),
               "bot_ponder_ms": round(1000 * ((s["hit"] or s.get("stopped") or s["end"]) - s["start"])),
               "bot_depth": s["infos"][-1][1] if s["infos"] else None, "pieces": len(board_p.piece_map())}
        if s["hit"]:
            hits.append(row)
        elif s.get("stopped"):
            nxt = next((t for t in searches[i + 1:] if t["pid"] == s["pid"]), None)
            if nxt is None or nxt["kind"] != "go" or not nxt["position"]:
                continue
            b = moves_of(nxt["position"])
            if b[:-1] != a + p[-2:-1] or b == p:
                continue
            row["B"] = nxt["position"]
            row["clockB"] = clock(nxt["go"])
            row["actual"] = b[-1]
            row["predicted"] = p[-1]
            misses.append(row)
    pick = lambda rows: [rows[int(k * len(rows) / count)] for k in range(count)] if len(rows) > count else rows
    out = {"hits": pick(hits), "misses": pick(misses)}
    print(f"candidates: {len(hits)} hits, {len(misses)} misses; picked {len(out['hits'])} and {len(out['misses'])}")
    with open(sys.argv[2], "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, indent=1)


if __name__ == "__main__":
    main()
