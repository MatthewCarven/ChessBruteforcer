"""Where the bot's lost and drawn games turned, and whose fault it was (TURNS.md has the results).

Matthew, 2026-10-21: "if all of a sudden you were not winning you'd know if someone made a mistake or you were
being out-calculated". For every game the bot lost or drew, this finds where our own score fell, then looks again
at the position before each fall: with our engine searching deeper, and with Stockfish.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/turning-points.py extract   (the logs -> games/turns/games.json;
                                       --since "YYYY-MM-DD HH:MM" for one build's games only)
    ... turning-points.py falls       (each game not won: its scores, falls and turn, as a list)
    ... turning-points.py search      (the falls re-searched -> searches.jsonl; stop it any time, it carries on;
                                       --reverse for a second worker from the other end; --cpus 14,15)
    ... turning-points.py stretches   (Stockfish at each game's stretch, its start and end -> stretches.jsonl)
    ... turning-points.py curves      (Stockfish on every move of ours up to the stretch's end -> curves.jsonl)
    ... turning-points.py calibrate   (Stockfish on every 5th move of the won games -> calibrate.jsonl)
    ... turning-points.py deep        (the misjudged games, our engine 4 plies past the bot's depth -> deep.jsonl;
                                       --extra, --cap 3600, --cpus, --threads, --hash 512, --reverse)
    ... turning-points.py table       (the tables, as markdown -> stdout)
Every command takes --dir <folder> (relative to the repo) for another run's data, e.g. a later week's: the first
run's is games/turns (TURNS.md).

Where the scores come from: lichess-bot's DEBUG log (bot/lichess-bot/lichess_bot_auto_logs, 7 days kept) has every
UCI line the engine sent. Our score for a move is the last `info` line of the search that chose it. For a ponder hit
that includes the lines from before `ponderhit`, which python-chess (and so the bot's PGNs) never kept: so this
has a score for every engine move, before P1 too. Results, ratings and time controls come from bot/games.

A fall: our score at one of our moves is at least FALL_CP (100) centipawns below our score at our move before, and
our expected score (Lichess's curve) at least 0.05 lower (so +900 to +750 isn't one). Scores are clamped to +-1000.
A game turned between the last of our moves where we were still OK and the first after it where that was gone
(`stretch_of`): at the biggest fall in there (`turn_of`), or it slid. Each fall is looked at again on the position
before it (P), where we played move M: our engine to the depth the bot reached there + 2, on P and on M (`search`),
and Stockfish 16 to depth 20 on P, on M alone, and on the position the fall was seen in. With s0 our score before
the fall and "half" half the fall below s0, its kind is (`classify`, in order):
  mistake      our deeper search prefers another move to M by MISTAKE_CP (50) or more, and Stockfish agrees M cost
               something (0.05 or more of expected score);
  false draw   M scored exactly 0 and it, or its logged line, comes back to a position from the game so far (the
               engine counts any repetition as a draw: `false_draw`);
  phantom      Stockfish sees no real fall: after the reply it is above half (our new score was the odd one out);
  outcalc      our deeper search already has M at or below half, and the opponent's reply wasn't the one we
               predicted (the ponder move): they found what our depth didn't;
  seen-late    the same, but they played the reply we predicted: our own line, seen deeper a move later;
  misjudged    our deeper search still doesn't, and Stockfish already has M at or below half (an evaluation error,
               as in TODO's knight ending: +2.4 for us, +0.25 for Stockfish);
  unexplained  neither saw it on P; Stockfish sees it only after the reply.
A slide is misjudged if Stockfish at its start is already at or below half the slide, else outplayed
(`classify_slide`). No stretch: a level draw (never ahead), or a flag.
"""
import json
import math
import os
import re
import sys
import time
from collections import defaultdict

import chess
import chess.pgn

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MAIN = os.path.normpath(os.path.join(ROOT, "..", "..", "..")) if os.sep + ".claude" + os.sep in ROOT else ROOT
LOGS = os.path.join(MAIN, "bot", "lichess-bot", "lichess_bot_auto_logs")
PGNS = os.path.join(MAIN, "bot", "games")
OUT = os.path.join(MAIN, "games", "turns")
if "--dir" in sys.argv:          # another run's data folder (relative to the repo), e.g. games/turns-2026-10-29
    OUT = os.path.join(MAIN, sys.argv[sys.argv.index("--dir") + 1])
ME = "matthewcarven"

FALL_CP = 100
FALL_E = 0.05
CLAMP = 1000
MISTAKE_CP = 50

LINE = re.compile(r"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d,\d{3}) chess\.engine .*?<UciProtocol \(pid=(\d+)\)>: (<<|>>) (.*)$")
OWNER = re.compile(r"The engine for game (\w+) has pid=(\d+)")
BOOK = re.compile(r"Got move (\w+) from book .* for game (\w+)")


def log_files():
    names = sorted(n for n in os.listdir(LOGS) if n.startswith("lichess-bot.log."))
    names.append("lichess-bot.log")
    return [os.path.join(LOGS, n) for n in names if os.path.exists(os.path.join(LOGS, n))]


def parse_info(text):
    words = text.split()
    if "score" not in words or "depth" not in words:
        return None
    i = words.index("score")
    kind, val = words[i + 1], int(words[i + 2])
    bound = words[i + 3] if i + 3 < len(words) and words[i + 3] in ("lowerbound", "upperbound") else None
    pv = words[words.index("pv") + 1:] if "pv" in words else []
    return {"depth": int(words[words.index("depth") + 1]), "kind": kind, "val": val, "bound": bound, "pv": pv}


def cp_of(kind, val):
    if kind == "mate":
        return CLAMP if val > 0 else -CLAMP
    return max(-CLAMP, min(CLAMP, val))


def expected(cp):
    return 1 / (1 + math.exp(-0.00368208 * cp))


# ---------------------------------------------------------------- extract

def read_logs():
    games = defaultdict(lambda: {"searches": [], "book": 0, "options": {}})
    owner, current, position, options = {}, {}, {}, defaultdict(dict)
    for path in log_files():
        with open(path, encoding="utf-8", errors="replace") as f:
            for raw in f:
                raw = raw.rstrip("\n")
                m = LINE.match(raw)
                if not m:
                    o = OWNER.search(raw)
                    if o:
                        owner[o.group(2)] = o.group(1)
                        games[o.group(1)]["options"] = dict(options[o.group(2)])
                        games[o.group(1)]["day"] = raw[:10]
                        continue
                    b = BOOK.search(raw)
                    if b:
                        games[b.group(2)]["book"] += 1
                    continue
                pid, way, text = m.group(2), m.group(3), m.group(4)
                if way == "<<":
                    if text.startswith("setoption name "):
                        name, _, value = text[len("setoption name "):].partition(" value ")
                        options[pid][name] = value
                        continue
                    game = owner.get(pid)
                    if game is None:
                        continue
                    s = current.get(pid)
                    if text.startswith("position"):
                        position[pid] = text
                    elif text.startswith("go"):
                        s = {"kind": "ponder" if text.startswith("go ponder") else "go", "position": position.get(pid),
                             "go": text, "infos": [], "hit": False, "stopped": False, "best": None, "ponder": None,
                             "t": m.group(1)}
                        games[game]["searches"].append(s)
                        current[pid] = s
                    elif text == "ponderhit" and s:
                        s["hit"] = True
                    elif text == "stop" and s:
                        s["stopped"] = True
                else:
                    s = current.get(pid)
                    if s is None:
                        continue
                    if text.startswith("info") and " score " in text:
                        i = parse_info(text)
                        if i:
                            s["infos"].append(i)
                    elif text.startswith("bestmove"):
                        w = text.split()
                        s["best"] = w[1]
                        s["ponder"] = w[3] if len(w) > 3 and w[2] == "ponder" else None
                        current[pid] = None
    return games


def board_of(position):
    words = position.split()
    if words[1] == "startpos":
        board = chess.Board()
        rest = words[2:]
    else:
        k = words.index("moves") if "moves" in words else len(words)
        board = chess.Board(" ".join(words[2:k]))
        rest = words[k:]
    moves = rest[1:] if rest and rest[0] == "moves" else []
    for mv in moves:
        board.push_uci(mv)
    return board, moves


def our_moves(searches):
    """Our engine moves, in order: each the search that chose a move the game played."""
    out, after_miss = [], False
    for s in searches:
        if not s["best"] or s["position"] is None:
            continue
        if s["kind"] == "ponder":
            if s["hit"]:
                out.append((s, "hit"))
                after_miss = False
            elif s["stopped"]:
                after_miss = True
            continue
        out.append((s, "after-miss" if after_miss else "go"))
        after_miss = False
    moves = []
    for s, src in out:
        board, played = board_of(s["position"])
        scored = [i for i in s["infos"] if i["bound"] is None] or s["infos"]
        last = scored[-1] if scored else None
        moves.append({"ply": len(played), "fen": board.fen(), "moves": played, "move": s["best"], "predicted": s["ponder"],
                      "src": src, "depth": last["depth"] if last else None,
                      "score": [last["kind"], last["val"]] if last else None,
                      "pv": last["pv"][:8] if last else [], "go": s["go"], "t": s["t"]})
    return moves


def read_pgns():
    out = {}
    for name in os.listdir(PGNS):
        if not name.endswith(".pgn"):
            continue
        with open(os.path.join(PGNS, name), encoding="utf-8", errors="replace") as f:
            h = chess.pgn.read_headers(f)
        if h is None or "GameId" not in h:
            continue
        out[h["GameId"]] = dict(h)
    return out


def extract():
    """--since "YYYY-MM-DD HH:MM" keeps only games whose engine started thinking then or later (a new build's)."""
    if os.path.exists(os.path.join(OUT, "games.json")) and "--force" not in sys.argv:
        sys.exit(f"{OUT} already has games.json, and the other files there are keyed to it: give another --dir, or "
                 f"--force to overwrite it")
    since = sys.argv[sys.argv.index("--since") + 1] if "--since" in sys.argv else ""
    logs = read_logs()
    pgns = read_pgns()
    games = []
    for gid, g in logs.items():
        h = pgns.get(gid)
        if h is None or not g["searches"] or g["searches"][0]["t"] < since:
            continue
        white = h.get("White", "").lower() == ME
        result = h.get("Result")
        ours = {"1-0": 1.0 if white else 0.0, "0-1": 0.0 if white else 1.0, "1/2-1/2": 0.5}.get(result)
        if ours is None:
            continue
        base, _, inc = h.get("TimeControl", "-").partition("+")
        games.append({"id": gid, "day": g.get("day"), "white": white, "opponent": h.get("Black" if white else "White"),
                      "opp_elo": int(h.get("BlackElo" if white else "WhiteElo", "0") or 0),
                      "our_elo": int(h.get("WhiteElo" if white else "BlackElo", "0") or 0),
                      "tc": h.get("TimeControl"), "base": int(base) if base.isdigit() else None,
                      "inc": int(inc) if inc.isdigit() else None, "event": h.get("Event"),
                      "termination": h.get("Termination"), "result": ours, "book": g["book"],
                      "eval": os.path.basename(g["options"].get("EvalFile", "")), "threads": g["options"].get("Threads"),
                      "options": g["options"], "moves": our_moves(g["searches"])})
    games.sort(key=lambda x: (x["day"] or "", x["moves"][0]["t"] if x["moves"] else ""))
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "games.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(games, f)
    n = defaultdict(int)
    for g in games:
        n[g["result"]] += 1
    print(f"games with logs and a result: {len(games)} (won {n[1.0]}, drawn {n[0.5]}, lost {n[0.0]}); "
          f"engine moves {sum(len(g['moves']) for g in games)}, "
          f"with a score {sum(1 for g in games for m in g['moves'] if m['score'])}")


# ---------------------------------------------------------------- falls

def load_games():
    with open(os.path.join(OUT, "games.json"), encoding="utf-8") as f:
        return json.load(f)


def false_draw(m):
    """Our move scored exactly 0, and its line (our move, then the logged pv) comes back to a position from the game
    so far: the engine scores any repetition as a draw (Search.IsRepetition), though coming back once to a position
    from before the search isn't one, and an opponent who is winning just plays on. 'move' if our move itself
    repeats; 'line' if a later move of the line does; False otherwise."""
    if m["score"] != ["cp", 0]:
        return False
    board = chess.Board()
    seen = {board._transposition_key()}
    for mv in m["moves"]:
        board.push_uci(mv)
        seen.add(board._transposition_key())
    line = [m["move"]] + (m["pv"][1:] if m["pv"] and m["pv"][0] == m["move"] else [])
    for i, mv in enumerate(line):
        try:
            board.push_uci(mv)
        except ValueError:
            break
        if board._transposition_key() in seen:
            return "move" if i == 0 else "line"
    return False


def find_falls(game):
    falls = []
    ms = [m for m in game["moves"] if m["score"]]
    for a, b in zip(ms, ms[1:]):
        sa, sb = cp_of(*a["score"]), cp_of(*b["score"])
        de = expected(sa) - expected(sb)
        if sa - sb >= FALL_CP and de >= FALL_E:
            reply = b["moves"][a["ply"] + 1] if b["ply"] == a["ply"] + 2 else None
            falls.append({"game": game["id"], "ply": a["ply"], "fen": a["fen"], "move": a["move"],
                          "predicted": a["predicted"], "reply": reply, "next_fen": b["fen"],
                          "hit": b["src"] == "hit", "before": sa, "after": sb, "drop_e": de,
                          "depth": a["depth"], "next_depth": b["depth"], "pv": a["pv"],
                          "pieces": chess.Board(a["fen"]).occupied.bit_count(), "false_draw": false_draw(a)})
    return falls


def not_won(games):
    return [g for g in games if g["result"] < 1.0 and g["moves"]]


def falls_cmd():
    games = load_games()
    total = 0
    for g in not_won(games):
        f = find_falls(g)
        total += len(f)
        turn, st = turn_of(g), stretch_of(g)
        sc = [cp_of(*m["score"]) for m in g["moves"] if m["score"]]
        print(f"{g['id']} {g['tc']:>6} {g['opp_elo']:>4} {g['result']:.1f} {g['termination'][:8]:8} moves {len(g['moves']):3} "
              f"first {sc[0] if sc else 0:+5} max {max(sc) if sc else 0:+5} last {sc[-1] if sc else 0:+5} falls {len(f)}"
              + (f"  turn: ply {turn['ply']} {turn['before']:+}->{turn['after']:+} "
                 f"{'predicted' if turn['reply'] == turn['predicted'] else 'unpredicted'} reply" if turn else
                 f"  slide: ply {st['ply']}-{st['end_ply']} {st['before']:+}->{st['after']:+}" if st else ""))
    print(f"falls: {total}")


# ---------------------------------------------------------------- search

BOT_ENGINE = os.path.join(MAIN, "bot", "lichess-bot", "engines", "bruteforcer")
EVALS = os.path.join(MAIN, "bot", "eval")
TABLES = os.path.join(MAIN, "tables")
STOCKFISH = ["wsl.exe", "-d", "Ubuntu-24.04", "--", "nice", "-n", "19", "/usr/games/stockfish"]
BELOW_NORMAL = 0x00004000


def option(name, default, kind=str):
    args = sys.argv
    return kind(args[args.index(name) + 1]) if name in args else default


def pin(pid, cpus):
    import ctypes
    k32 = ctypes.windll.kernel32
    h = k32.OpenProcess(0x0200 | 0x0400, False, pid)
    k32.SetProcessAffinityMask(h, ctypes.c_size_t(sum(1 << c for c in cpus)))
    k32.CloseHandle(h)


def engine_copy():
    """Our engine, run from a copy (games/turns/engine) so the bot's own files are never locked."""
    import shutil
    here = os.path.join(OUT, "engine")
    exe = os.path.join(here, "ChessBruteforcer.Engine.exe")
    if not os.path.exists(exe):
        shutil.copytree(BOT_ENGINE, here)
    return exe


def pov_cp(score):
    return cp_of("cp", score.score(mate_score=100000))


def tasks(games):
    """Every fall in a game we didn't win: each game's turn (`turn_of`) first, then the other falls from -200 or
    better, then the falls of games already lost."""
    first, second, third = [], [], []
    for g in not_won(games):
        turn = turn_of(g)
        for f in find_falls(g):
            f["eval"] = g["eval"]
            is_turn = turn is not None and f["ply"] == turn["ply"]
            (first if is_turn else second if f["before"] >= -200 else third).append(f)
    return first + second + third


def search():
    import chess.engine
    games = load_games()
    path = os.path.join(OUT, "searches.jsonl")
    done = set()
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            done = {json.loads(line)["key"] for line in f if line.strip()}
    todo = [t for t in tasks(games) if f"{t['game']}:{t['ply']}" not in done][:option("--limit", 10**9, int)]
    if "--reverse" in sys.argv:          # a second worker: from the other end (each skips what the other has done)
        todo.reverse()
    cpus = [int(c) for c in option("--cpus", "14,15").split(",")]
    extra, cap, sf_depth = option("--extra", 2, int), option("--cap", 90.0, float), option("--sf-depth", 20, int)
    print(f"{len(todo)} falls to search ({len(done)} done); our engine on CPUs {cpus}, depth +{extra} (cap {cap:.0f} s); "
          f"Stockfish depth {sf_depth}", flush=True)
    ours = chess.engine.SimpleEngine.popen_uci([engine_copy()], creationflags=BELOW_NORMAL)
    pin(ours.transport.get_pid(), cpus)
    ours.configure({"Threads": option("--threads", len(cpus), int), "Hash": 256, "EndgameTables": TABLES,
                    "TableCache": 256})
    sf = chess.engine.SimpleEngine.popen_uci(STOCKFISH, creationflags=BELOW_NORMAL)
    sf.configure({"Threads": 1, "Hash": 128})
    current_eval = None
    try:
        for n, t in enumerate(todo, 1):
            if f"{t['game']}:{t['ply']}" in read_jsonl("searches.jsonl"):
                continue
            if t["eval"] != current_eval:
                ours.configure({"EvalFile": os.path.join(EVALS, t["eval"]) if t["eval"] else "<empty>"})
                current_eval = t["eval"]
            board = chess.Board(t["fen"])
            move = chess.Move.from_uci(t["move"])
            depth = (t["depth"] or 10) + extra
            t0 = time.time()
            key = f"{t['game']}:{t['ply']}"   # a new game for python-chess: ucinewgame, so a cold table
            deep = ours.analyse(board, chess.engine.Limit(depth=depth, time=cap), game=key)
            best = deep["pv"][0] if deep.get("pv") else None
            rec = {"key": key, "deep_depth": deep.get("depth"), "deep_best": best.uci() if best else None,
                   "deep_score": pov_cp(deep["score"].relative)}
            if best == move:
                rec["deep_move_score"] = rec["deep_score"]
            else:
                board.push(move)
                after = ours.analyse(board, chess.engine.Limit(depth=depth - 1, time=cap), game=key)
                board.pop()
                rec["deep_move_score"] = -pov_cp(after["score"].relative)
            rec["deep_s"] = round(time.time() - t0, 1)
            t1 = time.time()
            sf_best = sf.analyse(board, chess.engine.Limit(depth=sf_depth), game=key)
            rec["sf_best"] = sf_best["pv"][0].uci() if sf_best.get("pv") else None
            rec["sf_score"] = pov_cp(sf_best["score"].relative)
            if sf_best.get("pv") and sf_best["pv"][0] == move:
                rec["sf_move_score"] = rec["sf_score"]
            else:
                rec["sf_move_score"] = pov_cp(sf.analyse(board, chess.engine.Limit(depth=sf_depth), root_moves=[move], game=key)["score"].relative)
            rec["sf_next_score"] = pov_cp(sf.analyse(chess.Board(t["next_fen"]), chess.engine.Limit(depth=sf_depth), game=key)["score"].relative)
            rec["sf_s"] = round(time.time() - t1, 1)
            with open(path, "a", encoding="utf-8", newline="\n") as f:
                f.write(json.dumps(rec) + "\n")
            print(f"{n}/{len(todo)} {rec['key']} {t['before']:+}->{t['after']:+} ours d{rec['deep_depth']} "
                  f"{rec['deep_best']} {rec['deep_score']:+} (played {rec['deep_move_score']:+}) {rec['deep_s']}s | sf "
                  f"{rec['sf_best']} {rec['sf_score']:+} (played {rec['sf_move_score']:+}) next {rec['sf_next_score']:+} "
                  f"{rec['sf_s']}s", flush=True)
    finally:
        ours.quit()
        sf.quit()


# ---------------------------------------------------------------- stretches

OK_LOSS, LOST = -100, -300      # a loss: still OK at -100 or better; gone at -300 or worse
OK_DRAW, DRAWN = 100, 20        # a draw: ahead at +100 or better; gone at +20 or worse


def stretch_of(game):
    """Where a lost or drawn game turned: from A, the last of our moves where we were still OK, to B, the first one
    after it where that was gone. (A loss never OK after the book starts at our first move.) None for a draw never
    ahead, or a game still OK at its last move."""
    ms = [m for m in game["moves"] if m["score"]]
    sc = [cp_of(*m["score"]) for m in ms]
    ok, gone = (OK_LOSS, LOST) if game["result"] == 0.0 else (OK_DRAW, DRAWN)
    good = [i for i, x in enumerate(sc) if x >= ok]
    if not good and game["result"] == 0.5:
        return None
    a = good[-1] if good else 0
    b = next((j for j in range(a + 1, len(sc)) if sc[j] <= gone), len(sc) - 1)
    if b <= a:
        return None
    return {"game": game["id"], "ply": ms[a]["ply"], "fen": ms[a]["fen"], "end_ply": ms[b]["ply"], "end_fen": ms[b]["fen"],
            "before": sc[a], "after": sc[b], "from_book": not good, "moves": b - a, "false_draw": false_draw(ms[a])}


def turn_of(game):
    """The biggest fall inside the game's stretch, if there is one: a sudden turn. Otherwise it slid."""
    st = stretch_of(game)
    if st is None:
        return None
    falls = [f for f in find_falls(game) if st["ply"] <= f["ply"] < st["end_ply"]]
    return max(falls, key=lambda f: f["drop_e"]) if falls else None


def stretch_key(st):
    return f"{st['game']}:{st['ply']}-{st['end_ply']}"


def stretches_cmd():
    """Stockfish at each lost or drawn game's stretch, its start and its end (to tell slides apart)."""
    import chess.engine
    games = load_games()
    done = set(read_jsonl("stretches.jsonl"))
    todo = [st for st in (stretch_of(g) for g in not_won(games)) if st and stretch_key(st) not in done]
    sf_depth = option("--sf-depth", 20, int)
    print(f"{len(todo)} stretches to look at ({len(done)} done); Stockfish depth {sf_depth}", flush=True)
    sf = chess.engine.SimpleEngine.popen_uci(STOCKFISH, creationflags=BELOW_NORMAL)
    sf.configure({"Threads": 1, "Hash": 128})
    try:
        for n, st in enumerate(todo, 1):
            key = stretch_key(st)
            rec = {"key": key}
            rec["sf_start"] = pov_cp(sf.analyse(chess.Board(st["fen"]), chess.engine.Limit(depth=sf_depth), game=key)["score"].relative)
            rec["sf_end"] = pov_cp(sf.analyse(chess.Board(st["end_fen"]), chess.engine.Limit(depth=sf_depth), game=key)["score"].relative)
            with open(os.path.join(OUT, "stretches.jsonl"), "a", encoding="utf-8", newline="\n") as f:
                f.write(json.dumps(rec) + "\n")
            print(f"{n}/{len(todo)} {key} {st['before']:+}->{st['after']:+} over {st['moves']} moves; sf {rec['sf_start']:+} -> "
                  f"{rec['sf_end']:+}", flush=True)
    finally:
        sf.quit()


def curve_positions(game):
    """Our moves from the first out of the book to the end of the game's stretch: where Stockfish says it turned."""
    st = stretch_of(game)
    if st is None:
        return []
    ms = [m for m in game["moves"] if m["score"]]
    b = next(i for i, m in enumerate(ms) if m["ply"] == st["end_ply"])
    return ms[:b + 1]


def curves_cmd():
    """Stockfish on each of those positions (games/turns/curves.jsonl, one line a position)."""
    import chess.engine
    games = load_games()
    done = set(read_jsonl("curves.jsonl"))
    todo = [(g["id"], m) for g in not_won(games) for m in curve_positions(g) if f"{g['id']}:{m['ply']}" not in done]
    sf_depth = option("--sf-depth", 18, int)
    print(f"{len(todo)} positions ({len(done)} done); Stockfish depth {sf_depth}", flush=True)
    sf = chess.engine.SimpleEngine.popen_uci(STOCKFISH, creationflags=BELOW_NORMAL)
    sf.configure({"Threads": 1, "Hash": 128})
    try:
        for n, (gid, m) in enumerate(todo, 1):
            key = f"{gid}:{m['ply']}"
            info = sf.analyse(chess.Board(m["fen"]), chess.engine.Limit(depth=sf_depth), game=gid)
            rec = {"key": key, "sf": pov_cp(info["score"].relative), "sf_best": info["pv"][0].uci() if info.get("pv") else None}
            with open(os.path.join(OUT, "curves.jsonl"), "a", encoding="utf-8", newline="\n") as f:
                f.write(json.dumps(rec) + "\n")
            if n % 50 == 0:
                print(f"{n}/{len(todo)}", flush=True)
    finally:
        sf.quit()


def calibrate_cmd():
    """Are Stockfish's centipawns ours? Every 5th of our moves in games we won (games/turns/calibrate.jsonl), Stockfish
    at the curve's depth, against our own score there."""
    import chess.engine
    games = load_games()
    done = set(read_jsonl("calibrate.jsonl"))
    todo = [(g["id"], m) for g in games if g["result"] == 1.0 for m in g["moves"][::5]
            if m["score"] and f"{g['id']}:{m['ply']}" not in done]
    sf_depth = option("--sf-depth", 16, int)
    print(f"{len(todo)} positions ({len(done)} done); Stockfish depth {sf_depth}", flush=True)
    sf = chess.engine.SimpleEngine.popen_uci(STOCKFISH, creationflags=BELOW_NORMAL)
    sf.configure({"Threads": 1, "Hash": 128})
    try:
        for n, (gid, m) in enumerate(todo, 1):
            info = sf.analyse(chess.Board(m["fen"]), chess.engine.Limit(depth=sf_depth), game=gid)
            with open(os.path.join(OUT, "calibrate.jsonl"), "a", encoding="utf-8", newline="\n") as f:
                f.write(json.dumps({"key": f"{gid}:{m['ply']}", "sf": pov_cp(info["score"].relative),
                                    "ours": cp_of(*m["score"])}) + "\n")
            if n % 100 == 0:
                print(f"{n}/{len(todo)}", flush=True)
    finally:
        sf.quit()


# ---------------------------------------------------------------- deeper

def misjudged_positions(games, searches, slides):
    """The misjudged games (C): a sudden turn's position before the fall, or a slide's first position, with our move
    there, the bot's depth there, and the line half the fall (or slide) below our score."""
    out = []
    for g in not_won(games):
        kind, at = game_kind(g, searches, slides)
        if kind not in ("misjudged", "slide: misjudged"):
            continue
        m = next(m for m in g["moves"] if m["ply"] == at["ply"])
        out.append({"game": g["id"], "ply": at["ply"], "fen": m["fen"], "move": m["move"], "depth": m["depth"],
                    "eval": g["eval"], "kind": kind, "line": at["before"] - (at["before"] - at["after"]) / 2,
                    "lost": 1 - g["result"]})
    return out


def deep_cmd():
    """Is "misjudged" really beyond depth? Our engine on each misjudged game's position, --extra (4) plies past the
    bot's depth (games/turns/deep.jsonl), and on our move there: does it now see half the fall, or prefer another
    move? Stop it any time; --reverse for a second worker."""
    import chess.engine
    games = load_games()
    path = os.path.join(OUT, "deep.jsonl")
    done = set(read_jsonl("deep.jsonl"))
    todo = [p for p in misjudged_positions(games, read_jsonl("searches.jsonl"), read_jsonl("stretches.jsonl"))
            if f"{p['game']}:{p['ply']}" not in done][:option("--limit", 10**9, int)]
    if "--reverse" in sys.argv:
        todo.reverse()
    cpus = [int(c) for c in option("--cpus", "14,15").split(",")]
    extra, cap = option("--extra", 4, int), option("--cap", 3600.0, float)   # +4 is about an hour of one core each
    print(f"{len(todo)} misjudged positions to search ({len(done)} done); our engine on CPUs {cpus}, depth +{extra} "
          f"(cap {cap:.0f} s)", flush=True)
    ours = chess.engine.SimpleEngine.popen_uci([engine_copy()], creationflags=BELOW_NORMAL)
    pin(ours.transport.get_pid(), cpus)
    ours.configure({"Threads": option("--threads", len(cpus), int), "Hash": option("--hash", 512, int),
                    "EndgameTables": TABLES, "TableCache": 256})
    current_eval = None
    try:
        for n, t in enumerate(todo, 1):
            key = f"{t['game']}:{t['ply']}"
            if key in read_jsonl("deep.jsonl"):
                continue
            if t["eval"] != current_eval:
                ours.configure({"EvalFile": os.path.join(EVALS, t["eval"]) if t["eval"] else "<empty>"})
                current_eval = t["eval"]
            board = chess.Board(t["fen"])
            move = chess.Move.from_uci(t["move"])
            depth = (t["depth"] or 10) + extra
            t0 = time.time()
            deep = ours.analyse(board, chess.engine.Limit(depth=depth, time=cap), game=key)
            best = deep["pv"][0] if deep.get("pv") else None
            rec = {"key": key, "extra": extra, "depth": deep.get("depth"), "best": best.uci() if best else None,
                   "score": pov_cp(deep["score"].relative)}
            if best == move:
                rec["move_score"] = rec["score"]
            else:
                board.push(move)
                after = ours.analyse(board, chess.engine.Limit(depth=depth - 1, time=cap), game=key)
                board.pop()
                rec["move_score"] = -pov_cp(after["score"].relative)
            rec["s"] = round(time.time() - t0, 1)
            with open(path, "a", encoding="utf-8", newline="\n") as f:
                f.write(json.dumps(rec) + "\n")
            print(f"{n}/{len(todo)} {key} {t['kind']} bot d{t['depth']} -> d{rec['depth']} best {rec['best']} "
                  f"{rec['score']:+} (played {rec['move_score']:+}; half the fall at {t['line']:+.0f}) {rec['s']}s",
                  flush=True)
    finally:
        ours.quit()


def deep_verdict(p, r):
    if r["move_score"] <= p["line"]:
        return "now sees half the fall or more"
    if r["score"] - r["move_score"] >= MISTAKE_CP:
        return "still doesn't, but prefers another move"
    return "still doesn't see it"


def deep_table(games, searches, slides):
    deep = read_jsonl("deep.jsonl")
    if not deep:
        return
    rows = defaultdict(lambda: defaultdict(lambda: [0, 0.0]))
    reached = defaultdict(int)
    for p in misjudged_positions(games, searches, slides):
        r = deep.get(f"{p['game']}:{p['ply']}")
        verdict = deep_verdict(p, r) if r else "not searched yet"
        for k in (p["kind"], "all"):
            rows[k][verdict][0] += 1
            rows[k][verdict][1] += p["lost"]
        if r:
            reached[f"+{(r['depth'] or 0) - (p['depth'] or 0)}"] += 1
    extra = next(iter(deep.values()))["extra"]
    verdicts = ["now sees half the fall or more", "still doesn't, but prefers another move", "still doesn't see it",
                "not searched yet"]
    print(f"\n| misjudged (C), our engine {extra} plies past the bot's depth (games / points lost) | "
          + " | ".join(verdicts) + " |\n|---|" + "---:|" * len(verdicts))
    for k in ("misjudged", "slide: misjudged", "all"):
        if k in rows:
            print(f"| {k} | " + " | ".join(f"{rows[k][v][0]} / {rows[k][v][1]:g}" if rows[k][v][0] else "" for v in verdicts)
                  + " |")
    print(f"\nplies reached past the bot's depth (the cap can stop it short): {dict(sorted(reached.items()))}")


# ---------------------------------------------------------------- table

def read_jsonl(name):
    path = os.path.join(OUT, name)
    out = {}
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            for line in f:
                if line.strip():
                    r = json.loads(line)
                    out[r["key"]] = r
    return out


def classify(f, r):
    if f["false_draw"]:
        return "false draw"
    s0 = f["before"]
    line = s0 - (f["before"] - f["after"]) / 2       # half the fall below s0
    cost = expected(r["sf_score"]) - expected(r["sf_move_score"])
    if r["deep_score"] - r["deep_move_score"] >= MISTAKE_CP and cost >= 0.05:
        return "mistake"
    if r["sf_next_score"] > line:
        return "phantom"
    if r["deep_move_score"] <= line:
        return "outcalc" if f["reply"] != f["predicted"] else "seen-late"
    if r["sf_move_score"] <= line:
        return "misjudged"
    return "unexplained"


def classify_slide(st, r):
    if st["false_draw"]:
        return "false draw"
    if st["from_book"]:
        return "worse out of the book"
    line = st["before"] - (st["before"] - st["after"]) / 2
    return "slide: misjudged" if r["sf_start"] <= line else "slide: outplayed"


def tc_band(g):
    base, inc = g["base"] or 0, g["inc"] or 0
    t = base + 40 * inc                                   # Lichess's estimate of a game's length
    return "bullet (<3 min)" if t < 180 else "blitz 3 min" if base < 300 else "blitz 5 min" if t < 480 else "rapid+"


def elo_band(g):
    e = g["opp_elo"]
    return "<2000" if e < 2000 else "2000-2199" if e < 2200 else "2200-2399" if e < 2400 else "2400+"


KINDS = ["mistake", "outcalc", "seen-late", "misjudged", "unexplained", "phantom", "false draw", "slide: misjudged",
         "slide: outplayed", "worse out of the book", "flag", "level", "not searched yet"]


def game_kind(g, searches, slides):
    sc = [cp_of(*m["score"]) for m in g["moves"] if m["score"]]
    if g["termination"] in ("Time forfeit", "Abandoned") and (not sc or sc[-1] > -200):
        return "flag", None
    t = turn_of(g)
    if t:
        r = searches.get(f"{t['game']}:{t['ply']}")
        return (classify(t, r) if r else "not searched yet"), t
    st = stretch_of(g)
    if st:
        r = slides.get(stretch_key(st))
        return (classify_slide(st, r) if r else "not searched yet"), st
    return "level", None


GROUPS = {"mistake": "A. our mistake", "outcalc": "B. out-calculated", "seen-late": "B. out-calculated",
          "misjudged": "C. misjudged", "slide: misjudged": "C. misjudged", "false draw": "D. false draw",
          "slide: outplayed": "E. outplayed slowly", "level": "F. never ahead (a level draw)"}
GROUP_ORDER = ["A. our mistake", "B. out-calculated", "C. misjudged", "D. false draw", "E. outplayed slowly",
               "F. never ahead (a level draw)", "G. other"]


def group(kind):
    return GROUPS.get(kind, "G. other")


def pieces_band(at):
    if at is None:
        return "(no turn)"
    n = chess.Board(at["fen"]).occupied.bit_count()
    return "a: 6-7 (an ending)" if n <= 7 else "b: 8-12" if n <= 12 else "c: 13-20" if n <= 20 else "d: 21-32"


def link(g, ply):
    return f"[{g['id']}](https://lichess.org/{g['id']}#{ply})"


def table():
    games = load_games()
    searches, slides = read_jsonl("searches.jsonl"), read_jsonl("stretches.jsonl")
    nw = not_won(games)
    lost_total = sum(1 - g["result"] for g in nw)
    rows = defaultdict(lambda: {"games": 0, "lost": 0.0, "examples": []})
    by = defaultdict(lambda: defaultdict(float))
    for g in nw:
        kind, at = game_kind(g, searches, slides)
        row = rows[kind]
        row["games"] += 1
        row["lost"] += 1 - g["result"]
        if at and len(row["examples"]) < 3:
            row["examples"].append(f"{link(g, at['ply'])} move {at['ply'] // 2 + 1}, {at['before']:+} to {at['after']:+}")
        for dim, value in (("tc", tc_band(g)), ("elo", elo_band(g)), ("eval", g["eval"] or "built in"),
                           ("pieces", pieces_band(at))):
            by[(dim, value)][group(kind)] += 1 - g["result"]
    won = sum(1 for g in games if g["result"] == 1.0)
    print(f"games: {len(games)} (won {won}, drawn {sum(1 for g in nw if g['result'] == 0.5)}, lost "
          f"{sum(1 for g in nw if g['result'] == 0.0)}); points lost {lost_total:g}\n")
    print("| group | where the game turned | games | points lost | share | examples |\n|---|---|---:|---:|---:|---|")
    for k in sorted((k for k in rows), key=lambda k: (GROUP_ORDER.index(group(k)), KINDS.index(k) if k in KINDS else 99)):
        r = rows[k]
        print(f"| {group(k)[:2]} | {k} | {r['games']} | {r['lost']:g} | {r['lost'] / lost_total:.0%} | {'; '.join(r['examples'])} |")
    totals = defaultdict(float)
    for k, r in rows.items():
        totals[group(k)] += r["lost"]
    print("\n| group | points lost | share |\n|---|---:|---:|")
    for gname in GROUP_ORDER:
        if totals[gname]:
            print(f"| {gname} | {totals[gname]:g} | {totals[gname] / lost_total:.0%} |")
    shown = [x for x in GROUP_ORDER if totals[x]]
    for dim, title in (("tc", "time control"), ("elo", "opponent's rating"), ("eval", "evaluation"),
                       ("pieces", "pieces on the board where it turned")):
        keys = sorted(k for k in by if k[0] == dim)
        print(f"\n| points lost, by {title} | " + " | ".join(x[:2] for x in shown) + " | all |\n|---|"
              + "---:|" * (len(shown) + 1))
        for key in keys:
            row = by[key]
            print(f"| {key[1]} | " + " | ".join(f"{row[x]:g}" if row[x] else "" for x in shown) + f" | {sum(row.values()):g} |")
    # every fall, not only the turns
    count = defaultdict(lambda: [0, 0])
    depth_gap = defaultdict(int)
    for g in nw:
        for f in find_falls(g):
            r = searches.get(f"{f['game']}:{f['ply']}")
            k = classify(f, r) if r else "not searched yet"
            count[k][0 if f["before"] >= -200 else 1] += 1
            if r:
                gap = (r["deep_depth"] or 0) - (f["depth"] or 0)
                depth_gap["+2" if gap >= 2 else "+1" if gap == 1 else "same or less"] += 1
    print("\n| every fall | from -200 or better | from below -200 (already lost) |\n|---|---:|---:|")
    for k in KINDS:
        if k in count:
            print(f"| {k} | {count[k][0]} | {count[k][1]} |")
    print(f"\nre-search depth against the bot's: {dict(depth_gap)}")
    lag_table(nw, searches, slides)
    deep_table(games, searches, slides)


LAGS = ["agreed", "1-2 moves", "3-5 moves", "6-10 moves", "11-20 moves", "more than 20", "never, out of book"]


def sf_stretch(g, curves):
    """Stockfish's own stretch, by the same rule as ours (`stretch_of`): the last of our moves (up to the end of our
    stretch) where it had us OK, and the first after that where it had it gone. None without the curve; (None, ms,
    sf) if Stockfish never had us OK after the book."""
    st = stretch_of(g)
    ms = curve_positions(g)
    if st is None or not ms or any(f"{g['id']}:{m['ply']}" not in curves for m in ms):
        return None
    sf = [curves[f"{g['id']}:{m['ply']}"]["sf"] for m in ms]
    ok, gone = (OK_LOSS, LOST) if g["result"] == 0.0 else (OK_DRAW, DRAWN)
    a = next(i for i, m in enumerate(ms) if m["ply"] == st["ply"])
    fine = [i for i in range(a + 1) if sf[i] >= ok]
    if not fine:
        return None, ms, sf
    sa = fine[-1]
    sb = next((j for j in range(sa + 1, len(sf)) if sf[j] <= gone), len(sf) - 1)
    return (sa, sb), ms, sf


def sf_lag(g, curves):
    """When our engine last thought we were OK (the stretch's start), did Stockfish agree? If not, how many of our
    moves before that it last did."""
    got = sf_stretch(g, curves)
    if got is None:
        return None
    span, ms, sf = got
    if span is None:
        return "never, out of book"
    a = next(i for i, m in enumerate(ms) if m["ply"] == stretch_of(g)["ply"])
    n = a - span[0]
    return "agreed" if n == 0 else "1-2 moves" if n <= 2 else "3-5 moves" if n <= 5 else "6-10 moves" if n <= 10 \
        else "11-20 moves" if n <= 20 else "more than 20"


SF_TURN_E = 0.1


def sf_turn(g, curves):
    """Where Stockfish says the game turned: in its own stretch, did one of our moves (with the reply) cost SF_TURN_E
    or more of expected score? If so, did our own score fall at that move too (100+: at once; 40+: partly; else not)?
    If not, it went gradually."""
    got = sf_stretch(g, curves)
    if got is None:
        return None
    span, ms, sf = got
    if span is None:
        return "never OK after the book"
    sa, sb = span
    drops = [(expected(sf[i]) - expected(sf[i + 1]), i) for i in range(sa, max(sa + 1, sb))]
    d, i = max(drops)
    if d < SF_TURN_E:
        return "gradual"
    ours = cp_of(*ms[i]["score"]) - cp_of(*ms[i + 1]["score"])
    return "one move: we saw it at once" if ours >= FALL_CP else "one move: we saw part" if ours >= 40 else \
        "one move: we didn't see it"


SF_TURNS = ["one move: we saw it at once", "one move: we saw part", "one move: we didn't see it", "gradual",
            "never OK after the book"]


def lag_table(nw, searches, slides):
    curves = read_jsonl("curves.jsonl")
    lags = defaultdict(lambda: defaultdict(lambda: [0, 0.0]))
    for g in nw:
        lag = sf_lag(g, curves)
        if lag is None:
            continue
        kind, _ = game_kind(g, searches, slides)
        for k in (group(kind), "all"):
            lags[k][lag][0] += 1
            lags[k][lag][1] += 1 - g["result"]
    print("\n| when we last thought we were OK, Stockfish had last thought so (games / points lost) | " + " | ".join(LAGS)
          + " |\n|---|" + "---:|" * len(LAGS))
    for k in GROUP_ORDER + ["all"]:
        if k in lags:
            print(f"| {k} | " + " | ".join(f"{lags[k][x][0]} / {lags[k][x][1]:g}" if lags[k][x][0] else "" for x in LAGS)
                  + " |")
    turns = defaultdict(lambda: defaultdict(lambda: [0, 0.0]))
    for g in nw:
        t = sf_turn(g, curves)
        if t is None:
            continue
        kind, _ = game_kind(g, searches, slides)
        for k in (group(kind), "all"):
            turns[k][t][0] += 1
            turns[k][t][1] += 1 - g["result"]
    print("\n| where Stockfish says it turned (games / points lost) | " + " | ".join(SF_TURNS)
          + " |\n|---|" + "---:|" * len(SF_TURNS))
    for k in GROUP_ORDER + ["all"]:
        if k in turns:
            print(f"| {k} | " + " | ".join(f"{turns[k][x][0]} / {turns[k][x][1]:g}" if turns[k][x][0] else ""
                                          for x in SF_TURNS) + " |")


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "table"
    {"extract": extract, "falls": falls_cmd, "search": search, "stretches": stretches_cmd, "curves": curves_cmd, "calibrate": calibrate_cmd, "deep": deep_cmd,
     "table": table}[cmd]()
