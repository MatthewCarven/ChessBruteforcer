"""Does the bot keep a plan from one move to the next?  One game, move by move, from lichess-bot's DEBUG log.

For each of our engine moves: the search that played it (a plain `go`, or a ponder that got `ponderhit`), every depth's
score and line, and the line (PV) our previous move expected.  Prints the tables in games/analysis/61InZpXn-plan.md.

usage: bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/ponder-scope/plan.py <DEBUG log> <game id> [--json out]
(python-chess is the bot venv's.  The log is bot/lichess-bot/lichess_bot_auto_logs/lichess-bot.log[.<date>].)
"""
import argparse
import ast
import json
import re
import statistics as st
import sys
from datetime import datetime

import chess

LINE = re.compile(r"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d,\d{3}) chess\.engine .*?<UciProtocol \(pid=(\d+)\)>: (<<|>>) (.*)$")
INFO = re.compile(r"^info depth (\d+) score (cp|mate) (-?\d+)( lowerbound)? nodes (\d+) nps \d+ time (\d+)"
                  r"(?: hashfull \d+)?(?: pv ?(.*))?$")
STATE = re.compile(r"DEBUG Game state: (\{.*\})\s*$")
MATE = 100000   # a mate's score in centipawns, for sorting and swings (mate in n: MATE - n)


def ts(text):
    return datetime.strptime(text, "%Y-%m-%d %H:%M:%S,%f").timestamp()


def centipawns(kind, value):
    if kind == "cp":
        return value
    return MATE - abs(value) if value > 0 else -(MATE - abs(value))


def parse(path, game_id):
    """The game's searches, and its last game state (moves, status, winner)."""
    searches, pid, state, started, position, current = [], None, None, False, None, None
    with open(path, encoding="utf-8", errors="replace") as f:
        for raw in f:
            raw = raw.rstrip("\n")
            if not started:
                if "'type': 'gameStart'" in raw and f"'gameId': '{game_id}'" in raw:
                    started = True
                continue
            if pid is None:
                m = LINE.match(raw)
                if m and m.group(4) == "Connection made":
                    pid = m.group(2)
                elif "Connection made" in raw:
                    pid = re.search(r"pid=(\d+)", raw).group(1)
                continue
            ms = STATE.search(raw)
            if ms:
                game_state = ast.literal_eval(ms.group(1))
                if game_state.get("type") == "gameState":
                    state = game_state
                continue
            m = LINE.match(raw)
            if not m or m.group(2) != pid:
                continue
            t, way, text = ts(m.group(1)), m.group(3), m.group(4)
            if way == "<<":
                if text.startswith("position"):
                    position = text
                elif text.startswith("go"):
                    words = position.split()
                    moves = words[words.index("moves") + 1:] if "moves" in words else []
                    current = {"kind": "ponder" if text.startswith("go ponder") else "go", "start": t, "go": text,
                               "moves": moves, "infos": [], "hit": None, "stopped": None, "end": None,
                               "bestmove": None, "ponder": None, "infos_before_hit": None}
                    searches.append(current)
                elif text == "ponderhit" and current:
                    current["hit"] = t
                    current["infos_before_hit"] = len(current["infos"])
                elif text == "stop" and current and current["stopped"] is None:
                    current["stopped"] = t
                elif text == "quit":
                    break
            elif current is not None:
                mi = INFO.match(text)
                if mi:
                    current["infos"].append({
                        "t": t, "depth": int(mi.group(1)), "cp": centipawns(mi.group(2), int(mi.group(3))),
                        "score": f"{mi.group(2)} {mi.group(3)}", "partial": bool(mi.group(4)),
                        "nodes": int(mi.group(5)), "ms": int(mi.group(6)), "pv": (mi.group(7) or "").split()})
                elif text.startswith("bestmove"):
                    words = text.split()
                    current["end"] = t
                    current["bestmove"] = words[1]
                    current["ponder"] = words[3] if len(words) > 3 else None
                    current = None
    return searches, state


def san_line(board, uci_moves, limit=None):
    b, out = board.copy(), []
    for i, u in enumerate(uci_moves[:limit] if limit else uci_moves):
        try:
            move = chess.Move.from_uci(u)
            if move not in b.legal_moves:
                out.append(f"({u}?)")
                break
            prefix = f"{b.fullmove_number}." if b.turn == chess.WHITE else (f"{b.fullmove_number}..." if i == 0 else "")
            out.append(prefix + b.san(move))
            b.push(move)
        except ValueError:
            out.append(f"({u}?)")
            break
    return " ".join(out)


def common_prefix(a, b):
    n = 0
    while n < len(a) and n < len(b) and a[n] == b[n]:
        n += 1
    return n


def last_full(infos):
    """The last finished depth's line (a P4 'lowerbound' line is a cut depth: skip it)."""
    full = [i for i in infos if not i["partial"]]
    return full[-1] if full else (infos[-1] if infos else None)


def at_depth(infos, depth):
    """The search's line at the deepest finished depth no deeper than `depth`."""
    found = [i for i in infos if not i["partial"] and i["depth"] <= depth]
    return found[-1] if found else None


def settle(infos, move, from_depth=6):
    """The depth from which the search's first move was `move` for good, and how often its first move changed
    from `from_depth` on (the early depths are noise)."""
    full = [i for i in infos if not i["partial"] and i["pv"]]
    settled, changes, last = None, 0, None
    for i in full:
        first = i["pv"][0]
        if i["depth"] >= from_depth and last is not None and first != last:
            changes += 1
        if first == move:
            settled = settled or i["depth"]
        else:
            settled = None
        last = first
    return settled, changes


def analyse(searches, state, our_white):
    moves = state["moves"].split()
    played = {}
    for s in searches:
        if s["bestmove"] and (s["kind"] == "go" or s["hit"]):
            played[len(s["moves"])] = s
    stopped = {len(s["moves"]): s for s in searches if s["kind"] == "ponder" and not s["hit"] and s["stopped"]}
    rows = []
    for n in sorted(played):
        s = played[n]
        board = chess.Board()
        for u in moves[:n]:
            board.push_uci(u)
        pieces = chess.popcount(board.occupied)
        final = s["infos"][-1] if s["infos"] else None
        full = last_full(s["infos"])
        row = {"ply": n, "move_no": n // 2 + 1, "move": s["bestmove"], "played_ok": n < len(moves) and moves[n] == s["bestmove"],
               "san": board.san(chess.Move.from_uci(s["bestmove"])), "pieces": pieces,
               "source": "hit" if s["kind"] == "ponder" else "go",
               "cp": final["cp"] if final else None, "score": final["score"] if final else None,
               "depth": final["depth"] if final else None, "pv": final["pv"] if final else [],
               "pv_san": san_line(board, final["pv"]) if final else "",
               "nodes": final["nodes"] if final else None, "ms": final["ms"] if final else None,
               "pv_len": len(final["pv"]) if final else 0,
               "capture": board.is_capture(chess.Move.from_uci(s["bestmove"])),
               "check": board.gives_check(chess.Move.from_uci(s["bestmove"])),
               "depths": [(i["depth"], i["pv"][0] if i["pv"] else None, i["cp"], i["partial"]) for i in s["infos"]]}
        settled, changes = settle(s["infos"], s["bestmove"])
        row["settled_at"], row["first_move_changes"] = settled, changes
        row["game_followed"] = common_prefix(moves[n:], row["pv"])   # plies of the line the game then played
        row["game_followed_all"] = row["game_followed"] == len(row["pv"]) and n + len(row["pv"]) <= len(moves)
        if s["kind"] == "ponder":
            k = s["infos_before_hit"]
            before = [i for i in s["infos"][:k] if not i["partial"]]
            row["pondered_ms"] = round(1000 * (s["hit"] - s["start"]))
            row["after_hit_ms"] = round(1000 * (s["end"] - s["hit"]))
            row["depth_at_hit"] = before[-1]["depth"] if before else 0
            row["move_at_hit"] = before[-1]["pv"][0] if before and before[-1]["pv"] else None
            row["cp_at_hit"] = before[-1]["cp"] if before else None
            row["depth_played"] = full["depth"] if full else None
            after = [i["depth"] for i in s["infos"][k:]]
            row["restarted"] = bool(after) and bool(before) and min(after) < before[-1]["depth"]
        else:
            row["used_ms"] = round(1000 * (s["end"] - s["start"]))
        prev = played.get(n - 2)
        if prev is not None and prev["infos"]:
            pfinal = prev["infos"][-1]
            ppv = pfinal["pv"]
            reply = moves[n - 1]
            before_reply = board.copy()
            before_reply.pop()
            row["reply_forcing"] = (before_reply.is_capture(chess.Move.from_uci(reply))
                                    or before_reply.gives_check(chess.Move.from_uci(reply)))
            row["reply_san"] = before_reply.san(chess.Move.from_uci(reply))
            row["predicted_san"] = san_line(before_reply, [prev["ponder"]]).split(".")[-1] if prev["ponder"] else "-"
            # Our planned move, in SAN, in the position it was planned for (after the reply we expected).
            planned_at = before_reply.copy()
            if prev["ponder"] and chess.Move.from_uci(prev["ponder"]) in planned_at.legal_moves:
                planned_at.push_uci(prev["ponder"])
                planned = ppv[2] if len(ppv) > 2 and ppv[1] == prev["ponder"] else None
                if planned and chess.Move.from_uci(planned) in planned_at.legal_moves:
                    row["plan_san"] = planned_at.san(chess.Move.from_uci(planned))
            row["prev_depth"] = pfinal["depth"]
            row["prev_pv_len"] = len(ppv)
            row["predicted_reply"] = prev["ponder"]
            row["reply"] = reply
            row["reply_as_predicted"] = prev["ponder"] == reply
            row["pv_reply"] = ppv[1] if len(ppv) > 1 else None
            row["pv_next"] = ppv[2] if len(ppv) > 2 and ppv[1] == reply else None
            row["pv_next_any"] = ppv[2] if len(ppv) > 2 else None
            row["agree_plies"] = common_prefix(row["pv"], ppv[2:]) if len(ppv) > 2 and ppv[1] == reply else None
            row["prev_cp"] = pfinal["cp"]
            # What the table handed this search: its line at the depth the previous search had searched this position
            # to (two plies shallower than its own last depth), found nearly free from the entries it left.
            inherited = at_depth(s["infos"], pfinal["depth"] - 2)
            row["inherited_depth"] = inherited["depth"] if inherited else None
            row["inherited_move"] = inherited["pv"][0] if inherited and inherited["pv"] else None
            row["inherited_cp"] = inherited["cp"] if inherited else None
            row["inherited_ms"] = inherited["ms"] if inherited else None
            if "plan_san" not in row and s["kind"] == "ponder" and row["inherited_move"]:
                # (the old line stopped short of our next move: the table's move stands in for it)
                row["plan_san"] = board.san(chess.Move.from_uci(row["inherited_move"]))
            # Where the move played took over for good: the score there against the depth before (a jump is a tactic
            # the deeper search found; a small shift is a close call settled the other way).
            full_infos = [i for i in s["infos"] if not i["partial"]]
            if settled and settled > 1:
                at = next(i for i in full_infos if i["depth"] == settled)
                before = [i for i in full_infos if i["depth"] < settled]
                row["switch_swing"] = at["cp"] - before[-1]["cp"] if before else None
            if n in stopped:
                miss = stopped[n]
                mfull = last_full(miss["infos"])
                row["miss_pondered_ms"] = round(1000 * (miss["stopped"] - miss["start"]))
                row["miss_depth"] = mfull["depth"] if mfull else 0
                row["miss_would_play"] = miss["bestmove"]
        rows.append(row)
    return rows, moves


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("log")
    ap.add_argument("game")
    ap.add_argument("--json")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    searches, state = parse(args.log, args.game)
    if not searches:
        raise SystemExit("no searches found for that game")
    our_white = len(searches[0]["moves"]) % 2 == 0
    rows, moves = analyse(searches, state, our_white)
    print(f"game {args.game}: {len(moves)} plies, status {state.get('status')}, winner {state.get('winner')}; "
          f"we are {'White' if our_white else 'Black'}; searches {len(searches)}; engine moves {len(rows)}")
    if args.json:
        with open(args.json, "w", encoding="utf-8", newline="\n") as f:
            json.dump({"game": args.game, "moves": moves, "state": {k: v for k, v in state.items() if k != "moves"},
                       "rows": rows}, f, indent=1)

    print("\n| move | src | pcs | played | score | d | pv len | prev pv: reply / our next | inherited (d) | settled at | changes | pv |")
    print("|---:|---|---:|---|---:|---:|---:|---|---|---:|---:|---|")
    for r in rows:
        prev = ""
        if "predicted_reply" in r:
            prev = f"{r['predicted_reply']}{'=' if r['reply_as_predicted'] else '≠' + r['reply']} / {r.get('pv_next_any') or '-'}"
        inh = f"{r.get('inherited_move')} ({r.get('inherited_depth')})" if r.get("inherited_move") else "-"
        print(f"| {r['move_no']} | {r['source']} | {r['pieces']} | {r['san']} | {r['score']} | {r['depth']} | {r['pv_len']} "
              f"| {prev} | {inh} | {r['settled_at']} | {r['first_move_changes']} | {r['pv_san'][:70]} |")

    with_prev = [r for r in rows if "predicted_reply" in r]
    print("\n| group | moves | our move = old PV's 3rd ply | old PV had a 3rd ply | our move = inherited move | "
          "plies agreeing (mean, where comparable) |")
    print("|---|---:|---:|---:|---:|---:|")
    for name, group in (("ponder hit", [r for r in with_prev if r["source"] == "hit"]),
                        ("ponder miss", [r for r in with_prev if r["source"] == "go"]),
                        ("all", with_prev)):
        if not group:
            continue
        has3 = [r for r in group if r.get("pv_next")]
        same3 = [r for r in has3 if r["pv_next"] == r["move"]]
        inh = [r for r in group if r.get("inherited_move")]
        same_inh = [r for r in inh if r["inherited_move"] == r["move"]]
        agree = [r["agree_plies"] for r in group if r.get("agree_plies") is not None]
        print(f"| {name} | {len(group)} | {len(same3)} of {len(has3)} | {len(has3)} | {len(same_inh)} of {len(inh)} | "
              f"{st.mean(agree) if agree else float('nan'):.2f} (n {len(agree)}) |")

    # How far the new line follows the old one, against how far it could (both lines are cut short by the table).
    comparable = [r for r in with_prev if r.get("agree_plies") is not None]
    print("\n| hit | old PV from our next move | new PV | plies that could agree | plies agreeing |")
    print("|---:|---:|---:|---:|---:|")
    for r in comparable:
        cap = min(r["pv_len"], r["prev_pv_len"] - 2)
        print(f"| {r['move_no']} | {r['prev_pv_len'] - 2} | {r['pv_len']} | {cap} | {r['agree_plies']} |")
    caps = [min(r["pv_len"], r["prev_pv_len"] - 2) for r in comparable]
    full = [r for r, c in zip(comparable, caps) if r["agree_plies"] == c]
    print(f"agree all the way they both go: {len(full)} of {len(comparable)}; plies that could agree: mean "
          f"{st.mean(caps):.2f}; agreeing {st.mean(r['agree_plies'] for r in comparable):.2f}")
    print(f"PV length: median {st.median(r['pv_len'] for r in rows)}; PVs of 1-3 plies: "
          f"{sum(1 for r in rows if r['pv_len'] <= 3)} of {len(rows)}")

    # The ponder.
    hits = [r for r in with_prev if r["source"] == "hit"]
    misses = [r for r in with_prev if r["source"] == "go"]
    print(f"\nponder: {len(hits)} hits, {len(misses)} misses, hit rate {len(hits) / max(1, len(with_prev)):.0%}")

    def med(values):
        values = [v for v in values if v is not None]
        return f"{st.median(values):.0f}" if values else "-"

    print("\n| | moves | pondered (ms, median) | depth at ponderhit / stop | depth played | after the hit (ms) / "
          "own search (ms) | ms to the old search's depth less 2 |")
    print("|---|---:|---:|---:|---:|---:|---:|")
    print(f"| hits | {len(hits)} | {med(r['pondered_ms'] for r in hits)} | {med(r['depth_at_hit'] for r in hits)} | "
          f"{med(r['depth_played'] for r in hits)} | {med(r['after_hit_ms'] for r in hits)} | "
          f"{med(r['inherited_ms'] for r in hits)} |")
    print(f"| misses | {len(misses)} | {med(r.get('miss_pondered_ms') for r in misses)} | "
          f"{med(r.get('miss_depth') for r in misses)} | {med(r['depth'] for r in misses)} | "
          f"{med(r['used_ms'] for r in misses)} | {med(r['inherited_ms'] for r in misses)} |")
    gained = [r["depth_played"] - r["depth_at_hit"] for r in hits if r["depth_played"] is not None]
    print("depths added after the hit: " + ", ".join(f"{g}: {gained.count(g)}" for g in sorted(set(gained))))
    print(f"hits whose lines after ponderhit went back below the depth at the hit: {sum(r['restarted'] for r in hits)}")
    print(f"first move changed at depth 6+ within the search (mean): hits "
          f"{st.mean(r['first_move_changes'] for r in hits):.2f}, misses {st.mean(r['first_move_changes'] for r in misses):.2f}; "
          f"settled at depth (median): hits {med(r['settled_at'] for r in hits)}, misses {med(r['settled_at'] for r in misses)}")

    # How far ahead did the expected line come true?
    followed = [r["game_followed"] for r in rows if r["ply"] + 2 < len(moves)]
    print("\n| plies of our PV the game then followed | moves |\n|---:|---:|")
    for k in sorted(set(followed)):
        print(f"| {k} | {followed.count(k)} |")
    print(f"the game followed the whole PV: {sum(r['game_followed_all'] for r in rows)} of {len(rows)} "
          f"(PVs of 1-3 plies among them: {sum(1 for r in rows if r['game_followed_all'] and r['pv_len'] <= 3)})")
    same = [r for r in misses if r.get("miss_would_play") == r["move"]]
    print(f"misses: the stopped ponder's move (for the reply we expected) is the move we then played: "
          f"{len(same)} of {len(misses)}")

    # The evaluation, move to move (our point of view): what moved it, the hits or the misses?  "In the balance": our
    # score before the move was -3.00 or better (after that the game was lost, and a lost ending's scores run to -20).
    print("\n| our move after | while | moves | eval change, sum (cp) | mean | moves it fell 30+ cp | rose 30+ cp |")
    print("|---|---|---:|---:|---:|---:|---:|")
    for phase, keep in (("in the balance", lambda r: r["prev_cp"] >= -300),
                        ("lost (-3.00 or worse), scores capped at -10.00", lambda r: r["prev_cp"] < -300)):
        for name, group in (("a hit (the reply we expected)", hits), ("a miss (another reply)", misses)):
            d = [clamp(r["cp"]) - clamp(r["prev_cp"]) for r in group if keep(r) and abs(r["cp"]) < 90000
                 and abs(r["prev_cp"]) < 90000]
            if d:
                print(f"| {name} | {phase} | {len(d)} | {sum(d)} | {st.mean(d):.1f} | {sum(1 for x in d if x <= -30)} | "
                      f"{sum(1 for x in d if x >= 30)} |")

    # Where the plan changed, and why.
    kinds = {}
    print("\n| move | kind | played | the plan's move | why | our score before -> after (cp) |")
    print("|---:|---|---|---|---|---|")
    for r in with_prev:
        kind, why = reason(r)
        kinds.setdefault(kind, []).append(r)
        if kind != "kept" and r["prev_cp"] >= -300:
            print(f"| {r['move_no']} | {r['source']} | {r['san']} | {r.get('plan_san') or '-'} | {why} | "
                  f"{r['prev_cp']} -> {r['cp']} |")
    print("\n| what happened to the plan | moves | in the balance (our score -3.00 or better) |\n|---|---:|---:|")
    for kind in ("kept", "deeper search", "deeper search, a tactic", "already the table's move",
                 "they deviated, we played our planned move anyway", "they deviated", "tables"):
        group = kinds.get(kind, [])
        print(f"| {kind} | {len(group)} | {sum(1 for r in group if r['prev_cp'] >= -300)} |")


def clamp(cp):
    return max(-1000, min(1000, cp))


def reason(r):
    """What happened to the plan at this move: its kind, and the why in words."""
    if r["pieces"] <= 5:
        return "tables", "the endgame tables"
    if r["source"] == "go":
        forcing = " (forcing)" if r["reply_forcing"] else ""
        if r.get("pv_next_any") == r["move"]:
            return ("they deviated, we played our planned move anyway",
                    f"they played {r['reply_san']}{forcing}, not {r['predicted_san']}; our planned move still played")
        return "they deviated", f"they played {r['reply_san']}{forcing}, not {r['predicted_san']}"
    plan = r.get("pv_next") or r.get("inherited_move")
    if r["move"] == plan and r.get("inherited_move") in (None, r["move"]):
        return "kept", ""
    if r["settled_at"] == 1:
        return "already the table's move", "the search began with it: the table held it, not the old line's move"
    swing = r.get("switch_swing")
    if swing is not None and abs(swing) >= 50 and abs(r["cp"]) < 90000:
        return "deeper search, a tactic", f"took over at depth {r['settled_at']}, the score moving {swing:+d} there"
    return "deeper search", f"took over at depth {r['settled_at']}" + (f", the score moving {swing:+d} there"
                                                                         if swing is not None else "")


if __name__ == "__main__":
    main()
