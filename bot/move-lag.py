"""Where the bot's clock goes on each move: lichess.org's charge against the engine's own time, from lichess-bot's DEBUG log.

    python bot\\move-lag.py [log ...]  [--csv out.csv]

Reads bot\\lichess-bot\\lichess_bot_auto_logs\\lichess-bot.log* by default (read only; 30 days kept from 2026-10-25,
DEBUG from 2026-10-15). For each of the bot's moves it pairs:
  - the game state that gave us the move (logged as it arrived; its clocks are lichess.org's, in ms),
  - the engine's "bestmove" (the move chosen), and the POST that sent it (logged when lichess.org answered),
  - the game state that echoes our move back, with our clock after it.
charged = our clock before - our clock after + the increment: what lichess.org took for the move.
engine  = state arrived -> bestmove (lichess-bot's own handling, the search, and the stop of a missed ponder).
post    = bestmove -> POST answered (one HTTP round trip to lichess.org).
hidden  = charged - engine - post: time lichess.org counts that we never see, mostly the opponent's move on its way
          to us (the clock starts when lichess.org has it), less any lag compensation lichess.org gives.
A move is used only when both states are found (a missed ponder's discarded bestmove has no echo).
Games are told apart by their move lists (each state carries the whole list), so several at once are fine.
"""
import csv
import datetime
import glob
import os
import re
import statistics
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
LOGS = os.path.join(HERE, "lichess-bot", "lichess_bot_auto_logs", "lichess-bot.log*")
LINE = re.compile(r"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d,\d{3}) \S+ \(\S+\) \w+ (.*)$")
STATE = re.compile(r"'moves': '([^']*)', 'wtime': (\d+), 'btime': (\d+), 'winc': (\d+), 'binc': (\d+)"
                   r"(?:, 'status': '(\w+)')?")
INITIAL = re.compile(r"Initial state: \{'id': '(\w+)'.*?'clock': \{'initial': (\d+), 'increment': (\d+)\}")
ENGINE = re.compile(r"<UciProtocol \(pid=(\d+)\)>: (<<|>>) (.*)$")
POST = re.compile(r'"POST /api/bot/game/(\w+)/move/(\w+)\?\S* HTTP/1\.1" (\d+)')


def when(stamp):
    return datetime.datetime.strptime(stamp, "%Y-%m-%d %H:%M:%S,%f")


def parse(paths):
    """States by move list, each engine's searches, POSTs, and each game's clock."""
    states = {}                 # moves -> [(time, wtime, btime, winc, binc, status)]
    searches = []               # (pid, position moves, bestmove time, bestmove)
    posts = {}                  # (game, uci) -> [time answered]
    games = {}                  # game -> (initial ms, increment ms)
    position = {}               # pid -> moves of its last "position"
    for path in paths:
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                m = LINE.match(line)
                if not m:
                    continue
                t, msg = m[1], m[2]
                if msg.startswith(("Game state: ", "Initial state: ")):
                    s = STATE.search(msg)
                    if s:
                        states.setdefault(s[1], []).append((when(t), int(s[2]), int(s[3]), int(s[4]), int(s[5]), s[6]))
                    i = INITIAL.search(msg)
                    if i:
                        games[i[1]] = (int(i[2]), int(i[3]))
                elif msg.startswith("<UciProtocol"):
                    e = ENGINE.match(msg)
                    if not e:
                        continue
                    pid, way, cmd = e[1], e[2], e[3]
                    if way == "<<" and cmd.startswith("position"):
                        position[pid] = cmd.split(" moves ", 1)[1] if " moves " in cmd else ""
                    elif way == ">>" and cmd.startswith("bestmove") and pid in position:
                        searches.append((pid, position[pid], when(t), cmd.split()[1]))
                elif "POST /api/bot/game/" in msg:
                    p = POST.search(msg)
                    if p and p[3] == "200":
                        posts.setdefault((p[1], p[2]), []).append(when(t))
    return states, searches, posts, games


def moves_of(states, searches, posts, games):
    out = []
    by_move = {}
    for (game, uci), times in posts.items():
        for t in times:
            by_move.setdefault(uci, []).append((t, game))
    for pid, before, t_best, best in searches:
        after = (before + " " + best).strip()
        if before not in states or after not in states:
            continue
        # the state that gave us the move: the last one with this list before bestmove; the echo: the first after it
        given = [s for s in states[before] if s[0] <= t_best]
        echo = [s for s in states[after] if s[0] >= t_best]
        if not given or not echo:
            continue
        s0, s1 = given[-1], echo[0]
        white = len(before.split()) % 2 == 0 if before else True
        k = 1 if white else 2
        inc = s0[3] if white else s0[4]
        if (s1[0] - s0[0]).total_seconds() > 600:
            continue
        sent = [(t, g) for t, g in by_move.get(best, []) if t_best <= t <= t_best + datetime.timedelta(seconds=30)]
        if not sent:
            continue
        t_post, game = min(sent)
        charged = (s0[k] - s1[k] + inc) / 1000
        engine = (t_best - s0[0]).total_seconds()
        post = (t_post - t_best).total_seconds()
        initial, increment = games.get(game, (None, inc))
        out.append({"game": game, "time": s0[0], "ply": len(before.split()) if before else 0,
                    "clock": s0[k] / 1000, "after": s1[k] / 1000, "inc": inc / 1000,
                    "base": initial / 1000 if initial else None, "charged": charged, "engine": engine,
                    "post": post, "hidden": charged - engine - post})
    return out


def q(xs, p):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, int(p * len(xs)))] if xs else float("nan")


def row(label, sel):
    if not sel:
        return f"| {label} | 0 | | | | | |"
    c = [m["charged"] for m in sel]
    e = [m["engine"] for m in sel]
    p = [m["post"] for m in sel]
    h = [m["hidden"] for m in sel]
    return (f"| {label} | {len(sel):,} | {statistics.median(c):.2f} | {statistics.median(e):.2f} | "
            f"{statistics.median(p):.2f} ({q(p, 0.9):.2f}) | {statistics.median(h):.2f} ({q(h, 0.9):.2f}) | "
            f"{statistics.median([pp + hh for pp, hh in zip(p, h)]):.2f} ({q([pp + hh for pp, hh in zip(p, h)], 0.9):.2f}) |")


def main(args):
    out_csv = None
    rest = list(args)
    if "--csv" in rest:
        i = rest.index("--csv")
        out_csv = rest[i + 1]
        del rest[i:i + 2]
    paths = rest or sorted(glob.glob(LOGS))
    if not paths:
        raise SystemExit(f"no logs at {LOGS} (run from the main checkout, or name the logs)")
    states, searches, posts, games = parse(paths)
    moves = moves_of(states, searches, posts, games)
    if not moves:
        raise SystemExit("no moves paired in those logs (DEBUG logs from 2026-10-15 on are wanted)")
    print(f"{len(moves):,} of the bot's moves paired, from {len(paths)} logs "
          f"({min(m['time'] for m in moves):%Y-%m-%d} to {max(m['time'] for m in moves):%Y-%m-%d}, local time)\n")
    print("Seconds a move, median (90th percentile): charged = what lichess.org took off our clock; engine = state "
          "in -> bestmove; post = bestmove -> POST answered; hidden = the rest; lag = post + hidden, the part the "
          "engine never sees.\n")
    print("| moves | n | charged | engine | post | hidden | lag |")
    print("|---|---|---|---|---|---|---|")
    print(row("all", moves))
    for lo, hi, label in ((0, 10, "our clock under 10 s"), (10, 30, "10-30 s"), (30, 1e9, "30 s or more")):
        print(row(label, [m for m in moves if lo <= m["clock"] < hi]))
    for label, want in (("no increment", 0), ("with increment", 1)):
        print(row(label, [m for m in moves if (m["inc"] > 0) == want]))
    instant = [m for m in moves if m["engine"] < 0.05]
    print(row("engine under 0.05 s (book, tables, ponder hits)", instant))
    days = sorted({m["time"].date() for m in moves})
    for d in days:
        print(row(f"{d:%m-%d}", [m for m in moves if m["time"].date() == d]))

    # Where the clock settles: the change a move (increment less charged), by increment and clock. The engine plans
    # clock/30 + 3/4 of the increment and the lag comes on top, so with a small increment it can't stop falling.
    print("\n### Our clock's change a move, mean seconds (moves), by increment and clock\n")
    bands = ((0, 10), (10, 20), (20, 30), (30, 60))
    print("| increment | " + " | ".join(f"clock {lo}-{hi} s" for lo, hi in bands) + " |")
    print("|---|" + "---|" * len(bands))
    for inc in sorted({m["inc"] for m in moves}):
        cells = []
        for lo, hi in bands:
            sel = [m["inc"] - m["charged"] for m in moves if m["inc"] == inc and lo <= m["clock"] < hi]
            cells.append(f"{statistics.mean(sel):+.2f} ({len(sel)})" if len(sel) >= 20 else "-")
        print(f"| +{inc:g} | " + " | ".join(cells) + " |")

    # The lag's share of the clock in games without increment: what the engine's plan doesn't know about.
    print("\n### Games without increment: lag against the clock\n")
    print("| game | clock | our moves seen | lag, total s | lag, s a move | our clock at the end |")
    print("|---|---|---|---|---|---|")
    by_game = {}
    for m in moves:
        by_game.setdefault(m["game"], []).append(m)
    for g, ms in sorted(by_game.items(), key=lambda kv: kv[1][0]["time"]):
        if ms[0]["inc"] == 0 and ms[0]["base"]:
            lag = sum(m["post"] + m["hidden"] for m in ms)
            print(f"| {g} | {ms[0]['base'] / 60:g}+0 | {len(ms)} | {lag:.1f} | {lag / len(ms):.2f} | "
                  f"{ms[-1]['after']:.1f} s |")

    if out_csv:
        with open(out_csv, "w", encoding="utf-8", newline="") as f:
            w = csv.DictWriter(f, fieldnames=list(moves[0].keys()))
            w.writeheader()
            w.writerows(moves)
        print(f"\nwrote {out_csv}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
