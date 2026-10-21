"""A test for scripts/learner/relearn.py (option (c)), on a temporary bot folder with this tree's bot/learn.py.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/learner/test_relearn.py

Five positions in ours.jsonl and a re-score file: A loses 80 cp (re-learnt), B 20 (kept), C 60 but the learner has
since moved on there (skipped), D exactly 50 (re-learnt), E 70 with castling as the net's move (re-learnt; ours.bin
must read back e1g1). Checks: a dry run changes nothing; --apply appends 3 rows, writes the record, and ours.bin
answers each position with its latest move; a second --apply adds nothing; --min-loss 100 picks none. No engine.
"""
import csv, json, os, shutil, subprocess, sys, tempfile
import chess, chess.polyglot

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
RELEARN = os.path.join(HERE, "relearn.py")

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


def run(bot, *args):
    r = subprocess.run([sys.executable, "-I", RELEARN, bot, os.path.join(bot, "rescore.csv"), *args],
                       capture_output=True, text=True)
    if r.returncode:
        print(r.stdout, r.stderr)
    return r


with tempfile.TemporaryDirectory() as root:
    bot = os.path.join(root, "bot")
    os.makedirs(os.path.join(bot, "books"))
    shutil.copy(os.path.join(REPO, "bot", "learn.py"), bot)
    learnt = os.path.join(bot, "books", "ours.jsonl")
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
    before = open(learnt, "rb").read()

    r = run(bot)
    check("dry run: 3 to re-learn, 1 skipped", r.returncode == 0 and "3 to re-learn" in r.stdout
          and "1 of those already changed" in r.stdout, r.stdout.splitlines()[0] if r.stdout else r.stderr[-200:])
    check("dry run: ours.jsonl unchanged, no ours.bin", open(learnt, "rb").read() == before
          and not os.path.exists(os.path.join(bot, "books", "ours.bin")))

    r = run(bot, "--apply")
    lines = [json.loads(l) for l in open(learnt, encoding="utf-8") if l.strip()]
    check("apply: 3 rows appended", r.returncode == 0 and len(lines) == 9, f"{len(lines)} lines")
    records = [f for f in os.listdir(os.path.join(bot, "books")) if f.startswith("ours-relearnt-")]
    check("apply: the record holds the 3 rows", len(records) == 1 and
          sum(1 for l in open(os.path.join(bot, "books", records[0]), encoding="utf-8") if l.strip()) == 3)
    latest = {}
    for l in lines:
        latest[l["key"]] = l
    want = {"A": "c7c5", "B": "d7d5", "C": "c7c5", "D": "g8f6", "E": "e1g1"}
    for name, (fen, move, best, loss) in POSITIONS.items():
        board = chess.Board(fen)
        l = latest[chess.polyglot.zobrist_hash(board)]
        with chess.polyglot.open_reader(os.path.join(bot, "books", "ours.bin")) as reader:
            book = [e.move.uci() for e in reader.find_all(board)]
        check(f"apply: {name} plays {want[name]}", l["move"] == want[name] and book == [want[name]],
              f"ours.jsonl {l['move']} ({l['why']}), ours.bin {book}")
    a = latest[chess.polyglot.zobrist_hash(chess.Board(POSITIONS["A"][0]))]
    check("apply: A's row keeps the old move and its loss", a.get("was") == "e7e5" and a.get("was_loss") == 80
          and a.get("eval") == "h1024-3m" and a.get("why") == "re-learnt", json.dumps(a)[:160])

    r = run(bot, "--apply")
    check("apply again: nothing added", r.returncode == 0 and "nothing to apply" in r.stdout
          and sum(1 for l in open(learnt, encoding="utf-8") if l.strip()) == 9)

    r = run(bot, "--min-loss", "100")
    check("--min-loss 100: none", r.returncode == 0 and "0 lose 100 cp or more" in r.stdout)

print("\nall passed" if not failures else f"\n{len(failures)} FAILED: {', '.join(failures)}")
sys.exit(1 if failures else 0)
