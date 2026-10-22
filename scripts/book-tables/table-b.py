"""BT1 (b): Matthew's 5 half-moves as a book, "all of theirs, our best" (BOOK-TABLES.md, decision BT; 2026-10-23).

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/book-tables/table-b.py <log .jsonl> --engine <exe> --eval <file>
        [--depth 18] [--engines 8] [--plies 5] [--limit N] [--book <out .bin>]
    ... table-b.py <log .jsonl> --summary [--depth 18] [--plies 5] [--book <out .bin>]

Our turns are the positions before half-move --plies that our moves and all of theirs reach: with 5, White's moves
1-3 and Black's moves 1-2. Their turns: every legal reply, less those after which a capture-only search finds a side
down a piece or more (blocked() in prune.py at T = 3, Matthew's "any lost pieces block out the entries"). At our
turns one search to --depth picks our move. If it isn't one of the human book's moves (bot\\books\\lichess-2200.bin,
the main checkout's), the book's two most-played moves are tried in turn, each scored by searching the position after
it one ply less (the engine has no searchmoves; scripts/learner/rescore.py does the same), and the first within 20 cp
of our best is stored instead (BT3 (a)), so the human book carries on after the table. The positions two plies on
follow the move stored, so the walk goes ply by ply: a position's next positions are queued the moment its move is
decided, the lowest ply first, and --engines searches run at once.

One thread, Hash 64, no endgame tables, a fresh game (ucinewgame) per search, so a search's answer doesn't depend on
the order. Below-normal priority, which the engines inherit. Run it from a copy of the engine build (not the bot's).
A new search waits while less than 2 GB of commit is free. Every search is appended to the log as it ends, and a run
started again with the same log skips the searches in it: stop it any time (Ctrl+C, or a file <log>.stop, which lets
the running searches finish). A file <log>.engines holding a number keeps only that many of the --engines busy, read
again every ~30 s (beside a run that mustn't slow: 2, then 4; a lower number lets the running searches finish).
--limit N starts at most N searches this run (a timing trial). Progress goes to the
window and to <log name>.log. Once every position has its move, and with --summary alone (no engine), it writes the
book (16-byte Polyglot entries, one move a position, weight 1; by default the log's name with .bin), reads it back,
and prints the summary (also to <log name>-summary.txt).
"""
import ctypes, heapq, json, os, queue, statistics, struct, sys, threading, time, traceback
import chess, chess.polyglot as pg

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), "ponder-scope"))
import uci  # noqa: E402

# prune.py's capture check, as ourbest.py takes it (the part before its counting run).
_src = open(os.path.join(HERE, "prune.py"), encoding="utf-8").read().split("T = float")[0]
_ns = {"__file__": os.path.join(HERE, "prune.py")}
exec(compile(_src, "prune_head", "exec"), _ns)
blocked, MAIN = _ns["blocked"], _ns["MAIN"]

T = 3              # a piece: prune.py's threshold for "lost"
MARGIN = 20        # cp: a human move this close to our best is stored instead
HUMAN_TRIES = 2    # the human book's most-played moves tried, in order
HASH = 64
MIN_FREE = 2 << 30

args = sys.argv[1:]


def option(name, default, kind=int):
    if name not in args:
        return default
    i = args.index(name)
    value = kind(args[i + 1])
    del args[i:i + 2]
    return value


DEPTH, ENGINES, PLIES = option("--depth", 18), option("--engines", 8), option("--plies", 5)
LIMIT, ENGINE, EVAL = option("--limit", None), option("--engine", None, str), option("--eval", None, str)
BOOK_OUT = option("--book", None, str)
SUMMARY = "--summary" in args
if SUMMARY:
    args.remove("--summary")
if len(args) != 1 or (not SUMMARY and not (ENGINE and EVAL)):
    sys.exit(__doc__)
LOG = os.path.abspath(args[0])
BASE = os.path.splitext(LOG)[0]
BOOK_OUT = BOOK_OUT or BASE + ".bin"
STOP, CAP = LOG + ".stop", LOG + ".engines"

k32 = ctypes.windll.kernel32
k32.GetCurrentProcess.restype = ctypes.c_void_p
k32.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
k32.SetPriorityClass(k32.GetCurrentProcess(), 0x4000)   # below normal; the engines inherit it


class MEMORYSTATUSEX(ctypes.Structure):
    _fields_ = [("dwLength", ctypes.c_uint32), ("dwMemoryLoad", ctypes.c_uint32)] + [
        (name, ctypes.c_uint64) for name in ("ullTotalPhys", "ullAvailPhys", "ullTotalPageFile", "ullAvailPageFile",
                                             "ullTotalVirtual", "ullAvailVirtual", "ullAvailExtendedVirtual")]


def free_commit():
    m = MEMORYSTATUSEX()
    m.dwLength = ctypes.sizeof(m)
    k32.GlobalMemoryStatusEx(ctypes.byref(m))
    return m.ullAvailPageFile


class FILETIME(ctypes.Structure):
    _fields_ = [("lo", ctypes.c_uint32), ("hi", ctypes.c_uint32)]


k32.GetProcessTimes.argtypes = [ctypes.c_void_p] + [ctypes.POINTER(FILETIME)] * 4
k32.GetProcessTimes.restype = ctypes.c_int


def cpu_seconds(popen):
    """The engine process's CPU time (kernel + user): what a search costs on a shared laptop, not its wait for a core."""
    t = [FILETIME() for _ in range(4)]
    if not k32.GetProcessTimes(ctypes.c_void_p(int(popen._handle)), *(ctypes.byref(x) for x in t)):
        return None
    return sum((x.hi << 32 | x.lo) for x in t[2:]) / 1e7


progress = open(BASE + ".log", "a", encoding="utf-8")


def say(text):
    line = f"{time.strftime('%H:%M:%S')} {text}"
    print(line, flush=True)
    progress.write(line + "\n")
    progress.flush()


def last_info(lines):
    """The last info line with a score and a pv: depth, cp for the side to move (mates as +-10000 less the distance)."""
    found = None
    for _, line in lines:
        w = line.split()
        if not w or w[0] != "info" or "score" not in w or "pv" not in w or "depth" not in w:
            continue
        kind, value = w[w.index("score") + 1], int(w[w.index("score") + 2])
        cp = value if kind == "cp" else (10000 - value if value > 0 else -10000 - value)
        found = {"depth": int(w[w.index("depth") + 1]), "cp": cp, "bound": "lowerbound" in w or "upperbound" in w,
                 "nodes": int(w[w.index("nodes") + 1]) if "nodes" in w else None, "pv": w[w.index("pv") + 1:]}
    return found


class Worker(threading.Thread):
    """One engine, one search at a time: jobs from its inbox, records to the results queue."""

    def __init__(self, n, results):
        super().__init__(daemon=True)
        self.n, self.results, self.inbox, self.engine = n, results, queue.Queue(), None

    def run(self):
        while True:
            job = self.inbox.get()
            if job is None:
                if self.engine:
                    self.engine.quit()
                return
            try:
                self.results.put((self, job, self.search(job)))
            except Exception as e:
                self.kill()
                self.results.put((self, job, e))

    def kill(self):
        if self.engine:
            try:
                self.engine.p.kill()
            except Exception:
                pass
        self.engine = None

    def search(self, job):
        if self.engine is None:
            self.engine = uci.Engine(os.path.abspath(ENGINE), {"Threads": 1, "Hash": HASH, "EvalFile": EVAL})
        e = self.engine
        e.send("ucinewgame")
        e.send(f"position fen {job['fen']}")
        e.ready()
        cpu0, t0 = cpu_seconds(e.p), time.time()
        e.send(f"go depth {job['depth']}")
        lines = e.wait("bestmove", timeout=4 * 3600)
        wall, cpu1 = time.time() - t0, cpu_seconds(e.p)
        info = last_info(lines)
        best = lines[-1][1].split()[1]
        return {"key": job["key"], "kind": job["kind"], "move": job["move"], "ply": job["ply"], "fen": job["fen"],
                "table_depth": DEPTH, "depth": job["depth"], "reached": info["depth"], "cp": info["cp"],
                "bound": info["bound"], "bestmove": best, "pv": info["pv"][:10], "nodes": info["nodes"],
                "seconds": round(wall, 1), "cpu": None if cpu0 is None or cpu1 is None else round(cpu1 - cpu0, 1),
                "engine": self.n, "ended": time.strftime("%Y-%m-%d %H:%M:%S")}


def polyglot_move(board, move):
    """A move in Polyglot's 16 bits: castling as the king taking its own rook (bot/learn.py's)."""
    to = move.to_square
    if board.is_castling(move):
        to = chess.square(7 if chess.square_file(move.to_square) == 6 else 0, chess.square_rank(move.from_square))
    promotion = {None: 0, chess.KNIGHT: 1, chess.BISHOP: 2, chess.ROOK: 3, chess.QUEEN: 4}[move.promotion]
    return (chess.square_file(to) | chess.square_rank(to) << 3 | chess.square_file(move.from_square) << 6
            | chess.square_rank(move.from_square) << 9 | promotion << 12)


class Walk:
    """Our positions and their moves, rebuilt from the searches done: a position whose move needs a search queues it."""

    def __init__(self, records):
        self.records = records          # (key, kind, move uci or None) -> record
        self.nodes = {}                 # our positions: key -> node
        self.blocked = set()            # keys of positions after their replies that lose a piece
        self.over = set()               # keys of positions where the game is over (no search, no entry)
        self.heap, self.queued, self.seq = [], set(), 0
        self.book = pg.open_reader(os.path.join(MAIN, "bot", "books", "lichess-2200.bin"))
        self.add(chess.Board(), 0, [])                      # our White positions start at the start
        start = chess.Board()
        for m in list(start.legal_moves):                   # our Black ones after each of White's first moves
            start.push(m)
            self.reply(start, 1, [m.uci()])
            start.pop()

    def human_moves(self, board):
        moves = []
        for e in sorted(self.book.find_all(board), key=lambda e: (-e.weight, e.move.uci())):
            if e.move not in moves:
                moves.append(e.move)
        return moves

    def reply(self, board, ply, line):
        """The position after one of their replies: blocked, over, or one of ours."""
        key = pg.zobrist_hash(board)
        if board.is_game_over():
            self.over.add(key)
        elif blocked(board, T):
            self.blocked.add(key)
        else:
            self.add(board, ply, line)

    def add(self, board, ply, line):
        key = pg.zobrist_hash(board)
        if key in self.nodes:
            return
        self.nodes[key] = {"fen": board.fen(), "ply": ply, "colour": "white" if board.turn else "black", "line": line,
                           "human": [m.uci() for m in self.human_moves(board)], "tried": []}
        self.advance(key)

    def advance(self, key):
        node = self.nodes[key]
        board = chess.Board(node["fen"])
        best = self.records.get((key, "best", None))
        if best is None:
            return self.queue(key, "best", None, board)
        bm = chess.Move.from_uci(best["bestmove"])
        assert bm in board.legal_moves, (node["fen"], best["bestmove"])
        node.update(best=bm.uci(), best_cp=best["cp"], tried=[])
        if not node["human"]:
            return self.decide(key, bm.uci(), "no book", best["cp"])
        if bm.uci() in node["human"]:
            return self.decide(key, bm.uci(), "book", best["cp"])
        for i, m in enumerate(node["human"][:HUMAN_TRIES]):
            after = self.records.get((key, "after", m))
            if after is None:
                return self.queue(key, "after", m, board)
            human_cp = -after["cp"]
            node["tried"].append((m, human_cp))
            if best["cp"] - human_cp <= MARGIN:
                return self.decide(key, m, f"human {i + 1}", human_cp)
        return self.decide(key, bm.uci(), "best", best["cp"])

    def queue(self, key, kind, move, board):
        job_id = (key, kind, move)
        if job_id in self.queued:
            return
        node = self.nodes[key]
        if move is not None:
            board.push(chess.Move.from_uci(move))
            if board.is_game_over():             # a human move that ends the game needs no search
                cp = -10000 if board.is_checkmate() else 0
                self.records[job_id] = {"key": key, "kind": kind, "move": move, "cp": cp, "synthetic": True}
                return self.advance(key)
        self.queued.add(job_id)
        depth = DEPTH if kind == "best" else DEPTH - 1
        heapq.heappush(self.heap, (node["ply"], self.seq, {"key": key, "kind": kind, "move": move, "ply": node["ply"],
                                                            "fen": board.fen(), "depth": depth}))
        self.seq += 1

    def decide(self, key, move, why, cp):
        node = self.nodes[key]
        node.update(move=move, why=why, cp=cp)
        if node["ply"] + 2 >= PLIES:              # our next turn would be past the table
            return
        board = chess.Board(node["fen"])
        board.push(chess.Move.from_uci(move))
        for r in list(board.legal_moves):
            board.push(r)
            self.reply(board, node["ply"] + 2, node["line"] + [move, r.uci()])
            board.pop()

    def done(self, record):
        job_id = (record["key"], record["kind"], record["move"])
        self.records[job_id] = record
        self.queued.discard(job_id)
        self.advance(record["key"])

    def undecided(self):
        return sum(1 for n in self.nodes.values() if "move" not in n)


def load():
    records = {}
    if os.path.exists(LOG):
        for line in open(LOG, encoding="utf-8"):
            if line.strip():
                r = json.loads(line)
                if r.get("table_depth") == DEPTH:
                    records[(r["key"], r["kind"], r["move"])] = r
    return records


def busy_cap():
    """The engines to keep busy: --engines, or fewer while <log>.engines says so."""
    try:
        return max(0, min(ENGINES, int(open(CAP, encoding="utf-8").read().strip())))
    except (OSError, ValueError):
        return ENGINES


def run(walk):
    results = queue.Queue()
    workers = [Worker(n, results) for n in range(ENGINES)]
    for w in workers:
        w.start()
    idle, running, started, failures, waiting_memory = list(workers), 0, 0, 0, False
    say(f"depth {DEPTH}, plies {PLIES}, {ENGINES} engines: {len(walk.records)} searches in the log, "
        f"{len(walk.nodes)} positions so far, {walk.undecided()} without a move, {len(walk.heap)} searches queued")
    out = open(LOG, "a", encoding="utf-8")
    try:
        last_cap = ENGINES
        while True:
            stop = os.path.exists(STOP) or (LIMIT is not None and started >= LIMIT) or failures > 5
            cap = busy_cap()
            if cap != last_cap:
                say(f"engines kept busy: {cap} of {ENGINES} ({running} running now)")
                last_cap = cap
            while walk.heap and idle and running < cap and not stop:
                if free_commit() < MIN_FREE:
                    if not waiting_memory:
                        say(f"waiting: {free_commit() / 2 ** 30:.2f} GB of commit free, under 2")
                    waiting_memory = True
                    break
                waiting_memory = False
                _, _, job = heapq.heappop(walk.heap)
                w = idle.pop()
                w.inbox.put(job)
                started += 1
                running += 1
                stop = (LIMIT is not None and started >= LIMIT)
            if running == 0:
                if walk.heap and not stop:       # only the memory floor or <log>.engines holds it
                    time.sleep(30)
                    continue
                break
            try:
                w, job, record = results.get(timeout=30)
            except queue.Empty:
                continue
            running -= 1
            idle.append(w)
            if isinstance(record, Exception):
                failures += 1
                say(f"engine {w.n} failed on {job['fen']} ({record!r}); queued again")
                walk.seq += 1
                heapq.heappush(walk.heap, (job["ply"], walk.seq, job))
                continue
            out.write(json.dumps(record) + "\n")
            out.flush()
            walk.done(record)
            node = walk.nodes[record["key"]]
            what = f"{record['kind']}" + (f" {record['move']}" if record["move"] else "")
            say(f"[{len(walk.records)} searches, {len(walk.nodes)} positions, {walk.undecided()} open, {running} running, "
                f"{len(walk.heap)} queued] ply {node['ply']} {node['colour']} {' '.join(node['line']) or 'start'}: "
                f"{what} -> {record['bestmove']} {record['cp']:+d} d{record['reached']} {record['seconds']:.0f} s "
                f"(cpu {record['cpu']} s)" + (f"; stored {node['move']} ({node['why']})" if "move" in node else ""))
    finally:
        for w in workers:
            if w.engine and running:     # stopped mid-search (Ctrl+C): those searches are lost, the log isn't
                w.kill()
            w.inbox.put(None)
        for w in workers:
            w.join(timeout=15)
        out.close()
    if failures > 5:
        say("stopped: more than 5 engine failures")
    elif os.path.exists(STOP):
        say(f"stopped by {STOP}")
    elif LIMIT is not None and walk.heap:
        say(f"stopped at --limit {LIMIT}")


def quantiles(xs):
    xs = sorted(xs)
    if not xs:
        return "-"
    q = statistics.quantiles(xs, n=4) if len(xs) > 1 else [xs[0]] * 3
    return f"median {q[1]:+.0f} [{q[0]:+.0f}, {q[2]:+.0f}], {xs[0]:+d} to {xs[-1]:+d}"


def write_book(walk):
    entries = sorted((key, polyglot_move(chess.Board(n["fen"]), chess.Move.from_uci(n["move"])))
                     for key, n in walk.nodes.items() if "move" in n)
    temp = BOOK_OUT + ".tmp"
    with open(temp, "wb") as f:
        for key, move in entries:
            f.write(struct.pack(">QHHI", key, move, 1, 0))
    os.replace(temp, BOOK_OUT)
    agree = 0
    with pg.open_reader(BOOK_OUT) as reader:
        for n in walk.nodes.values():
            found = [e.move.uci() for e in reader.find_all(chess.Board(n["fen"]))]
            agree += found == [n["move"]]
    return len(entries), agree


def summary(walk):
    lines = []
    p = lines.append
    nodes = list(walk.nodes.values())
    p(f"BT1 (b), depth {DEPTH} (human moves at {DEPTH - 1}), plies {PLIES}: {len(nodes)} of our positions, "
      f"{walk.undecided()} still without a move.")
    for colour in ("white", "black"):
        mine = [n for n in nodes if n["colour"] == colour]
        by_ply = {}
        for n in mine:
            by_ply[n["ply"]] = by_ply.get(n["ply"], 0) + 1
        p(f"  {colour}: {len(mine)} ({', '.join(f'ply {k}: {v}' for k, v in sorted(by_ply.items()))})")
    p(f"  their replies blocked (a piece lost): {len(walk.blocked)} positions; games over (no entry): {len(walk.over)}")
    searches = [r for r in walk.records.values() if not r.get("synthetic")]
    for kind in ("best", "after"):
        rs = [r for r in searches if r["kind"] == kind]
        if rs:
            cpu = [r["cpu"] for r in rs if r.get("cpu") is not None]
            p(f"  searches, {kind}: {len(rs)}; wall median {statistics.median(r['seconds'] for r in rs):.0f} s, "
              f"cpu median {statistics.median(cpu) if cpu else 0:.0f} s, cpu total {sum(cpu) / 3600:.1f} h; "
              f"nodes median {statistics.median(r['nodes'] or 0 for r in rs):,.0f}")
    decided = [n for n in nodes if "move" in n]
    if not decided:
        return "\n".join(lines)
    p("")
    p("Our move | positions | share")
    in_book = [n for n in decided if n["human"]]
    whys = {}
    for n in decided:
        whys[n["why"]] = whys.get(n["why"], 0) + 1
    for why, text in (("book", "our best is a human-book move"), ("human 1", "the book's most-played move, within 20 cp"),
                      ("human 2", "the book's second move, within 20 cp"),
                      ("best", "our best, the book's moves tried and further than 20 cp"),
                      ("no book", "our best, the position not in the human book")):
        p(f"  {text} | {whys.get(why, 0)} | {whys.get(why, 0) / len(decided):.0%}")
    p(f"  (in the human book: {len(in_book)}; our stored move is a human-book move in "
      f"{sum(1 for n in in_book if n['move'] in n['human'])} of them)")
    tried = [(n, m, cp) for n in decided for m, cp in n["tried"]]
    if tried:
        losses = [n["best_cp"] - cp for n, m, cp in tried]
        p(f"  human moves tried: {len(tried)}; best - human: {quantiles(losses)}; within 20 cp: "
          f"{sum(1 for x in losses if x <= MARGIN)}")
    p("")
    p("Values (cp, our side, the move stored) by colour and ply:")
    for colour in ("white", "black"):
        for ply in sorted({n["ply"] for n in decided if n["colour"] == colour}):
            cps = [n["cp"] for n in decided if n["colour"] == colour and n["ply"] == ply]
            p(f"  {colour} ply {ply} ({len(cps)}): {quantiles(cps)}")
    p("")
    start = walk.nodes.get(pg.zobrist_hash(chess.Board()))
    if start and "move" in start:
        b = chess.Board()
        p(f"As White, move 1: {b.san(chess.Move.from_uci(start['move']))} ({start['cp']:+d}, {start['why']}; "
          f"human book: {', '.join(b.san(chess.Move.from_uci(m)) for m in start['human'][:5])})")
    p("As Black, move 1, against each of White's first moves:")
    for n in sorted((n for n in decided if n["ply"] == 1), key=lambda n: -len(n["human"])):
        b = chess.Board()
        first = b.san(chess.Move.from_uci(n["line"][0]))
        b.push_uci(n["line"][0])
        p(f"  1.{first} {b.san(chess.Move.from_uci(n['move']))} ({n['cp']:+d}, {n['why']})")
    return "\n".join(lines)


def main():
    walk = Walk(load())
    if not SUMMARY:
        run(walk)
    if walk.undecided() == 0:
        count, agree = write_book(walk)
        say(f"book: {BOOK_OUT}, {count} entries; read back: {agree} of {len(walk.nodes)} positions give their move")
    text = summary(walk)
    print(text)
    if walk.undecided() == 0:
        with open(BASE + "-summary.txt", "w", encoding="utf-8", newline="\n") as f:
            f.write(text + "\n")


if __name__ == "__main__":
    try:
        main()
    except BaseException:
        say("failed:\n" + traceback.format_exc())
        raise
