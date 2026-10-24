"""BT1 (c), step 2: search the positions of vet-c-positions.csv and score each book move to vet.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/book-tables/vet-c-search.py <engine exe> <out.csv>
        [--depth 16] [--tiers 1,2] [--workers 1] [--sample N] [--confirm <screen.csv> --worse 50] [--eval <file>]
        [--stop-at HH:MM]

For each position: our engine's best move and score at --depth; then each move to vet that isn't the best, scored
by searching the position after it to --depth - 1 and negating (the engine has no MultiPV or searchmoves; as
scripts/learner/rescore.py does). loss = best - move, in centipawns. Every move's loss is kept, so any veto line can
be drawn afterwards; vet-c-book.py draws it at 50.
--confirm <screen.csv>: only the positions with a move --worse (50) or more behind there, and only those moves, at
--depth (18 for the confirm). --sample N: N positions spread evenly over the list (a timing).
One thread, Hash 64, no endgame tables, the bot's net (h1536-3m from the main checkout's bot\\eval), a fresh game
(ucinewgame) per search, below-normal priority (the engines inherit it), --workers engines at once pulling the
positions in priority order. Rows are appended to out.csv as each search ends, and a restart skips what's there.
Run it from a copy of a Release build (not bot\\lichess-bot\\engines). A log goes beside out.csv (.log).
--stop-at HH:MM (the next such time on the clock): the engines are stopped then, the searches they were in the
middle of are dropped (only finished searches are ever written), and it exits with code 3, so that vet-c.cmd doesn't
go on to the confirm. Started again, it carries on. Closing its window keeps the finished searches too.
"""
import csv, ctypes, os, queue, subprocess, sys, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), "ponder-scope"))
from uci import Engine, infos   # noqa: E402


def main_checkout():
    """The checkout whose bot\\eval holds the nets (they're tracked, so a worktree has its own, byte for byte)."""
    repo = os.path.dirname(os.path.dirname(HERE))
    if os.path.isdir(os.path.join(repo, "bot", "eval")):
        return repo
    common = subprocess.run(["git", "-C", repo, "rev-parse", "--git-common-dir"], capture_output=True, text=True).stdout.strip()
    return os.path.dirname(os.path.abspath(os.path.join(repo, common)))


def option(name, default, kind=int):
    return kind(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default


EXE, OUT = sys.argv[1], sys.argv[2]
DEPTH, WORKERS, SAMPLE, WORSE = option("--depth", 16), option("--workers", 1), option("--sample", 0), option("--worse", 50)
TIERS = {int(t) for t in option("--tiers", "1,2", str).split(",")}
CONFIRM = option("--confirm", None, str)
EVALFILE = option("--eval", os.path.join(main_checkout(), "bot", "eval", "h1536-3m.nnue"), str)
POSITIONS = option("--positions", os.path.join(HERE, "results", "vet-c-positions.csv"), str)
PHASE = "confirm" if CONFIRM else "screen"
LOG = os.path.splitext(OUT)[0] + ".log"
DEADLINE = None
if option("--stop-at", None, str):
    hour, minute = map(int, option("--stop-at", None, str).split(":"))
    now = time.localtime()
    DEADLINE = time.mktime((now.tm_year, now.tm_mon, now.tm_mday, hour, minute, 0, 0, 0, -1))
    if DEADLINE <= time.time():
        DEADLINE += 24 * 3600
FIELDS = ["phase", "kind", "rank", "tier", "move_no", "key", "fen", "move", "share", "played", "best", "best_cp",
          "move_cp", "loss", "depth", "reached", "nodes", "seconds", "worker", "when"]
lock = threading.Lock()


def log(text):
    line = time.strftime("%Y-%m-%d %H:%M:%S  ") + text
    with lock:
        print(line, flush=True)
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(line + "\n")


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


k32 = ctypes.windll.kernel32
k32.GetCurrentProcess.restype = ctypes.c_void_p
k32.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
k32.SetPriorityClass(k32.GetCurrentProcess(), 0x4000)   # below normal; the engines inherit it

need = 2.0 + 0.15 * WORKERS
if free_commit_gb() < need:
    log(f"only {free_commit_gb():.2f} GB of commit free, {need:.2f} wanted (2 GB kept free): not starting")
    sys.exit(2)

positions = [r for r in csv.DictReader(open(POSITIONS, encoding="utf-8")) if int(r["tier"]) in TIERS]
for r in positions:
    total = sum(int(e.split(":")[1]) for e in r["book"].split())
    r["shares"] = {e.split(":")[0]: int(e.split(":")[1]) / total for e in r["book"].split()}
    r["plays"] = {e.split(":")[0]: int(e.split(":")[1]) for e in r["played"].split()}
    r["moves"] = r["vet"].split()
if CONFIRM:
    flagged = {}
    for row in csv.DictReader(open(CONFIRM, encoding="utf-8")):
        if row.get("kind") == "move" and row.get("loss") not in (None, "") and int(row["loss"]) >= WORSE:
            flagged.setdefault(row["key"], []).append(row["move"])
    positions = [dict(r, moves=flagged[r["key"]]) for r in positions if r["key"] in flagged]
if SAMPLE:
    positions = [positions[i * len(positions) // SAMPLE] for i in range(min(SAMPLE, len(positions)))]

best_done, move_done = {}, set()
if os.path.exists(OUT):
    for row in csv.DictReader(open(OUT, encoding="utf-8")):
        try:
            if int(row["depth"]) != DEPTH:
                continue
            if row["kind"] == "best":
                best_done[row["key"]] = (row["best"], int(row["best_cp"]))
            elif row["kind"] == "move":
                int(row["loss"])
                move_done.add((row["key"], row["move"]))
        except (KeyError, TypeError, ValueError):
            continue   # a line cut short by a stop
new_file = not os.path.exists(OUT)
out = open(OUT, "a", newline="", encoding="utf-8")
writer = csv.DictWriter(out, fieldnames=FIELDS)
if new_file:
    writer.writeheader()
    out.flush()


def write(row):
    with lock:
        writer.writerow(row)
        out.flush()


def centipawns(score):
    kind, value = score.split()
    value = int(value)
    if kind == "cp":
        return value
    return 10000 - value if value > 0 else -10000 - value


class Stopped(Exception):
    pass


stopping, finished = threading.Event(), threading.Event()
engines = {}   # worker: its engine, for the stop time


def search(engine, fen, moves, depth):
    """(best move, score for the side to move, depth reached, nodes, seconds)."""
    if stopping.is_set():
        raise Stopped()
    engine.send("ucinewgame")
    engine.ready()
    engine.send(f"position fen {fen}" + (f" moves {' '.join(moves)}" if moves else ""))
    t0 = time.perf_counter()
    engine.send(f"go depth {depth}")
    lines = engine.wait("bestmove", timeout=6 * 3600)
    seconds = time.perf_counter() - t0
    full = [i for i in infos(lines) if not i["partial"]]
    last = full[-1]
    best = lines[-1][1].split()[1]
    return best, centipawns(last["score"]), last["depth"], last["nodes"], seconds


jobs = queue.Queue()
for r in positions:
    jobs.put(r)
counts = {"searches": 0, "positions": 0}
log(f"{PHASE} at depth {DEPTH}: {len(positions)} positions (tiers {sorted(TIERS)}), "
    f"{sum(len(r['moves']) for r in positions)} moves; {len(best_done)} positions and {len(move_done)} moves done "
    f"already; {WORKERS} engines, {EVALFILE}; {free_commit_gb():.2f} GB commit free")


def work(n):
    engine = None
    while not stopping.is_set():
        try:
            r = jobs.get_nowait()
        except queue.Empty:
            break
        for attempt in (1, 2):
            try:
                if engine is None:
                    engine = Engine(EXE, {"Threads": 1, "Hash": 64, "EvalFile": EVALFILE})
                    with lock:
                        engines[n] = engine
                base = {"phase": PHASE, "rank": r["rank"], "tier": r["tier"], "move_no": r["move_no"], "key": r["key"],
                        "fen": r["fen"], "depth": DEPTH, "worker": n}
                if r["key"] in best_done:
                    best, best_cp = best_done[r["key"]]
                else:
                    best, best_cp, reached, nodes, secs = search(engine, r["fen"], [], DEPTH)
                    write(dict(base, kind="best", best=best, best_cp=best_cp, reached=reached, nodes=nodes,
                               seconds=f"{secs:.1f}", when=time.strftime("%Y-%m-%d %H:%M:%S")))
                    with lock:
                        best_done[r["key"]] = (best, best_cp)
                        counts["searches"] += 1
                    log(f"[{n}] #{r['rank']} t{r['tier']} m{r['move_no']} best {best} {best_cp:+d} d{reached} "
                        f"{nodes} nodes {secs:.1f} s")
                for move in r["moves"]:
                    if (r["key"], move) in move_done:
                        continue
                    row = dict(base, kind="move", move=move, share=f"{r['shares'].get(move, 0.0):.4f}",
                               played=r["plays"].get(move, 0), best=best, best_cp=best_cp)
                    if move == best:
                        row.update(move_cp=best_cp, loss=0, reached="", nodes=0, seconds="0.0")
                    else:
                        _, reply_cp, reached, nodes, secs = search(engine, r["fen"], [move], DEPTH - 1)
                        row.update(move_cp=-reply_cp, loss=best_cp + reply_cp, reached=reached, nodes=nodes,
                                   seconds=f"{secs:.1f}")
                        with lock:
                            counts["searches"] += 1
                        log(f"[{n}] #{r['rank']} {move} {-reply_cp:+d} loss {best_cp + reply_cp} d{reached} "
                            f"{nodes} nodes {secs:.1f} s")
                    row["when"] = time.strftime("%Y-%m-%d %H:%M:%S")
                    write(row)
                    with lock:
                        move_done.add((r["key"], move))
                with lock:
                    counts["positions"] += 1
                break
            except (Stopped, RuntimeError, queue.Empty, OSError, IndexError) as error:
                if engine is not None:
                    engine.quit()
                engine = None
                if stopping.is_set():
                    return   # the stop time: the search in hand is dropped, nothing half-done is written
                log(f"[{n}] #{r['rank']} attempt {attempt}: {error!r}; restarting the engine")
    if engine is not None:
        engine.quit()


def watch():
    """At the stop time, kill the engines mid-search; the workers then leave without writing."""
    while not finished.wait(timeout=5):
        if DEADLINE is not None and time.time() >= DEADLINE:
            stopping.set()
            log(f"the stop time ({time.strftime('%H:%M', time.localtime(DEADLINE))}): stopping the engines; "
                f"the searches they were in are dropped, the finished ones are kept")
            with lock:
                running = list(engines.values())
            for engine in running:
                try:
                    engine.p.kill()
                except OSError:
                    pass
            return


threads = [threading.Thread(target=work, args=(n,)) for n in range(WORKERS)]
for t in threads:
    t.start()
threading.Thread(target=watch, daemon=True).start()
for t in threads:
    t.join()
finished.set()
if stopping.is_set():
    log(f"{PHASE} at depth {DEPTH} stopped at the stop time: {counts['positions']} positions and {counts['searches']} "
        f"searches this run; start it again to carry on")
    out.close()
    sys.exit(3)
log(f"{PHASE} at depth {DEPTH} finished: {counts['positions']} positions and {counts['searches']} searches this run")
out.close()
