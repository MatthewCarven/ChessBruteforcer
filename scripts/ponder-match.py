"""Is pondering worth anything? Our engine with ponder on against the same engine with ponder off, driven the way
lichess-bot drives it: python-chess's engine.play(..., ponder=True), which after each "bestmove <move> ponder <reply>"
sends "go ponder" on the position after that reply, then "ponderhit" if the reply comes or "stop" if it doesn't.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/ponder-match.py <engine .exe> <results .jsonl>
        [--games 140] [--tc 10+0.1] [--concurrency 2] [--cpus 2,4,6,8] [--openings games/tune/openings-300.txt]
        [--eval bot/eval/eval-sf1.txt] [--hash 64] [--threads 1]
        [--arms "PonderFinish=true;PonderFinish=false"] [--sprt 0,10]

Both sides are the same build with the same evaluation (the bot's, --eval) and no endgame tables. Openings come in
colour-reversed pairs, so each opening is played once with each side as white. Real clocks: a side whose time runs
out loses.

Two modes. Plain: side A ponders and side B doesn't. --arms (an A/B match, for changes to pondering itself): both
sides ponder, and they differ only by the UCI options given for each arm, A's before the ";" and B's after it (an
arm's options are name=value pairs split by ",", and either arm may be empty). Results are from A's point of view.

On one machine the pondering side thinks while its opponent does, so the two would share cores where on Lichess the
opponent's computer is somewhere else. So every engine gets a logical CPU of its own (--cpus, two a game: even
numbers, one per physical core on the i9-11900H, whose CPUs 2k and 2k+1 share a core) and one thread; the two swap
CPUs every game, so neither arm keeps a busier core. Engines run at below-normal priority, so the bot comes first.

Each game is appended to the results file as it ends; run again with the same file to add more (--games more). With
--sprt elo0,elo1 the run stops starting games once the SPRT (fishtest's simple normal form, as the C# match tool's,
alpha = beta = 0.05) accepts either hypothesis, counting the games already in the file; games under way still finish.
The summary (and `--summary` alone) gives A's score and Elo with a 95% interval (and the LLR with --sprt), and for each
side: how often it named a reply to ponder on, how often that reply came (the ponder hit rate), the clock it used a
move (after a hit, and otherwise) and the clock it had left at the end of the game.
"""
import ctypes, json, math, os, queue, sys, threading, time
from concurrent.futures import ThreadPoolExecutor
import chess, chess.engine

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SPRT_BOUNDS = (math.log(0.05 / 0.95), math.log(0.95 / 0.05))   # alpha = beta = 0.05: -2.94, +2.94


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


def score_from_elo(e):
    return 1 / (1 + 10 ** (-e / 400))


def llr(points, elo0, elo1):
    """Log-likelihood ratio of elo1 over elo0 (normal approximation, as MatchStats.LogLikelihoodRatio)."""
    n = len(points)
    if n == 0:
        return 0.0
    mean = sum(points) / n
    var = sum((p - mean) ** 2 for p in points) / n
    if var == 0:
        return 0.0
    s0, s1 = score_from_elo(elo0), score_from_elo(elo1)
    return n * (s1 - s0) * (2 * mean - s0 - s1) / (2 * var)


def parse_arms(text):
    """'A1=x,A2=y;B1=z' -> [{'A1': x, 'A2': y}, {'B1': z}], with true/false as bools and whole numbers as ints."""
    def value(v):
        v = v.strip()
        if v.lower() in ("true", "false"):
            return v.lower() == "true"
        try:
            return int(v)
        except ValueError:
            return v
    parts = text.split(";")
    if len(parts) != 2:
        raise SystemExit(f'--arms needs two arms split by ";": {text!r}')
    arms = []
    for part in parts:
        arm = {}
        for pair in filter(None, (p.strip() for p in part.split(","))):
            if "=" not in pair:
                raise SystemExit(f"--arms: {pair!r} isn't name=value")
            name, v = pair.split("=", 1)
            arm[name.strip()] = value(v)
        arms.append(arm)
    return arms


def label(arm):
    return ", ".join(f"{k}={str(v).lower() if isinstance(v, bool) else v}" for k, v in arm.items()) or "(defaults)"


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


def normalised(g):
    """A game row in this version's form: side A and side B. Rows from before the A/B mode (ponder on v off) map
    to A = ponder on, B = ponder off."""
    if "a" in g:
        return g
    return {**g, "a": g["ponder"], "mode": "plain", "arms": ["ponder on", "ponder off"],
            "clock_a_end": g["clock_on_end"], "clock_b_end": g["clock_off_end"],
            "moves_a": g["moves_on"], "used_a": g["used_on"], "named_a": g["ponder_named"],
            "answered_a": g["ponder_answered"], "hits_a": g["ponder_hits"], "used_hits_a": g["used_on_hits"],
            "moves_b": g["moves_off"], "used_b": g["used_off"], "named_b": 0, "answered_b": 0, "hits_b": 0,
            "used_hits_b": 0.0}


def summary(path, sprt=None):
    games = [normalised(json.loads(line)) for line in open(path, encoding="utf-8") if line.strip()]
    if not games:
        print(f"no games in {path}")
        return
    first = games[0]
    tc = first["tc"]
    base = float(tc.split("+")[0])
    print(f"{len(games)} games in {path} ({tc}), from A's point of view")
    print(f"  A: {first['arms'][0]}")
    print(f"  B: {first['arms'][1]}" + ("" if first["mode"] == "ab" else " (not pondering)"))
    points = [g["points"] for g in games]
    n = len(points)
    mean = sum(points) / n
    sd = math.sqrt(sum((p - mean) ** 2 for p in points) / max(1, n - 1))
    half = 1.96 * sd / math.sqrt(n)
    w = sum(p == 1 for p in points); d = sum(p == 0.5 for p in points); l = n - w - d
    line = (f"  +{w} -{l} ={d}  score {100 * mean:.1f}% (+-{100 * half:.1f})  "
            f"Elo {elo(mean):+.0f} [{elo(mean - half):+.0f}, {elo(mean + half):+.0f}]")
    if sprt:
        value = llr(points, *sprt)
        verdict = ("H1 accepted" if value >= SPRT_BOUNDS[1] else "H0 accepted" if value <= SPRT_BOUNDS[0]
                   else "undecided")
        line += f"  SPRT [{sprt[0]:g}, {sprt[1]:g}] LLR {value:.2f} ({SPRT_BOUNDS[0]:.2f}, {SPRT_BOUNDS[1]:.2f}): {verdict}"
    print(line)
    on_time = [g for g in games if g["termination"] == "time"]
    if on_time:
        lost_a = sum(g["points"] == 0 for g in on_time)
        print(f"  games lost on time: A {lost_a}, B {len(on_time) - lost_a}")
    else:
        print("  games lost on time: none")
    print("  side | moves | reply named | hit rate | clock a move | after a hit | otherwise | clock left at the end")
    for side in ("a", "b"):
        moves = sum(g[f"moves_{side}"] for g in games)
        named = sum(g[f"named_{side}"] for g in games)
        answered = sum(g[f"answered_{side}"] for g in games)
        hits = sum(g[f"hits_{side}"] for g in games)
        used = sum(g[f"used_{side}"] for g in games)
        used_hits = sum(g[f"used_hits_{side}"] for g in games)
        left = sum(g[f"clock_{side}_end"] for g in games) / n
        pondering = side == "a" or first["mode"] == "ab"
        rate = f"{100 * hits / max(1, answered):.1f}%" if pondering else "-"
        print(f"  {side.upper()}    | {moves} | {100 * named / max(1, moves):.1f}% | {rate} | "
              f"{used / max(1, moves) * 1000:.0f} ms | "
              + (f"{used_hits / max(1, hits) * 1000:.0f} ms | "
                 f"{(used - used_hits) / max(1, moves - hits) * 1000:.0f} ms | " if pondering else "- | - | ")
              + f"{left:.2f} s ({100 * left / base:.0f}%)")


def main():
    opts, args = options(sys.argv[1:], {
        "games": (140, int), "tc": ("10+0.1", str), "concurrency": (2, int), "cpus": ("2,4,6,8", str),
        "openings": (os.path.join(REPO, "games", "tune", "openings-300.txt"), str),
        "eval": (os.path.join(REPO, "bot", "eval", "eval-sf1.txt"), str), "hash": (64, int), "threads": (1, int),
        "arms": ("", str), "sprt": ("", str), "summary": (False, bool)})
    sprt = tuple(float(x) for x in opts["sprt"].split(",")) if opts["sprt"] else None
    if opts["summary"]:
        summary(args[0], sprt)
        return
    engine_path, path = args
    arms = parse_arms(opts["arms"]) if opts["arms"] else [{}, {}]
    mode = "ab" if opts["arms"] else "plain"
    ponders = {"a": True, "b": mode == "ab"}
    labels = [label(arms[0]), label(arms[1])] if mode == "ab" else ["ponder on", "ponder off"]
    base, inc = (float(x) for x in opts["tc"].split("+"))
    cpus = [int(c) for c in opts["cpus"].split(",")]
    if len(cpus) < 2 * opts["concurrency"]:
        raise SystemExit(f"--cpus needs two CPUs a game: {2 * opts['concurrency']} for --concurrency {opts['concurrency']}")
    openings = [line.split() for line in open(opts["openings"], encoding="utf-8")
                if line.strip() and not line.startswith("#")]
    earlier = [normalised(json.loads(line)) for line in open(path, encoding="utf-8") if line.strip()] \
        if os.path.exists(path) else []
    if earlier and (earlier[0]["mode"], earlier[0]["arms"], earlier[0]["tc"]) != (mode, labels, opts["tc"]):
        raise SystemExit(f"{path} holds games of {earlier[0]['arms']} at {earlier[0]['tc']}, not {labels} at {opts['tc']}")
    done = len(earlier)
    points = [g["points"] for g in earlier]   # every game so far, for the SPRT
    below_normal()

    lock = threading.Lock()
    local = threading.local()
    slots = queue.Queue()
    for s in range(opts["concurrency"]):
        slots.put(s)
    opened = []   # every engine started, to quit at the end (left running, they keep Python from exiting)
    config = {"EvalFile": opts["eval"], "Hash": opts["hash"], "Move Overhead": 30, "Threads": opts["threads"]}
    decided = threading.Event()

    def engines():
        if not hasattr(local, "a"):
            local.slot = slots.get()
            local.a = chess.engine.SimpleEngine.popen_uci(engine_path)
            local.b = chess.engine.SimpleEngine.popen_uci(engine_path)
            for engine, arm in ((local.a, arms[0]), (local.b, arms[1])):
                engine.configure({**config, **arm})
            with lock:
                opened.extend([local.a, local.b])
        return local.a, local.b, local.slot

    def play(number):
        if decided.is_set():
            return
        a, b, slot = engines()
        engine = {"a": a, "b": b}
        first, second = cpus[2 * slot], cpus[2 * slot + 1]
        pin(a, first if number % 2 == 0 else second)
        pin(b, second if number % 2 == 0 else first)
        a_white = number % 2 == 0
        colour = {"a": chess.WHITE if a_white else chess.BLACK, "b": chess.BLACK if a_white else chess.WHITE}
        opening = openings[(number // 2) % len(openings)]
        board = chess.Board()
        for uci in opening:
            board.push_uci(uci)
        clock = {chess.WHITE: base, chess.BLACK: base}
        game_id = object()
        termination = None
        stats = {f"{k}_{s}": 0 for s in ("a", "b") for k in ("moves", "named", "answered", "hits")}
        stats.update({f"{k}_{s}": 0.0 for s in ("a", "b") for k in ("used", "used_hits")})
        expected = {"a": None, "b": None}   # the reply each side named with its last move
        hit = {"a": False, "b": False}      # whether the move each side now answers is the one it named
        while not board.is_game_over(claim_draw=True) and board.ply() < 600:
            side = "a" if board.turn == colour["a"] else "b"
            other = "b" if side == "a" else "a"
            limit = chess.engine.Limit(white_clock=max(0.01, clock[chess.WHITE]), black_clock=max(0.01, clock[chess.BLACK]),
                                       white_inc=inc, black_inc=inc)
            started = time.perf_counter()
            # A copy: python-chess keeps the board it's given, to spot a ponder hit by comparing the next one
            # with what it pondered on. Given this board, which then moves on, it took the ping at the end of a
            # game that finished on the reply it expected for a hit, so it never sent "stop", and the next
            # game's first move came from the old search (f5f4 in a new opening; lichess-bot makes a new board
            # every move and a new engine every game, so the bot can't meet this).
            result = engine[side].play(board.copy(), limit, game=game_id, ponder=ponders[side])
            used = time.perf_counter() - started
            clock[board.turn] -= used
            stats[f"moves_{side}"] += 1
            stats[f"used_{side}"] += used
            if hit[side]:
                stats[f"used_hits_{side}"] += used
            if ponders[side]:
                expected[side] = result.ponder
                stats[f"named_{side}"] += expected[side] is not None
            # this move answers the other side's pondering
            hit[other] = expected[other] is not None and result.move == expected[other]
            if expected[other] is not None:
                stats[f"answered_{other}"] += 1
                stats[f"hits_{other}"] += hit[other]
            expected[other] = None
            if clock[board.turn] < 0:
                termination = "time"
                break
            clock[board.turn] += inc
            board.push(result.move)
        for e in (a, b):
            e.ping()   # stops a ponder on the last expected reply, which will never come
        if termination == "time":
            points = 0.0 if board.turn == colour["a"] else 1.0
        else:
            outcome = board.outcome(claim_draw=True)
            points = 0.5 if outcome is None or outcome.winner is None else 1.0 if outcome.winner == colour["a"] else 0.0
            termination = outcome.termination.name.lower() if outcome else "move limit"
        row = {"game": number, "mode": mode, "arms": labels, "a": "white" if a_white else "black", "points": points,
               "termination": termination, "plies": board.ply(), "opening": " ".join(opening),
               "clock_a_end": round(clock[colour["a"]], 2), "clock_b_end": round(clock[colour["b"]], 2),
               "tc": opts["tc"], **{k: round(v, 3) if isinstance(v, float) else v for k, v in stats.items()},
               "moves": " ".join(m.uci() for m in board.move_stack)}
        with lock:
            with open(path, "a", encoding="utf-8") as out:
                out.write(json.dumps(row) + "\n")
            points_so_far.append(points)
            note = ""
            if sprt:
                value = llr(points_so_far, *sprt)
                note = f", LLR {value:.2f}"
                if not SPRT_BOUNDS[0] < value < SPRT_BOUNDS[1]:
                    decided.set()
                    note += " (decided)"
            print(f"game {number}: A {row['a']:5s} {points} ({termination}, {board.ply()} plies, "
                  f"hits A {stats['hits_a']}/{stats['answered_a']} B {stats['hits_b']}/{stats['answered_b']}, "
                  f"clocks left A {row['clock_a_end']:.1f} / B {row['clock_b_end']:.1f}{note})", flush=True)

    points_so_far = points
    if sprt and not SPRT_BOUNDS[0] < llr(points, *sprt) < SPRT_BOUNDS[1]:
        print("the SPRT has decided already")
        decided.set()
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
    summary(path, sprt)


if __name__ == "__main__":
    main()
