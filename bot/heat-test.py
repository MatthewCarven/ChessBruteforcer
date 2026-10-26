"""A heat and speed test for the laptop: several copies of the bot's engine busy at once for 2 minutes, as with that
many bot games pondering, while typeperf records the CPU's use and its clock against base every 2 s.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe bot\\heat-test.py <engines> <threads> [output folder]

Engines pair off and play each other at 90+1, both pondering; an odd one out searches the latest position of the
first game 3 s at a time. First, 10 s of one engine alone for its speed. The engine and Hash come from the bot's
config.yml (bot/make-config.py writes it), the net from bot\\eval\\net.txt as the bot's games take it (else the
config's); no endgame tables, to keep the memory down. Not beside the bot: it would take the bot's cores. NitroSense
shows the temperature; take screenshots during the 2 minutes.

2026-10-25 (WORKLOG 08:25-08:50; Matthew's tests): the CPU sat at 92 C from half load up, whatever the threads or
the fans, and gave up clock instead (151% of base alone to ~130% with 16 threads busy), so the clock and the Mnps are
the measures, not the temperature. Mnps a game: 8 threads 19.0 alone, 10.8 with two on, ~6.5 with three; 4 threads
11.0, 8.5, 6.1.
"""
import csv
import datetime
import os
import statistics
import subprocess
import sys
import tempfile
import threading
import time

import chess
import chess.engine
import yaml

HERE = os.path.dirname(os.path.abspath(__file__))
ALONE, GAME = 10, 120
COUNTERS = [r"\Processor(_Total)\% Processor Time", r"\Processor Information(_Total)\% Processor Performance"]


def now():
    return datetime.datetime.now().strftime("%H:%M:%S")


def bot_engine():
    """The bot's engine and its options from config.yml (only the engine section is read), and the net its games
    play: bot\\eval\\net.txt's when it names one (bot\\use-net.cmd), else the config's."""
    with open(os.path.join(HERE, "lichess-bot", "config.yml"), encoding="utf-8") as f:
        engine = yaml.safe_load(f)["engine"]
    options = {k: engine["uci_options"][k] for k in ("EvalFile", "Hash") if k in engine["uci_options"]}
    try:
        with open(os.path.join(HERE, "eval", "net.txt"), encoding="utf-8") as f:
            name = f.read().strip()
    except OSError:
        name = ""
    if name:
        net = name if os.path.isabs(name) else os.path.join(HERE, "eval", name)
        if os.path.exists(net):
            options["EvalFile"] = net
    return os.path.join(engine["dir"], engine["name"]), options


def game(a, b, nps, fens, t0):
    """A against B at 90+1, both pondering, new games until the time is up; each move's nps under its engine."""
    games = moves = 0
    while time.monotonic() - t0 < GAME:
        board, clock, games = chess.Board(), [90.0, 90.0], games + 1
        white = (a, b) if games % 2 else (b, a)                 # swap colours each game
        while not board.is_game_over(claim_draw=True) and time.monotonic() - t0 < GAME:
            side = 0 if board.turn == chess.WHITE else 1
            limit = chess.engine.Limit(white_clock=clock[0], black_clock=clock[1], white_inc=1, black_inc=1)
            start = time.monotonic()
            result = white[side].play(board, limit, ponder=True, info=chess.engine.INFO_BASIC)
            clock[side] += 1 - (time.monotonic() - start)
            if result.info.get("nps"):
                nps[white[side]].append(result.info["nps"])
            board.push(result.move)
            fens.append(board.fen())
            moves += 1
            if clock[side] < 0:
                break
    print(f"{now()} a game pair: {games} game(s), {moves} moves", flush=True)


def searcher(c, nps, fens, t0):
    """The odd engine out: 3 s searches of the first game's latest position until the time is up."""
    while time.monotonic() - t0 < GAME:
        left = GAME - (time.monotonic() - t0)
        info = c.analyse(chess.Board(fens[-1]), chess.engine.Limit(time=min(3.0, max(0.5, left))))
        if info.get("nps"):
            nps[c].append(info["nps"])


def main(args):
    if len(args) < 2:
        print(__doc__)
        return 2
    n, threads = int(args[0]), int(args[1])
    out = args[2] if len(args) > 2 else tempfile.gettempdir()
    exe, options = bot_engine()
    log = os.path.join(out, f"heat-test-{n}x{threads}-{datetime.datetime.now():%Y%m%d-%H%M}.csv")
    perf = subprocess.Popen(["typeperf", *COUNTERS, "-si", "2", "-sc", str((ALONE + GAME + 16) // 2), "-o", log, "-y"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    engines = []
    for _ in range(n):
        e = chess.engine.SimpleEngine.popen_uci(exe, cwd=os.path.dirname(exe))
        e.configure({**options, "Threads": threads})
        engines.append(e)
    marks, nps, fens = {}, {e: [] for e in engines}, [chess.Board().fen()]
    try:
        marks["alone"] = time.time()
        net = os.path.basename(options.get("EvalFile", "the engine's own"))
        print(f"{now()} one engine alone, {threads} threads, {ALONE} s, net {net}", flush=True)
        alone = engines[0].analyse(chess.Board(), chess.engine.Limit(time=ALONE)).get("nps", 0)
        print(f"{now()} alone: {alone / 1e6:.2f} Mnps", flush=True)

        marks["game"] = time.time()
        print(f"{now()} {n} engines, {threads} threads each, {GAME} s", flush=True)
        t0 = time.monotonic()
        workers = [threading.Thread(target=game, args=(engines[i], engines[i + 1], nps, fens, t0))
                   for i in range(0, n - 1, 2)]
        if n % 2:
            workers.append(threading.Thread(target=searcher, args=(engines[-1], nps, fens, t0)))
        for w in workers:
            w.start()
        for w in workers:
            w.join()
        marks["end"] = time.time()
        print(f"{now()} done", flush=True)
    finally:
        for e in engines:
            try:
                e.quit()
            except Exception:
                pass
    try:
        perf.wait(timeout=20)
    except subprocess.TimeoutExpired:
        perf.terminate()

    total = 0
    for i, e in enumerate(engines):
        if nps[e]:
            m = statistics.median(nps[e])
            total += m
            print(f"engine {i + 1}: {m / 1e6:.2f} Mnps median over {len(nps[e])} searches ({m / alone:.2f}x alone)")
    print(f"together: {total / 1e6:.1f} Mnps ({total / alone:.2f}x one alone)")

    rows = []
    with open(log, newline="") as f:
        for row in list(csv.reader(f))[1:]:
            try:
                t = datetime.datetime.strptime(row[0], "%m/%d/%Y %H:%M:%S.%f").timestamp()
                rows.append((t, float(row[1]), float(row[2])))
            except (ValueError, IndexError):
                pass
    for phase, lo, hi in (("alone", marks["alone"], marks["game"]), ("together", marks["game"], marks["end"])):
        sel = [(u, c) for t, u, c in rows if lo <= t <= hi]
        if sel:
            print(f"{phase}: CPU use median {statistics.median(u for u, _ in sel):.0f}%, "
                  f"clock against base median {statistics.median(c for _, c in sel):.0f}% "
                  f"(min {min(c for _, c in sel):.0f}%, max {max(c for _, c in sel):.0f}%), {len(sel)} samples")
    print(f"every 2 s sample: {log}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
