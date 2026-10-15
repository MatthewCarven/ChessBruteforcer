"""Is pondering worth anything? Our engine with ponder on against the same engine with ponder off, driven the way
lichess-bot drives it: python-chess's engine.play(..., ponder=True), which after each "bestmove <move> ponder <reply>"
sends "go ponder" on the position after that reply, then "ponderhit" if the reply comes or "stop" if it doesn't.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/ponder-match.py <engine .exe> <results .jsonl>
        [--games 140] [--tc 10+0.1] [--concurrency 2] [--cpus 2,4,6,8] [--openings games/tune/openings-300.txt]
        [--eval bot/eval/eval-sf1.txt] [--hash 64] [--threads 1]

Both sides are the same build with the same evaluation (the bot's, --eval) and no endgame tables. Openings come in
colour-reversed pairs, so each opening is played once with each side pondering. Real clocks: a side whose time runs
out loses.

On one machine the pondering side thinks while its opponent does, so the two would share cores where on Lichess the
opponent's computer is somewhere else. So every engine gets a logical CPU of its own (--cpus, two a game: even
numbers, one per physical core on the i9-11900H, whose CPUs 2k and 2k+1 share a core) and one thread; the two swap
CPUs every game, so neither arm keeps a busier core. Engines run at below-normal priority, so the bot comes first.

Each game is appended to the results file as it ends; run again with the same file to add more. The summary (and
`--summary` alone) gives the ponder side's score and Elo with a 95% interval, how often it named a reply to ponder
on, how often that reply came (the ponder hit rate), and the clock each side used per move.
"""
import ctypes, json, math, os, queue, sys, threading, time
from concurrent.futures import ThreadPoolExecutor
import chess, chess.engine

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def options(args, spec):
    values = {}
    for name, (default, kind) in spec.items():
        values[name] = default
        if "--" + name in args:
            i = args.index("--" + name)
            values[name] = kind(args[i + 1]) if kind is not bool else True
            del args[i:i + (1 if kind is bool else 2)]
    return values, args


def elo(score):
    score = min(max(score, 0.001), 0.999)
    return -400 * math.log10(1 / score - 1)


kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
kernel32.GetCurrentProcess.restype = ctypes.c_void_p
kernel32.OpenProcess.restype = ctypes.c_void_p
kernel32.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
kernel32.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
kernel32.SetProcessAffinityMask.argtypes = [ctypes.c_void_p, ctypes.c_size_t]
kernel32.CloseHandle.argtypes = [ctypes.c_void_p]


def below_normal():
    """This process at below-normal priority; the engines it starts inherit it."""
    if not kernel32.SetPriorityClass(kernel32.GetCurrentProcess(), 0x4000):
        raise OSError(ctypes.get_last_error(), "SetPriorityClass failed")


def pin(engine, cpu):
    """Run the engine's process on one logical CPU only."""
    handle = kernel32.OpenProcess(0x0200 | 0x1000, False, engine.transport.get_pid())   # SET_INFORMATION | QUERY_LIMITED
    if not handle:
        raise OSError(ctypes.get_last_error(), "OpenProcess failed")
    try:
        if not kernel32.SetProcessAffinityMask(handle, 1 << cpu):
            raise OSError(ctypes.get_last_error(), f"SetProcessAffinityMask to CPU {cpu} failed")
    finally:
        kernel32.CloseHandle(handle)


def summary(path):
    games = [json.loads(line) for line in open(path, encoding="utf-8") if line.strip()]
    if not games:
        print(f"no games in {path}")
        return
    print(f"{len(games)} games in {path} ({games[0]['tc']}), from the pondering side's point of view")
    points = [g["points"] for g in games]
    n = len(points)
    mean = sum(points) / n
    sd = math.sqrt(sum((p - mean) ** 2 for p in points) / max(1, n - 1))
    half = 1.96 * sd / math.sqrt(n)
    w = sum(p == 1 for p in points); d = sum(p == 0.5 for p in points); l = n - w - d
    flags = sum(g["termination"] == "time" for g in games)
    print(f"  +{w} -{l} ={d}  score {100 * mean:.1f}% (+-{100 * half:.1f})  "
          f"Elo {elo(mean):+.0f} [{elo(mean - half):+.0f}, {elo(mean + half):+.0f}]  games lost on time {flags}")
    moves = sum(g["ponder_moves"] for g in games)
    named = sum(g["ponder_named"] for g in games)
    answered = sum(g["ponder_answered"] for g in games)
    hits = sum(g["ponder_hits"] for g in games)
    print(f"  pondering side: {moves} moves, a reply named on {named} ({100 * named / max(1, moves):.1f}%), "
          f"answered on {answered}, the reply right {hits} times: hit rate {100 * hits / max(1, answered):.1f}%")
    for side in ("on", "off"):
        used = sum(g[f"used_{side}"] for g in games)
        count = sum(g[f"moves_{side}"] for g in games)
        print(f"  ponder {side:3s}: {used / max(1, count) * 1000:.0f} ms of clock a move")
    hit_used = sum(g["used_on_hits"] for g in games)
    print(f"  ponder on, after a hit: {hit_used / max(1, hits) * 1000:.0f} ms a move; "
          f"otherwise {(sum(g['used_on'] for g in games) - hit_used) / max(1, sum(g['moves_on'] for g in games) - hits) * 1000:.0f}")


def main():
    opts, args = options(sys.argv[1:], {
        "games": (140, int), "tc": ("10+0.1", str), "concurrency": (2, int), "cpus": ("2,4,6,8", str),
        "openings": (os.path.join(REPO, "games", "tune", "openings-300.txt"), str),
        "eval": (os.path.join(REPO, "bot", "eval", "eval-sf1.txt"), str), "hash": (64, int), "threads": (1, int),
        "summary": (False, bool)})
    if opts["summary"]:
        summary(args[0])
        return
    engine_path, path = args
    base, inc = (float(x) for x in opts["tc"].split("+"))
    cpus = [int(c) for c in opts["cpus"].split(",")]
    if len(cpus) < 2 * opts["concurrency"]:
        raise SystemExit(f"--cpus needs two CPUs a game: {2 * opts['concurrency']} for --concurrency {opts['concurrency']}")
    openings = [line.split() for line in open(opts["openings"], encoding="utf-8")
                if line.strip() and not line.startswith("#")]
    done = sum(1 for line in open(path, encoding="utf-8") if line.strip()) if os.path.exists(path) else 0
    below_normal()

    lock = threading.Lock()
    local = threading.local()
    slots = queue.Queue()
    for s in range(opts["concurrency"]):
        slots.put(s)
    opened = []   # every engine started, to quit at the end (left running, they keep Python from exiting)
    config = {"EvalFile": opts["eval"], "Hash": opts["hash"], "Move Overhead": 30, "Threads": opts["threads"]}

    def engines():
        if not hasattr(local, "on"):
            local.slot = slots.get()
            local.on = chess.engine.SimpleEngine.popen_uci(engine_path)
            local.off = chess.engine.SimpleEngine.popen_uci(engine_path)
            for engine in (local.on, local.off):
                engine.configure(config)
            with lock:
                opened.extend([local.on, local.off])
        return local.on, local.off, local.slot

    def play(number):
        on, off, slot = engines()
        first, second = cpus[2 * slot], cpus[2 * slot + 1]
        pin(on, first if number % 2 == 0 else second)
        pin(off, second if number % 2 == 0 else first)
        on_white = number % 2 == 0
        opening = openings[(number // 2) % len(openings)]
        board = chess.Board()
        for uci in opening:
            board.push_uci(uci)
        clock = {chess.WHITE: base, chess.BLACK: base}
        game_id = object()
        termination = None
        stats = {"ponder_moves": 0, "ponder_named": 0, "ponder_answered": 0, "ponder_hits": 0,
                 "used_on": 0.0, "moves_on": 0, "used_off": 0.0, "moves_off": 0, "used_on_hits": 0.0}
        expected = None   # the reply the pondering side named with its last move
        hit = False
        while not board.is_game_over(claim_draw=True) and board.ply() < 600:
            ours = board.turn == (chess.WHITE if on_white else chess.BLACK)
            limit = chess.engine.Limit(white_clock=max(0.01, clock[chess.WHITE]), black_clock=max(0.01, clock[chess.BLACK]),
                                       white_inc=inc, black_inc=inc)
            started = time.perf_counter()
            # A copy: python-chess keeps the board it's given, to spot a ponder hit by comparing the next one
            # with what it pondered on. Given this board, which then moves on, it took the ping at the end of a
            # game that finished on the reply it expected for a hit, so it never sent "stop", and the next
            # game's first move came from the old search (f5f4 in a new opening; lichess-bot makes a new board
            # every move and a new engine every game, so the bot can't meet this).
            result = (on if ours else off).play(board.copy(), limit, game=game_id, ponder=ours)
            used = time.perf_counter() - started
            clock[board.turn] -= used
            if ours:
                stats["ponder_moves"] += 1
                stats["used_on"] += used
                stats["moves_on"] += 1
                if hit:
                    stats["used_on_hits"] += used
                expected = result.ponder
                stats["ponder_named"] += expected is not None
            else:
                stats["used_off"] += used
                stats["moves_off"] += 1
                hit = expected is not None and result.move == expected
                if expected is not None:
                    stats["ponder_answered"] += 1
                    stats["ponder_hits"] += hit
                expected = None
            if clock[board.turn] < 0:
                termination = "time"
                break
            clock[board.turn] += inc
            board.push(result.move)
        on.ping()   # stops the ponder on the last expected reply, which will never come
        if termination == "time":
            points = 0.0 if board.turn == (chess.WHITE if on_white else chess.BLACK) else 1.0
        else:
            outcome = board.outcome(claim_draw=True)
            points = 0.5 if outcome is None or outcome.winner is None else 1.0 if outcome.winner == on_white else 0.0
            termination = outcome.termination.name.lower() if outcome else "move limit"
        row = {"game": number, "ponder": "white" if on_white else "black", "points": points,
               "termination": termination, "plies": board.ply(), "opening": " ".join(opening),
               "clock_on_end": round(clock[chess.WHITE if on_white else chess.BLACK], 2),
               "clock_off_end": round(clock[chess.BLACK if on_white else chess.WHITE], 2),
               "tc": opts["tc"], **{k: round(v, 3) if isinstance(v, float) else v for k, v in stats.items()},
               "moves": " ".join(m.uci() for m in board.move_stack)}
        with lock:
            with open(path, "a", encoding="utf-8") as out:
                out.write(json.dumps(row) + "\n")
            print(f"game {number}: ponder side {row['ponder']:5s} {points} ({termination}, {board.ply()} plies, "
                  f"hits {stats['ponder_hits']}/{stats['ponder_answered']}, clocks left {row['clock_on_end']:.1f} "
                  f"on / {row['clock_off_end']:.1f} off)", flush=True)

    try:
        with ThreadPoolExecutor(opts["concurrency"]) as pool:
            for future in [pool.submit(play, n) for n in range(done, done + opts["games"])]:
                future.result()
    finally:
        for engine in opened:
            try:
                engine.quit()
            except Exception:
                pass
    summary(path)


if __name__ == "__main__":
    main()
