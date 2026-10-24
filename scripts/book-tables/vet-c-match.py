"""Decision C (a): is the vetted book any worse than the human book as Black? Paired games where the two differ.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/book-tables/vet-c-match.py pairs <pairs.json>
        [--pairs 1500] [--walks 400000] [--seed 1]
    ... vet-c-match.py run <engine exe> <pairs.json> <games.jsonl> [--concurrency 6] [--cpus 2-13] [--tc 10+0.1]
        [--sprt -10,0] [--stop-at HH:MM] [--games N] [--eval <file>] [--dry]
    ... vet-c-match.py summary <pairs.json> <games.jsonl> [--sprt -10,0]

Why pairs. games\\lichess-2200-vetted-black.bin is lichess-2200.bin less 56 moves (and plus one) in 48 positions with
Black to move. A game that never meets one of them goes the same way with either book, so a match of the whole book
spends 97-99% of its Black games (and all its White games) on noise; and the vetted book can't be 10 Elo worse over all
Black games while changing 1-3% of them, unless nearly all of those turned into losses. (A walk of 2,000 lines met a
veto in 0.75%; the bot's own games, ~3%.) So only the lines where the
books differ are played, each twice from the same position: once with the human book's move, once with the vetted
book's.

pairs: walks the opening as the match plays it, from the start: White's moves from lichess-2200-wide.bin (as Stockfish
has it in BOOK-TABLES.md's match), ours (Black) from the human chain (lichess-2200.bin, a copy of ours.bin,
lichess-2200-wide.bin: the first that has the position, weighted at random, as lichess-bot chooses), the first 12 moves
a side. The vetted chain has vetted-black.bin in place of lichess-2200.bin and draws together with the human one (a
maximal coupling: the same move unless the human chain's move is one the vetted chain can't play, then a move from what
the vetted chain has left), so the two part only where the human chain plays a vetoed move. Such a line is a pair. A
walk that leaves both books first, or reaches move 12, is a line where the books can't differ. The share of walks that
pair is how often a Black game meets a veto. Checked first: the two books differ in no position outside the veto list
(results/vet-c-vetoes.csv), so a stale vetted copy is refused. ours.bin is copied beside pairs.json (the learner
rewrites it live); the run reads that copy.

run: each pair's two games, one after the other in the queue: our engine plays both sides (two processes, one thread,
Hash 64, Move Overhead 30, no endgame tables, the bot's net as bot/make-config.py picks it, no pondering), from the
pair's position with that arm's move, then each side's books as above for its first 12 moves (Black: that arm's chain),
then the engine. Real clocks at --tc (the book moves of the line count their increments); a side whose time runs out
loses; draws as python-chess claims them. A game's two engines share one physical core (--cpus, two a game: only one
thinks at a time), at below-normal priority. Each game is appended to games.jsonl as it ends; started again it carries
on with the games not there. It stops starting games once the SPRT decides (games under way still finish), at the end
of the pairs, after --games, or at --stop-at HH:MM (the next such time on the clock: no new games in its last 90 s, and
games still going then are stopped and dropped; exit code 3). --dry: random movers in place of the engines (a check).

SPRT on each pair's difference (the vetted game's points for Black less the human one's), in Elo on the games where the
books differ, normal approximation, alpha = beta = 0.05 (as ponder-match.py's). [-10, 0], a no-regression test, as P5's.
summary: each arm's score as Black, the pairs' difference as Elo with a 95% interval, the SPRT, the share of Black games
the pairs stand for (from pairs.json) and so the whole book's difference, and the pairs by position.
"""
import csv, ctypes, hashlib, json, math, os, queue, random, shutil, signal, struct, subprocess, sys, threading, time
import chess, chess.engine, chess.polyglot

HERE = os.path.dirname(os.path.abspath(__file__))
BOOK_MOVES = 12
ARMS = ("human", "vetted")
SPRT_BOUNDS = (math.log(0.05 / 0.95), math.log(0.95 / 0.05))   # -2.94, +2.94
# bot/make-config.py's order: the first of these in bot\eval is the bot's evaluation.
EVALS = ("h1536-3m-ob8.nnue", "h1536-3m.nnue", "h1024-3m.nnue", "h512-3m.nnue", "nnue1-h512.nnue", "nnue1.nnue",
         "eval-sf1.txt")


def main_checkout():
    """The main checkout (bot\\books and games\\ aren't in git, so a worktree has neither)."""
    repo = os.path.dirname(os.path.dirname(HERE))
    common = subprocess.run(["git", "-C", repo, "rev-parse", "--git-common-dir"], capture_output=True, text=True).stdout.strip()
    return os.path.dirname(os.path.abspath(os.path.join(repo, common)))


MAIN = main_checkout()
HUMAN = os.path.join(MAIN, "bot", "books", "lichess-2200.bin")
VETTED = os.path.join(MAIN, "games", "lichess-2200-vetted-black.bin")
WIDE = os.path.join(MAIN, "bot", "books", "lichess-2200-wide.bin")
OURS = os.path.join(MAIN, "bot", "books", "ours.bin")
VETOES = os.path.join(HERE, "results", "vet-c-vetoes.csv")


def option(args, name, default, kind=str):
    if name not in args:
        return default
    i = args.index(name)
    value = True if kind is bool else kind(args[i + 1])
    del args[i:i + (1 if kind is bool else 2)]
    return value


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def entries_by_key(path):
    data = open(path, "rb").read()
    by = {}
    for i in range(0, len(data), 16):
        key, move, weight, learn = struct.unpack(">QHHI", data[i:i + 16])
        by.setdefault(key, set()).add((move, weight))
    return by


def chains(ours_copy):
    """Each side's books, in the order tried: White (the opponent), and ours (Black) for each arm."""
    return {"white": [WIDE], "human": [HUMAN, ours_copy, WIDE], "vetted": [VETTED, ours_copy, WIDE]}


def dist(readers, board):
    """The first book with the position: {move: weight}, else {}."""
    for reader in readers:
        moves = {}
        for e in reader.find_all(board):
            moves[e.move] = moves.get(e.move, 0) + e.weight
        if moves:
            return moves
    return {}


def pick(moves, u):
    """A move at random by weight, u in [0, 1)."""
    items = sorted(moves.items(), key=lambda kv: kv[0].uci())
    x = u * sum(w for _, w in items)
    for move, w in items:
        x -= w
        if x < 0:
            return move
    return items[-1][0]


# ----------------------------------------------------------------------------------------------------------- pairs

def make_pairs(out, want, walks_max, seed):
    folder = os.path.dirname(os.path.abspath(out))
    os.makedirs(folder, exist_ok=True)
    ours_copy = os.path.join(folder, "ours.bin")
    if not os.path.exists(ours_copy):
        shutil.copyfile(OURS, ours_copy)
    # The books may differ only where a move was vetoed.
    human, vetted = entries_by_key(HUMAN), entries_by_key(VETTED)
    differ = {k for k in human.keys() | vetted.keys() if human.get(k) != vetted.get(k)}
    listed = {chess.polyglot.zobrist_hash(chess.Board(r["fen"])) for r in csv.DictReader(open(VETOES, encoding="utf-8"))}
    stray = differ - listed
    print(f"the books differ in {len(differ)} positions, {len(differ & listed)} of them in the veto list "
          f"({len(listed)} listed); {len(human)} positions in lichess-2200.bin")
    if stray:
        raise SystemExit(f"{len(stray)} positions differ outside the veto list: the vetted copy is stale "
                         "(vet-c-book.py rebuilds it)")
    books = chains(ours_copy)
    readers = {b: chess.polyglot.open_reader(b) for b in dict.fromkeys(b for c in books.values() for b in c)}
    side = {name: [readers[b] for b in c] for name, c in books.items()}
    rng = random.Random(seed)
    pairs, walks, ended = [], 0, {"both books": 0, "move 12": 0}
    t0 = time.time()
    while len(pairs) < want and walks < walks_max:
        walks += 1
        board = chess.Board()
        while True:
            if board.ply() >= 2 * BOOK_MOVES:
                ended["move 12"] += 1
                break
            if board.turn == chess.WHITE:
                moves = dist(side["white"], board)
                if not moves:
                    ended["both books"] += 1
                    break
                board.push(pick(moves, rng.random()))
                continue
            ph = dist(side["human"], board)
            if chess.polyglot.zobrist_hash(board) not in differ:
                if not ph:
                    ended["both books"] += 1
                    break
                board.push(pick(ph, rng.random()))
                continue
            pv = dist(side["vetted"], board)
            assert ph and pv, board.fen()
            sh, sv = sum(ph.values()), sum(pv.values())
            mh = pick(ph, rng.random())
            u = rng.random()
            if pv.get(mh, 0) and u < min(1.0, (pv[mh] / sv) / (ph[mh] / sh)):
                board.push(mh)
                continue
            rest = {m: pv[m] / sv - ph.get(m, 0) / sh for m in pv if pv[m] / sv > ph.get(m, 0) / sh}
            mv = pick(rest, rng.random())
            pairs.append({"pair": len(pairs), "walk": walks, "moves": [m.uci() for m in board.move_stack],
                          "fen": board.fen(), "key": f"{chess.polyglot.zobrist_hash(board):016x}",
                          "move_no": board.fullmove_number, "human": mh.uci(), "vetted": mv.uci(),
                          "human_san": board.san(mh), "vetted_san": board.san(mv)})
            break
    for r in readers.values():
        r.close()
    record = {"made": time.strftime("%Y-%m-%d %H:%M:%S"), "seed": seed, "walks": walks, "paired": len(pairs),
              "ended": ended, "differ": len(differ),
              "books": {os.path.basename(b): {"bytes": os.path.getsize(b), "sha256": sha256(b)}
                        for b in (HUMAN, VETTED, WIDE, ours_copy)},
              "ours_copy": ours_copy, "pairs": pairs}
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(record, f, indent=1)
    share = len(pairs) / walks
    print(f"{walks} walks in {time.time() - t0:.0f} s: {len(pairs)} pairs ({100 * share:.2f}% of Black games meet a "
          f"veto), {len(set(p['key'] for p in pairs))} positions; others left both books {ended['both books']}, "
          f"reached move 12 {ended['move 12']}")


# ----------------------------------------------------------------------------------------------------------- run

class MEMORYSTATUSEX(ctypes.Structure):
    _fields_ = [("dwLength", ctypes.c_uint32), ("dwMemoryLoad", ctypes.c_uint32), ("ullTotalPhys", ctypes.c_uint64),
                ("ullAvailPhys", ctypes.c_uint64), ("ullTotalPageFile", ctypes.c_uint64),
                ("ullAvailPageFile", ctypes.c_uint64), ("ullTotalVirtual", ctypes.c_uint64),
                ("ullAvailVirtual", ctypes.c_uint64), ("ullAvailExtendedVirtual", ctypes.c_uint64)]


def free_commit_gb():
    status = MEMORYSTATUSEX()
    status.dwLength = ctypes.sizeof(status)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status))
    return status.ullAvailPageFile / 2**30


k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.GetCurrentProcess.restype = ctypes.c_void_p
k32.OpenProcess.restype = ctypes.c_void_p
k32.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
k32.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
k32.SetProcessAffinityMask.argtypes = [ctypes.c_void_p, ctypes.c_size_t]
k32.CloseHandle.argtypes = [ctypes.c_void_p]


def pin(engine, cpu):
    """The engine's process on one logical CPU only."""
    handle = k32.OpenProcess(0x0200 | 0x1000, False, engine.transport.get_pid())   # SET_INFORMATION | QUERY_LIMITED
    if not handle:
        raise OSError(ctypes.get_last_error(), "OpenProcess failed")
    try:
        if not k32.SetProcessAffinityMask(handle, 1 << cpu):
            raise OSError(ctypes.get_last_error(), f"SetProcessAffinityMask to CPU {cpu} failed")
    finally:
        k32.CloseHandle(handle)


class RandomMover:
    """--dry: a legal move at random, at once."""
    def __init__(self, seed):
        self.random = random.Random(seed)

    def play(self, board, limit, game=None):
        return chess.engine.PlayResult(self.random.choice(list(board.legal_moves)), None)

    def quit(self):
        pass


def cpu_list(text):
    cpus = []
    for part in text.split(","):
        a, _, b = part.partition("-")
        cpus.extend(range(int(a), int(b or a) + 1))
    return cpus


def deadline_of(text):
    if not text:
        return None
    hour, minute = map(int, text.split(":"))
    now = time.localtime()
    t = time.mktime((now.tm_year, now.tm_mon, now.tm_mday, hour, minute, 0, 0, 0, -1))
    return t + 24 * 3600 if t <= time.time() else t


def read_games(path):
    games = []
    if os.path.exists(path):
        for line in open(path, encoding="utf-8"):
            try:
                games.append(json.loads(line))
            except ValueError:
                pass   # a line cut short
    return games


def complete(games):
    """{pair: {arm: Black's points}} for the pairs with both games."""
    by = {}
    for g in games:
        by.setdefault(g["pair"], {})[g["arm"]] = g["black"]
    return {p: arms for p, arms in by.items() if all(a in arms for a in ARMS)}


def llr(diffs, mean_score, elo0, elo1):
    """The SPRT's log-likelihood ratio on the pairs' differences (normal approximation): Elo bounds as points at the
    games' mean score."""
    n = len(diffs)
    if n < 2:
        return 0.0
    mean = sum(diffs) / n
    var = sum((d - mean) ** 2 for d in diffs) / n
    if var == 0:
        return 0.0
    m = min(max(mean_score, 0.05), 0.95)
    k = m * (1 - m) * math.log(10) / 400
    mu0, mu1 = elo0 * k, elo1 * k
    return n * (mu1 - mu0) * (2 * mean - mu0 - mu1) / (2 * var)


def sprt_state(games, sprt):
    pairs = complete(games)
    if not pairs:
        return 0, 0.0
    diffs = [a["vetted"] - a["human"] for a in pairs.values()]
    mean_score = sum(a["vetted"] + a["human"] for a in pairs.values()) / (2 * len(pairs))
    return len(pairs), llr(diffs, mean_score, *sprt)


def run(args):
    sprt = tuple(float(x) for x in option(args, "--sprt", "-10,0").split(","))
    concurrency = option(args, "--concurrency", 6, int)
    cpus = cpu_list(option(args, "--cpus", "2-13"))
    base, inc = (float(x) for x in option(args, "--tc", "10+0.1").split("+"))
    deadline = deadline_of(option(args, "--stop-at", ""))
    limit_games = option(args, "--games", 10 ** 9, int)
    evalfile = option(args, "--eval", next((os.path.join(MAIN, "bot", "eval", e) for e in EVALS
                                            if os.path.exists(os.path.join(MAIN, "bot", "eval", e))), ""))
    dry = option(args, "--dry", False, bool)
    exe, pairs_path, out = args
    log_path = os.path.splitext(out)[0] + ".log"
    lock = threading.Lock()

    def log(text):
        line = time.strftime("%Y-%m-%d %H:%M:%S  ") + text
        with lock:
            print(line, flush=True)
            with open(log_path, "a", encoding="utf-8") as f:
                f.write(line + "\n")

    record = json.load(open(pairs_path, encoding="utf-8"))
    ours_copy = record["ours_copy"]
    for path in (HUMAN, VETTED, WIDE, ours_copy):
        if sha256(path) != record["books"][os.path.basename(path)]["sha256"]:
            raise SystemExit(f"{path} has changed since {pairs_path} was made: make the pairs again")
    if len(cpus) < 2 * concurrency and not dry:
        raise SystemExit(f"--cpus needs two CPUs a game: {2 * concurrency} for --concurrency {concurrency}")
    if not dry and not os.path.exists(evalfile):
        raise SystemExit(f"no evaluation {evalfile!r}")
    k32.SetPriorityClass(k32.GetCurrentProcess(), 0x4000)   # below normal; the engines inherit it
    need = 2.0 + 0.15 * 2 * concurrency
    if not dry and free_commit_gb() < need:
        log(f"only {free_commit_gb():.2f} GB of commit free, {need:.2f} wanted (2 GB kept free): not starting")
        sys.exit(2)

    games = read_games(out)
    done = {(g["pair"], g["arm"]) for g in games}
    todo = queue.Queue()
    for p in record["pairs"]:
        for arm in ARMS:
            if (p["pair"], arm) not in done:
                todo.put((p, arm))
    n_pairs, value = sprt_state(games, sprt)
    decided = threading.Event()
    stopping = threading.Event()
    if not SPRT_BOUNDS[0] < value < SPRT_BOUNDS[1]:
        decided.set()
    log(f"{'DRY RUN (random movers), ' if dry else ''}{len(record['pairs'])} pairs, {len(games)} games done already "
        f"({n_pairs} pairs complete, LLR {value:.2f}{', decided' if decided.is_set() else ''}); {todo.qsize()} games to "
        f"go; {concurrency} at a time on CPUs {cpus[:2 * concurrency]}, {base:g}+{inc:g}, SPRT [{sprt[0]:g}, {sprt[1]:g}]"
        + (f", stop at {time.strftime('%H:%M', time.localtime(deadline))}" if deadline else "")
        + ("" if dry else f"; {os.path.basename(evalfile)}; {free_commit_gb():.2f} GB commit free"))

    books = chains(ours_copy)
    opened = []
    counts = {"started": 0, "games": 0}

    def engines_for(slot):
        if dry:
            return RandomMover(f"w{slot}"), RandomMover(f"b{slot}")
        pair = []
        for cpu in cpus[2 * slot:2 * slot + 2]:
            e = chess.engine.SimpleEngine.popen_uci(exe)
            e.configure({"EvalFile": evalfile, "Hash": 64, "Threads": 1, "Move Overhead": 30})
            pin(e, cpu)
            with lock:
                opened.append(e)
            pair.append(e)
        return tuple(pair)

    def play(p, arm, white, black, readers):
        rng = random.Random(f"{record['seed']}-{p['pair']}-{arm}")
        board = chess.Board()
        clock = {chess.WHITE: base, chess.BLACK: base}
        for uci in p["moves"] + [p[arm]]:
            clock[board.turn] += inc
            board.push_uci(uci)
        book = {chess.WHITE: 0, chess.BLACK: 1}
        game_id, termination = object(), None
        while not board.is_game_over(claim_draw=True) and board.ply() < 600:
            move = None
            if board.ply() < 2 * BOOK_MOVES:
                moves = dist(readers["white" if board.turn == chess.WHITE else arm], board)
                if moves:
                    move = pick(moves, rng.random())
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
            points = 0.0 if board.turn == chess.BLACK else 1.0
        else:
            outcome = board.outcome(claim_draw=True)
            points = 0.5 if outcome is None or outcome.winner is None else 1.0 if outcome.winner == chess.BLACK else 0.0
            termination = outcome.termination.name.lower() if outcome else "move limit"
        start = len(p["moves"])
        return {"pair": p["pair"], "arm": arm, "black": points, "termination": termination, "plies": board.ply(),
                "first": p[f"{arm}_san"], "book_white": book[chess.WHITE], "book_black": book[chess.BLACK],
                "clock_white_end": round(clock[chess.WHITE], 2), "clock_black_end": round(clock[chess.BLACK], 2),
                "tc": f"{base:g}+{inc:g}", "eval": "random" if dry else os.path.basename(evalfile),
                "when": time.strftime("%Y-%m-%d %H:%M:%S"),
                "moves": " ".join(m.uci() for m in board.move_stack[start:])}

    def work(slot):
        readers = {name: [chess.polyglot.open_reader(b) for b in c] for name, c in books.items()}
        white = black = None
        retries = {}
        try:
            while not (stopping.is_set() or decided.is_set()):
                if deadline and time.time() > deadline - 90:
                    break
                with lock:
                    if counts["started"] >= limit_games:
                        break
                    try:
                        p, arm = todo.get_nowait()
                    except queue.Empty:
                        break
                    counts["started"] += 1
                try:
                    if white is None:
                        white, black = engines_for(slot)
                    row = play(p, arm, white, black, readers)
                except (chess.engine.EngineError, chess.engine.EngineTerminatedError, OSError) as error:
                    for e in (white, black):
                        try:
                            e.quit()
                        except Exception:
                            pass
                    white = black = None
                    if stopping.is_set():
                        return
                    key = (p["pair"], arm)
                    retries[key] = retries.get(key, 0) + 1
                    log(f"[{slot}] pair {p['pair']} {arm}: {error!r}; new engines"
                        + ("; the game goes back in the queue" if retries[key] <= 2 else "; given up"))
                    if retries[key] <= 2:
                        todo.put((p, arm))
                    continue
                if row is None:
                    return
                with lock:
                    with open(out, "a", encoding="utf-8", newline="\n") as f:
                        f.write(json.dumps(row) + "\n")
                    games.append(row)
                    counts["games"] += 1
                    n, value = sprt_state(games, sprt)
                    if not SPRT_BOUNDS[0] < value < SPRT_BOUNDS[1]:
                        decided.set()
                log(f"[{slot}] pair {p['pair']} {arm:6s} {p[arm + '_san']:6s} Black {row['black']} ({row['termination']}, "
                    f"{row['plies']} plies); {n} pairs, LLR {value:.2f}" + (" (decided)" if decided.is_set() else ""))
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
    n, value = sprt_state(games, sprt)
    why = ("the stop time" if stopping.is_set() or (deadline and time.time() > deadline - 90) else
           "the SPRT decided" if decided.is_set() else "--games reached" if counts["started"] >= limit_games else
           "every pair played")
    log(f"stopped ({why}): {counts['games']} games this run, {len(games)} in all, {n} pairs complete, LLR {value:.2f}")
    summary(pairs_path, out, sprt)
    if why == "the stop time":
        sys.exit(3)


# ----------------------------------------------------------------------------------------------------------- summary

def elo(score):
    score = min(max(score, 0.001), 0.999)
    return -400 * math.log10(1 / score - 1)


def summary(pairs_path, out, sprt=(-10.0, 0.0)):
    record = json.load(open(pairs_path, encoding="utf-8"))
    games = read_games(out)
    pairs = complete(games)
    share = record["paired"] / record["walks"]
    print(f"\n{len(games)} games in {out}; {len(pairs)} pairs complete of {len(record['pairs'])}. A pair stands for "
          f"{100 * share:.2f}% of Black games ({record['paired']} of {record['walks']} walks met a veto).")
    if not pairs:
        return
    rows = [g for g in games if g["pair"] in pairs]
    print("\n| arm (our book as Black) | games | +W -L =D for Black | Black's score | lost on time (Black / White) "
          "| plies | book moves (Black / White) |")
    print("|---|---:|---|---:|---|---:|---|")
    for arm in ARMS:
        g = [r for r in rows if r["arm"] == arm]
        w = sum(r["black"] == 1 for r in g); d = sum(r["black"] == 0.5 for r in g); l = len(g) - w - d
        flag_b = sum(r["termination"] == "time" and r["black"] == 0 for r in g)
        flag_w = sum(r["termination"] == "time" and r["black"] == 1 for r in g)
        print(f"| {arm} | {len(g)} | +{w} -{l} ={d} | {100 * sum(r['black'] for r in g) / len(g):.1f}% | "
              f"{flag_b} / {flag_w} | {sum(r['plies'] for r in g) / len(g):.0f} | "
              f"{sum(r['book_black'] for r in g) / len(g):.1f} / {sum(r['book_white'] for r in g) / len(g):.1f} |")
    diffs = [a["vetted"] - a["human"] for a in pairs.values()]
    n = len(diffs)
    mean = sum(diffs) / n
    sd = math.sqrt(sum((x - mean) ** 2 for x in diffs) / max(1, n - 1))
    half = 1.96 * sd / math.sqrt(n)
    m = sum(a["vetted"] + a["human"] for a in pairs.values()) / (2 * n)
    as_elo = lambda d: elo(m + d / 2) - elo(m - d / 2)
    value = llr(diffs, m, *sprt)
    verdict = ("H1 accepted (no worse)" if value >= SPRT_BOUNDS[1] else "H0 accepted (worse)" if value <= SPRT_BOUNDS[0]
               else "undecided")
    pent = {x: sum(d == x for d in diffs) for x in (-1, -0.5, 0, 0.5, 1)}
    print(f"\nVetted less human, a pair at a time: {mean:+.3f} points a game (+-{half:.3f}), "
          f"**{as_elo(mean):+.0f} Elo [{as_elo(mean - half):+.0f}, {as_elo(mean + half):+.0f}] on the games where the "
          f"books differ**; SPRT [{sprt[0]:g}, {sprt[1]:g}] LLR {value:.2f} ({SPRT_BOUNDS[0]:.2f}, "
          f"{SPRT_BOUNDS[1]:.2f}): {verdict}.")
    print("Pairs by the difference (vetted less human): " + ", ".join(f"{x:+g}: {c}" for x, c in pent.items()) + ".")
    print(f"Over all Black games that's {share * as_elo(mean):+.1f} Elo [{share * as_elo(mean - half):+.1f}, "
          f"{share * as_elo(mean + half):+.1f}], and over all games (half as Black) half that.")
    by_pos = {}
    for p in record["pairs"]:
        if p["pair"] in pairs:
            by_pos.setdefault(p["key"], []).append(p)
    print(f"\n{len(by_pos)} positions. The most played:\n")
    print("| move | position (moves from the start) | human book's move -> vetted | pairs | vetted less human |")
    print("|---:|---|---|---:|---:|")
    for key, ps in sorted(by_pos.items(), key=lambda kv: -len(kv[1]))[:15]:
        board = chess.Board()
        line = board.variation_san([chess.Move.from_uci(u) for u in ps[0]["moves"]])
        swaps = {}
        for p in ps:
            swaps[f"{p['human_san']} -> {p['vetted_san']}"] = swaps.get(f"{p['human_san']} -> {p['vetted_san']}", 0) + 1
        d = [pairs[p["pair"]]["vetted"] - pairs[p["pair"]]["human"] for p in ps]
        print(f"| {ps[0]['move_no']} | {line} | {', '.join(f'{s} ({c})' for s, c in sorted(swaps.items(), key=lambda kv: -kv[1])[:3])} "
              f"| {len(ps)} | {sum(d) / len(d):+.2f} |")


def main():
    args = sys.argv[1:]
    if not args or args[0] not in ("pairs", "run", "summary"):
        print(__doc__)
        return
    mode, args = args[0], args[1:]
    if mode == "pairs":
        want, walks, seed = option(args, "--pairs", 1500, int), option(args, "--walks", 400000, int), \
            option(args, "--seed", 1, int)
        make_pairs(args[0], want, walks, seed)
    elif mode == "run":
        run(args)
    else:
        sprt = tuple(float(x) for x in option(args, "--sprt", "-10,0").split(","))
        summary(args[0], args[1], sprt)


if __name__ == "__main__":
    main()
