"""BT4, Matthew's way (2026-10-25): the books against each other, our engine on both sides; then the winner against no book.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/book-tables/book-duel.py run <engine exe> <games.jsonl>
        --arms table-a,table-b,vetted [--games 1000] [--concurrency 6] [--cpus 2-13] [--tc 10+0.1] [--sprt 0,10]
        [--stop-at HH:MM] [--eval <file>] [--seed 1] [--dry]
    ... book-duel.py summary <games.jsonl>

Matthew: "all three should go against each other then the winner goes against a cpu without using any to see if
there is benefit". So: --arms table-a,table-b,vetted, a round robin, then --arms <winner>,none in a file of its own.

The arms, each side's books in the order tried (the first that has the position, weighted at random, as lichess-bot
chooses), for its first 12 moves:
    human     lichess-2200.bin, a copy of ours.bin, lichess-2200-wide.bin (the bot's chain, bot/make-config.py)
    table-a   BT1 (a)'s games\\book-table-a\\table-a-d8.bin, then the human chain
    table-b   BT1 (b)'s results\\table-b.bin, then the human chain
    vetted    BT1 (c)'s games\\lichess-2200-vetted-black.bin in place of lichess-2200.bin (White's moves the same)
    none      no book: the engine from move 1
Every pair of arms meets: game g is pair (g // 2) mod the number of pairs, the pair's first arm White in even games and
Black in odd, so each pair plays in colour-reversed twos and the pairs take turns. --games is games a pair (raise it
and run again to add more). ours.bin is copied beside games.jsonl at the first run (the learner rewrites it live and
can't while a reader holds it); the books' hashes go in <games>.books.json, and a run refuses books that have changed.

run: our engine on both sides, as vet-c-match.py plays it: two processes a game, one thread, Hash 64, Move Overhead 30,
no endgame tables, the bot's net (as bot/make-config.py picks it), no pondering, each pinned to one of --cpus (two a
game) at below-normal priority. Real clocks at --tc (book moves are instant, the increment still counts); a side
whose time runs out loses; draws as python-chess claims them. Each game is appended to games.jsonl as it ends;
started again it carries on with the games not there. It stops at --games, at --stop-at HH:MM (no new games in its
last 90 s; games still going then are dropped; exit code 3), or, with --sprt and a single pair, once the SPRT
decides (on the first arm's score in each colour-reversed two, normal approximation, alpha = beta = 0.05). --dry:
random movers in place of the engines (a check).

summary: each pair, from its first arm's side: W/L/D, the score and Elo with a 95% interval (from the colour-reversed
twos), the SPRT if asked, and how many different openings (the first 12 plies) it played; then each arm's score over
all its games, the round robin's table.
"""
import importlib.util, itertools, json, math, os, queue, random, shutil, signal, sys, threading, time
import chess, chess.engine, chess.polyglot

HERE = os.path.dirname(os.path.abspath(__file__))
# vet-c-match.py's pieces: the main checkout, the books' paths, engines pinned to a CPU, the clock, the SPRT.
_spec = importlib.util.spec_from_file_location("vet_c_match", os.path.join(HERE, "vet-c-match.py"))
vet = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(vet)
MAIN, BOOK_MOVES = vet.MAIN, vet.BOOK_MOVES
TABLE_A = os.path.join(MAIN, "games", "book-table-a", "table-a-d8.bin")
TABLE_B = os.path.join(HERE, "results", "table-b.bin")
ARMS = ("human", "table-a", "table-b", "vetted", "none")


def chains(ours_copy):
    human = [vet.HUMAN, ours_copy, vet.WIDE]
    return {"human": human, "table-a": [TABLE_A] + human, "table-b": [TABLE_B] + human,
            "vetted": [vet.VETTED, ours_copy, vet.WIDE], "none": []}


def pairs_of(arms):
    return list(itertools.combinations(arms, 2))


def schedule(g, pairs):
    """Game g's (White, Black)."""
    x, y = pairs[(g // 2) % len(pairs)]
    return (x, y) if g % 2 == 0 else (y, x)


def twos(games, x, y):
    """x's points in each complete colour-reversed two of pair (x, y), as a mean a game."""
    by = {}
    for g in games:
        if {g["white"], g["black"]} == {x, y}:
            by.setdefault(g["game"] // 2, []).append(g["points_white"] if g["white"] == x else 1 - g["points_white"])
    return [sum(p) / 2 for p in by.values() if len(p) == 2]


def sprt_value(games, x, y, sprt):
    means = twos(games, x, y)
    if not sprt or not means:
        return len(means), 0.0
    # vet.llr takes differences whose mean is the Elo difference as points: x's mean a game less a half.
    return len(means), vet.llr([m - 0.5 for m in means], sum(means) / len(means), *sprt)


def header_path(out):
    return os.path.splitext(out)[0] + ".books.json"


def setup(out, arms, seed):
    """The books for this file: on the first run, ours.bin copied and every book's hash kept; after, checked."""
    path = header_path(out)
    if os.path.exists(path):
        record = json.load(open(path, encoding="utf-8"))
        if record["arms"] != arms:
            raise SystemExit(f"{out} was started with --arms {','.join(record['arms'])}: use another file for {','.join(arms)}")
        for name, books in chains(record["ours_copy"]).items():
            for b in books if name in arms else []:
                if vet.sha256(b) != record["books"][b]:
                    raise SystemExit(f"{b} has changed since {out} was started: use another file")
        return record
    ours_copy = os.path.splitext(out)[0] + "-ours.bin"
    shutil.copyfile(vet.OURS, ours_copy)
    books = sorted({b for name, c in chains(ours_copy).items() if name in arms for b in c})
    for b in books:
        if not os.path.exists(b):
            raise SystemExit(f"no book {b}")
    record = {"arms": arms, "seed": seed, "ours_copy": ours_copy, "books": {b: vet.sha256(b) for b in books},
              "made": time.strftime("%Y-%m-%d %H:%M:%S")}
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(record, f, indent=1)
    return record


def run(args):
    arms = [a.strip() for a in vet.option(args, "--arms", "").split(",") if a.strip()]
    if len(arms) < 2 or len(set(arms)) < len(arms) or any(a not in ARMS for a in arms):
        raise SystemExit(f"--arms takes two or more of {', '.join(ARMS)}")
    per_pair = vet.option(args, "--games", 1000, int)
    concurrency = vet.option(args, "--concurrency", 6, int)
    cpus = vet.cpu_list(vet.option(args, "--cpus", "2-13"))
    base, inc = (float(x) for x in vet.option(args, "--tc", "10+0.1").split("+"))
    sprt_text = vet.option(args, "--sprt", "")
    sprt = tuple(float(x) for x in sprt_text.split(",")) if sprt_text else None
    deadline = vet.deadline_of(vet.option(args, "--stop-at", ""))
    seed = vet.option(args, "--seed", 1, int)
    evalfile = vet.option(args, "--eval", next((os.path.join(MAIN, "bot", "eval", e) for e in vet.EVALS
                                                if os.path.exists(os.path.join(MAIN, "bot", "eval", e))), ""))
    dry = vet.option(args, "--dry", False, bool)
    exe, out = args
    pairs = pairs_of(arms)
    if sprt and len(pairs) > 1:
        raise SystemExit("--sprt is for two arms (one pair): a round robin plays its --games")
    log_path = os.path.splitext(out)[0] + ".log"
    lock = threading.Lock()

    def log(text):
        line = time.strftime("%Y-%m-%d %H:%M:%S  ") + text
        with lock:
            print(line, flush=True)
            with open(log_path, "a", encoding="utf-8") as f:
                f.write(line + "\n")

    if len(cpus) < 2 * concurrency and not dry:
        raise SystemExit(f"--cpus needs two CPUs a game: {2 * concurrency} for --concurrency {concurrency}")
    if not dry and not os.path.exists(evalfile):
        raise SystemExit(f"no evaluation {evalfile!r}")
    vet.k32.SetPriorityClass(vet.k32.GetCurrentProcess(), 0x4000)   # below normal; the engines inherit it
    need = 2.0 + 0.15 * 2 * concurrency
    if not dry and vet.free_commit_gb() < need:
        log(f"only {vet.free_commit_gb():.2f} GB of commit free, {need:.2f} wanted (2 GB kept free): not starting")
        sys.exit(2)
    record = setup(out, arms, seed)
    books = chains(record["ours_copy"])

    games = vet.read_games(out)
    done = {g["game"] for g in games}
    todo = queue.Queue()
    for g in range(per_pair * len(pairs)):
        if g not in done:
            todo.put(g)
    decided = threading.Event()
    stopping = threading.Event()
    n_twos, value = sprt_value(games, *pairs[0], sprt)
    if sprt and not vet.SPRT_BOUNDS[0] < value < vet.SPRT_BOUNDS[1]:
        decided.set()
    log(f"{'DRY RUN (random movers), ' if dry else ''}{' v '.join(arms)}: {len(pairs)} pair(s), {per_pair} games a pair; "
        f"{len(games)} done already, {todo.qsize()} to go; {concurrency} at a time on CPUs {cpus[:2 * concurrency]}, "
        f"{base:g}+{inc:g}" + (f", SPRT [{sprt[0]:g}, {sprt[1]:g}] (LLR {value:.2f})" if sprt else "")
        + (f", stop at {time.strftime('%H:%M', time.localtime(deadline))}" if deadline else "")
        + ("" if dry else f"; {os.path.basename(evalfile)}; {vet.free_commit_gb():.2f} GB commit free"))

    opened = []
    counts = {"games": 0}

    def engines_for(slot):
        if dry:
            return vet.RandomMover(f"w{slot}"), vet.RandomMover(f"b{slot}")
        pair = []
        for cpu in cpus[2 * slot:2 * slot + 2]:
            e = chess.engine.SimpleEngine.popen_uci(exe)
            e.configure({"EvalFile": evalfile, "Hash": 64, "Threads": 1, "Move Overhead": 30})
            vet.pin(e, cpu)
            with lock:
                opened.append(e)
            pair.append(e)
        return tuple(pair)

    def play(g, white, black, readers):
        arm = dict(zip((chess.WHITE, chess.BLACK), schedule(g, pairs)))
        rng = random.Random(f"{record['seed']}-{g}")
        board = chess.Board()
        clock = {chess.WHITE: base, chess.BLACK: base}
        book = {chess.WHITE: 0, chess.BLACK: 0}
        game_id, termination = object(), None
        while not board.is_game_over(claim_draw=True) and board.ply() < 600:
            move = None
            if board.ply() < 2 * BOOK_MOVES:
                moves = vet.dist(readers[arm[board.turn]], board)
                if moves:
                    move = vet.pick(moves, rng.random())
                    book[board.turn] += 1
            if move is None:
                if stopping.is_set():
                    return None
                limit = chess.engine.Limit(white_clock=max(0.01, clock[chess.WHITE]),
                                           black_clock=max(0.01, clock[chess.BLACK]), white_inc=inc, black_inc=inc)
                started = time.perf_counter()
                move = (white if board.turn == chess.WHITE else black).play(board.copy(), limit, game=game_id).move
                clock[board.turn] -= time.perf_counter() - started
                if clock[board.turn] < 0:
                    termination = "time"
                    break
            clock[board.turn] += inc
            board.push(move)
        if termination == "time":
            points = 0.0 if board.turn == chess.WHITE else 1.0
        else:
            outcome = board.outcome(claim_draw=True)
            points = 0.5 if outcome is None or outcome.winner is None else 1.0 if outcome.winner == chess.WHITE else 0.0
            termination = outcome.termination.name.lower() if outcome else "move limit"
        return {"game": g, "white": arm[chess.WHITE], "black": arm[chess.BLACK], "points_white": points,
                "termination": termination, "plies": board.ply(), "book_white": book[chess.WHITE],
                "book_black": book[chess.BLACK], "clock_white_end": round(clock[chess.WHITE], 2),
                "clock_black_end": round(clock[chess.BLACK], 2), "tc": f"{base:g}+{inc:g}",
                "eval": "random" if dry else os.path.basename(evalfile), "when": time.strftime("%Y-%m-%d %H:%M:%S"),
                "moves": " ".join(m.uci() for m in board.move_stack)}

    def work(slot):
        readers = {name: [chess.polyglot.open_reader(b) for b in c] for name, c in books.items() if name in arms}
        white = black = None
        retries = {}
        try:
            while not (stopping.is_set() or decided.is_set()):
                if deadline and time.time() > deadline - 90:
                    break
                try:
                    g = todo.get_nowait()
                except queue.Empty:
                    break
                try:
                    if white is None:
                        white, black = engines_for(slot)
                    row = play(g, white, black, readers)
                except (chess.engine.EngineError, chess.engine.EngineTerminatedError, OSError) as error:
                    for e in (white, black):
                        try:
                            e.quit()
                        except Exception:
                            pass
                    white = black = None
                    if stopping.is_set():
                        return
                    retries[g] = retries.get(g, 0) + 1
                    log(f"[{slot}] game {g}: {error!r}; new engines"
                        + ("; the game goes back in the queue" if retries[g] <= 2 else "; given up"))
                    if retries[g] <= 2:
                        todo.put(g)
                    continue
                if row is None:
                    return
                with lock:
                    with open(out, "a", encoding="utf-8", newline="\n") as f:
                        f.write(json.dumps(row) + "\n")
                    games.append(row)
                    counts["games"] += 1
                    n, value = sprt_value(games, *pairs[0], sprt)
                    if sprt and not vet.SPRT_BOUNDS[0] < value < vet.SPRT_BOUNDS[1]:
                        decided.set()
                log(f"[{slot}] game {g}: {row['white']} v {row['black']} {row['points_white']} ({row['termination']}, "
                    f"{row['plies']} plies, book moves {row['book_white']}+{row['book_black']})"
                    + (f"; {n} twos, LLR {value:.2f}" + (" (decided)" if decided.is_set() else "") if sprt else ""))
        finally:
            for e in (white, black):
                if e is not None:
                    try:
                        e.quit()
                    except Exception:
                        pass
            for rs in readers.values():
                for r in rs:
                    r.close()

    finished = threading.Event()

    def watch():
        while not finished.wait(timeout=5):
            if deadline is not None and time.time() >= deadline:
                stopping.set()
                log("the stop time: stopping the engines; the games under way are dropped, the finished ones kept")
                with lock:
                    running = list(opened)
                for e in running:
                    try:
                        os.kill(e.transport.get_pid(), signal.SIGTERM)
                    except Exception:
                        pass
                return

    threading.Thread(target=watch, daemon=True).start()
    threads = [threading.Thread(target=work, args=(s,)) for s in range(concurrency)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    finished.set()
    why = ("the stop time" if stopping.is_set() or (deadline and time.time() > deadline - 90) else
           "the SPRT decided" if decided.is_set() else "every game played")
    log(f"stopped ({why}): {counts['games']} games this run, {len(games)} in all")
    summary(out, sprt)
    if why == "the stop time":
        sys.exit(3)


def summary(out, sprt=None):
    games = vet.read_games(out)
    record = json.load(open(header_path(out), encoding="utf-8")) if os.path.exists(header_path(out)) else None
    arms = record["arms"] if record else list(dict.fromkeys(a for g in games for a in (g["white"], g["black"])))
    print(f"{len(games)} games in {out}")
    for x, y in pairs_of(arms):
        rows = [g for g in games if {g["white"], g["black"]} == {x, y}]
        if not rows:
            continue
        points = [g["points_white"] if g["white"] == x else 1 - g["points_white"] for g in rows]
        w, d = sum(p == 1 for p in points), sum(p == 0.5 for p in points)
        means = twos(games, x, y)
        n = len(means)
        mean = sum(points) / len(points)
        if n >= 2:
            m = sum(means) / n
            half = 1.96 * math.sqrt(sum((v - m) ** 2 for v in means) / (n - 1)) / math.sqrt(n)
        else:
            half = 0.5
        openings = len({" ".join(g["moves"].split()[:12]) for g in rows})
        flags = sum(g["termination"] == "time" for g in rows)
        line = (f"  {x} v {y}: {len(rows)} games, {x} +{w} -{len(points) - w - d} ={d}, score {100 * mean:.1f}% "
                f"(+-{100 * half:.1f}), Elo {vet.elo(mean):+.0f} [{vet.elo(mean - half):+.0f}, {vet.elo(mean + half):+.0f}]; "
                f"{openings} different openings (12 plies); lost on time {flags}")
        if sprt:
            _, value = sprt_value(games, x, y, sprt)
            verdict = ("accept" if value >= vet.SPRT_BOUNDS[1] else "reject" if value <= vet.SPRT_BOUNDS[0] else "go on")
            line += f"; SPRT [{sprt[0]:g}, {sprt[1]:g}] LLR {value:.2f} ({verdict})"
        print(line)
    if len(arms) > 2:
        print("  the round robin, each arm's score over all its games:")
        table = []
        for a in arms:
            mine = [g["points_white"] if g["white"] == a else 1 - g["points_white"] for g in games if a in (g["white"], g["black"])]
            if mine:
                table.append((sum(mine) / len(mine), len(mine), a))
        for score, n, a in sorted(table, reverse=True):
            print(f"    {a:8s} {n:5d} games  {100 * score:5.1f}%  Elo {vet.elo(score):+.0f} against the others")


def main():
    args = sys.argv[1:]
    if args[:1] == ["run"]:
        run(args[1:])
    elif args[:1] == ["summary"]:
        sprt_text = vet.option(args, "--sprt", "")
        summary(args[1], tuple(float(x) for x in sprt_text.split(",")) if sprt_text else None)
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main()
