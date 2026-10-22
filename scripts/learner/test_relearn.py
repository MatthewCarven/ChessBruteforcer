"""A test for scripts/learner/relearn.py (option (c)), on a temporary bot folder with this tree's bot/learn.py.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/learner/test_relearn.py

Five positions in ours.jsonl and a re-score file: A loses 80 cp (re-learnt), B 20 (kept), C 60 but the learner has
since moved on there (skipped), D exactly 50 (re-learnt), E 70 with castling as the net's move (re-learnt; ours.bin
must read back e1g1). Checks: a dry run changes nothing; --apply refuses while a learner runs, or when it can't tell,
and changes nothing then; --apply with none running backs up ours.jsonl and ours.bin, appends 3 rows, and ours.bin
answers each position with its latest move; a second --apply adds nothing; --min-loss 100 picks none. And the real
learner check, against a stand-in process whose command line has bot\\learn.py in it: found while it runs, gone
after. relearn.main runs in this process, so the learner check is swapped for the --apply cases. No engine.
"""
import contextlib, csv, importlib.util, io, json, os, shutil, subprocess, sys, tempfile, time
import chess, chess.polyglot

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
spec = importlib.util.spec_from_file_location("relearn", os.path.join(HERE, "relearn.py"))
relearn = importlib.util.module_from_spec(spec)
spec.loader.exec_module(relearn)

POSITIONS = {   # name: (fen, learnt, net's best, loss)
    "A": ("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1", "e7e5", "c7c5", 80),
    "B": ("rnbqkbnr/pppppppp/8/8/3P4/8/PPP1PPPP/RNBQKBNR b KQkq - 0 1", "d7d5", "g8f6", 20),
    "C": ("rnbqkbnr/pppppppp/8/8/2P5/8/PP1PPPPP/RNBQKBNR b KQkq - 0 1", "e7e5", "g8f6", 60),
    "D": ("rnbqkbnr/pppppppp/8/8/8/5N2/PPPPPPPP/RNBQKB1R b KQkq - 1 1", "d7d5", "g8f6", 50),
    "E": ("r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4", "d2d3", "e1g1", 70),
}
failures = []


def check(name, ok, detail=""):
    print(f"{'ok  ' if ok else 'FAIL'} {name}" + (f": {detail}" if detail else ""))
    if not ok:
        failures.append(name)


def row(fen, move, why="no book"):
    board = chess.Board(fen)
    m = chess.Move.from_uci(move)
    to = m.to_square
    if board.is_castling(m):
        to = chess.square(7 if chess.square_file(m.to_square) == 6 else 0, chess.square_rank(m.from_square))
    polyglot = (chess.square_file(to) | chess.square_rank(to) << 3 | chess.square_file(m.from_square) << 6
                | chess.square_rank(m.from_square) << 9)
    return {"why": why, "best_cp": 10, "key": chess.polyglot.zobrist_hash(board), "polyglot": polyglot, "move": move,
            "san": board.san(m), "fen": fen, "game": "https://lichess.org/test", "played": move, "depth": 14,
            "when": "2026-10-14 12:00"}


def run(bot, *args, running=lambda: []):
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        code = relearn.main([bot, os.path.join(bot, "rescore.csv"), *args], running=running)
    return code, out.getvalue()


def lines_of(path):
    return [json.loads(l) for l in open(path, encoding="utf-8") if l.strip()]


with tempfile.TemporaryDirectory() as root:
    bot = os.path.join(root, "bot")
    books = os.path.join(bot, "books")
    os.makedirs(books)
    shutil.copy(os.path.join(REPO, "bot", "learn.py"), bot)
    learnt = os.path.join(books, "ours.jsonl")
    with open(learnt, "w", encoding="utf-8") as f:
        for name, (fen, move, best, loss) in POSITIONS.items():
            f.write(json.dumps(row(fen, move)) + "\n")
        f.write(json.dumps(row(POSITIONS["C"][0], "c7c5")) + "\n")   # C: the learner has moved on since
    with open(os.path.join(bot, "rescore.csv"), "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["arm", "key", "fen", "learnt", "why", "learnt_depth", "learnt_cp", "best", "best_cp",
                    "learnt_cp_now", "loss", "depth", "nodes", "seconds"])
        for fen, move, best, loss in POSITIONS.values():
            w.writerow(["net", chess.polyglot.zobrist_hash(chess.Board(fen)), fen, move, "no book", 14, 10, best, 30,
                        30 - loss, loss, 14, 1000, 1.0])
    relearn.load_learn(bot).write_book()   # the book as the learner left it
    before = {p: open(os.path.join(books, p), "rb").read() for p in ("ours.jsonl", "ours.bin")}

    def unchanged():
        return sorted(os.listdir(books)) == ["ours.bin", "ours.jsonl"] and all(
            open(os.path.join(books, p), "rb").read() == data for p, data in before.items())

    code, out = run(bot)
    check("dry run: 3 to re-learn, 1 skipped", code == 0 and "3 to re-learn" in out
          and "1 of those already changed" in out, out.splitlines()[0])
    check("dry run: nothing changed", unchanged())

    code, out = run(bot, "--apply", running=lambda: [(4242, r"python.exe -I C:\x\bot\learn.py")])
    check("apply, a learner running: refused (exit 2), nothing changed", code == 2 and "refused" in out
          and unchanged(), f"exit {code}")
    code, out = run(bot, "--apply", running=lambda: None)
    check("apply, can't tell: refused (exit 3), nothing changed", code == 3 and "refused" in out and unchanged(),
          f"exit {code}")

    code, out = run(bot, "--apply")
    rows = lines_of(learnt)
    check("apply: 3 rows added", code == 0 and len(rows) == 9 and "added 3 rows" in out, f"exit {code}, {len(rows)}")
    baks = sorted(f for f in os.listdir(books) if ".bak-" in f)
    check("apply: ours.jsonl and ours.bin backed up as they were", len(baks) == 2 and all(
        open(os.path.join(books, b), "rb").read() == before[b.split(".bak-")[0]] for b in baks), str(baks))
    latest = {}
    for l in rows:
        latest[l["key"]] = l
    want = {"A": "c7c5", "B": "d7d5", "C": "c7c5", "D": "g8f6", "E": "e1g1"}
    for name, (fen, move, best, loss) in POSITIONS.items():
        board = chess.Board(fen)
        l = latest[chess.polyglot.zobrist_hash(board)]
        with chess.polyglot.open_reader(os.path.join(books, "ours.bin")) as reader:
            book = [e.move.uci() for e in reader.find_all(board)]
        check(f"apply: {name} plays {want[name]}", l["move"] == want[name] and book == [want[name]],
              f"ours.jsonl {l['move']} ({l['why']}), ours.bin {book}")
    a = latest[chess.polyglot.zobrist_hash(chess.Board(POSITIONS["A"][0]))]
    check("apply: A's row keeps the old move and its loss", a.get("was") == "e7e5" and a.get("was_loss") == 80
          and a.get("eval") == "h1024-3m" and a.get("why") == "re-learnt", json.dumps(a)[:120])

    code, out = run(bot, "--apply")
    check("apply again: nothing added", code == 0 and "nothing to apply" in out and len(lines_of(learnt)) == 9)
    code, out = run(bot, "--min-loss", "100")
    check("--min-loss 100: none", code == 0 and "0 lose 100 cp or more" in out)

    # The real check, against a stand-in: a sleeping Python whose command line names a bot\learn.py.
    stand_in = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(120)", os.path.join(bot, "learn.py")])
    try:
        time.sleep(1)
        found = relearn.learner_running()
        check("learner check: finds a running bot\\learn.py", found is not None and stand_in.pid in {p for p, _ in found},
              f"{len(found or [])} found")
        code, out = run(bot, "--apply", "--min-loss", "0", running=relearn.learner_running)
        check("apply with it running: refused", code == 2 and "refused" in out, f"exit {code}")
    finally:
        stand_in.kill()
        stand_in.wait()
    time.sleep(1)
    found = relearn.learner_running()
    check("learner check: gone once it stops", found is not None and stand_in.pid not in {p for p, _ in found})

print("\nall passed" if not failures else f"\n{len(failures)} FAILED: {', '.join(failures)}")
sys.exit(1 if failures else 0)
