"""BT1 (c), step 3: Stockfish's word on our vetoes, on a control sample of moves we passed, and on Black's exits.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/book-tables/vet-c-stockfish.py vetoes|controls|exits
        [--depth 20] [--worse 50] [--n 50]

Stockfish 16 in WSL (wsl.exe -d Ubuntu-24.04 -- /usr/games/stockfish), one thread, Hash 64, --depth 20.
- vetoes: each move results/vet-c-confirm.csv has --worse (50) cp or more behind our best at depth 18. Stockfish's
  best and score, the vetoed move's score (searchmoves) and our best move's: sf_loss = Stockfish's best - the move.
  If Stockfish also dislikes the move, the human line is bad; if only our engine does, our net misjudges it.
- controls: --n (50) moves the screen passed (behind our best, but by less than --worse), drawn at random (seed 1):
  how often Stockfish dislikes a move we kept, so the vetoes' agreement has something to be compared with.
- exits: the network-era games where the bot was Black: the position of its first move out of book, Stockfish's
  score for Black there against the evaluation our engine gave its move in the game ([%eval] in the PGN).
Out: results/vet-c-sf-<mode>.csv, appended, a restart skipping what's done.
"""
import csv, ctypes, io, os, glob, random, subprocess, sys, time
import chess, chess.engine, chess.pgn

HERE = os.path.dirname(os.path.abspath(__file__))
RESULTS = os.path.join(HERE, "results")


def main_checkout():
    """bot\\games isn't in git: from a worktree, the main checkout's."""
    repo = os.path.dirname(os.path.dirname(HERE))
    if os.path.isdir(os.path.join(repo, "bot", "games")):
        return repo
    common = subprocess.run(["git", "-C", repo, "rev-parse", "--git-common-dir"], capture_output=True, text=True).stdout.strip()
    return os.path.dirname(os.path.abspath(os.path.join(repo, common)))


def option(name, default, kind=int):
    return kind(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default


MODE = sys.argv[1]
DEPTH, WORSE, N = option("--depth", 20), option("--worse", 50), option("--n", 50)
OUT = os.path.join(RESULTS, f"vet-c-sf-{MODE}.csv")
k32 = ctypes.windll.kernel32
k32.GetCurrentProcess.restype = ctypes.c_void_p
k32.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
k32.SetPriorityClass(k32.GetCurrentProcess(), 0x4000)


def cp(info, turn):
    return info["score"].pov(turn).score(mate_score=10000)


def rows_of(name):
    return list(csv.DictReader(open(os.path.join(RESULTS, name), encoding="utf-8")))


if MODE in ("vetoes", "controls"):
    if MODE == "vetoes":
        best18 = {r["key"]: r["best"] for r in rows_of("vet-c-confirm.csv") if r["kind"] == "best"}
        todo = [r for r in rows_of("vet-c-confirm.csv") if r["kind"] == "move" and int(r["loss"]) >= WORSE]
        for r in todo:
            r["ours"] = best18[r["key"]]
    else:
        passed = [r for r in rows_of("vet-c-screen.csv") if r["kind"] == "move" and 0 < int(r["loss"]) < WORSE]
        todo = random.Random(1).sample(passed, min(N, len(passed)))
        for r in todo:
            r["ours"] = r["best"]
    jobs = [(r["key"], r["fen"], r["move"], r["ours"], r) for r in todo]
    fields = ["key", "fen", "move", "ours", "loss_ours", "sf_best", "sf_best_cp", "sf_move_cp", "sf_ours_cp",
              "sf_loss", "sf_loss_ours", "depth", "seconds"]
    done_ids = lambda row: (row["key"], row["move"])
else:
    rows = {r["id"]: r for r in csv.DictReader(open(os.path.join(main_checkout(), "bot", "games", "record.csv"),
                                                    encoding="utf-8"))}
    jobs = []
    for path in sorted(glob.glob(os.path.join(main_checkout(), "bot", "games", "*.pgn"))):
        raw = open(path, "rb").read()
        try:
            text = raw.decode("utf-8")
        except UnicodeDecodeError:
            text = raw.decode("cp1252")
        game = chess.pgn.read_game(io.StringIO(text))
        if game is None or game.headers.get("Black") != "matthewcarven":
            continue
        row = rows.get(game.headers.get("GameId"))
        if row is None or not row["net"].startswith(("nnue", "h")):
            continue
        for node in game.mainline():
            # lichess-bot puts the engine's [%eval] at the end of its line, a side variation beside the move played
            # (book moves have none); the first such move of ours is the exit (bookexit.py's first eval: 142 of 142).
            if node.turn() != chess.WHITE:
                continue
            line = next((v for v in node.parent.variations[1:] if v.move == node.move), None)
            if line is not None and line.end().eval() is not None:
                ours = line.end().eval().pov(chess.BLACK).score(mate_score=10000)
                jobs.append((row["id"], node.parent.board().fen(), node.move.uci(), ours,
                             {"family": row["opening"].split(":")[0], "net": row["net"], "ply": node.ply()}))
                break
    fields = ["id", "fen", "played", "ours_cp", "family", "net", "ply", "sf_best", "sf_cp", "depth", "seconds"]
    done_ids = lambda row: row["id"]

done = {done_ids(r) for r in csv.DictReader(open(OUT, encoding="utf-8"))} if os.path.exists(OUT) else set()
new_file = not os.path.exists(OUT)
print(f"{MODE}: {len(jobs)} to do, {len(done)} done already -> {OUT}", flush=True)
engine = chess.engine.SimpleEngine.popen_uci(["wsl.exe", "-d", "Ubuntu-24.04", "--", "/usr/games/stockfish"])
engine.configure({"Threads": 1, "Hash": 64})
with open(OUT, "a", newline="", encoding="utf-8") as f:
    w = csv.DictWriter(f, fieldnames=fields)
    if new_file:
        w.writeheader()
    for i, job in enumerate(jobs):
        t0 = time.time()
        if MODE in ("vetoes", "controls"):
            key, fen, move, ours, r = job
            if (key, move) in done:
                continue
            board = chess.Board(fen)
            info = engine.analyse(board, chess.engine.Limit(depth=DEPTH), game=object())
            best, best_cp = info["pv"][0].uci(), cp(info, board.turn)
            scores = {best: best_cp}
            for m in (move, ours):
                if m not in scores:
                    one = engine.analyse(board, chess.engine.Limit(depth=DEPTH), game=object(),
                                         root_moves=[chess.Move.from_uci(m)])
                    scores[m] = cp(one, board.turn)
            w.writerow({"key": key, "fen": fen, "move": move, "ours": ours, "loss_ours": r["loss"], "sf_best": best,
                        "sf_best_cp": best_cp, "sf_move_cp": scores[move], "sf_ours_cp": scores[ours],
                        "sf_loss": best_cp - scores[move], "sf_loss_ours": best_cp - scores[ours],
                        "depth": info.get("depth"), "seconds": round(time.time() - t0, 1)})
            print(f"{i + 1}/{len(jobs)} {move}: ours {r['loss']} sf {best_cp - scores[move]} ({time.time() - t0:.1f} s)",
                  flush=True)
        else:
            gid, fen, played, ours, extra = job
            if gid in done:
                continue
            board = chess.Board(fen)
            info = engine.analyse(board, chess.engine.Limit(depth=DEPTH), game=object())
            sf = cp(info, chess.BLACK)
            w.writerow(dict(extra, id=gid, fen=fen, played=played, ours_cp=ours, sf_best=info["pv"][0].uci(),
                            sf_cp=sf, depth=info.get("depth"), seconds=round(time.time() - t0, 1)))
            print(f"{i + 1}/{len(jobs)} {gid} {extra['family']}: ours {ours:+d} sf {sf:+d} ({time.time() - t0:.1f} s)",
                  flush=True)
        f.flush()
engine.quit()
