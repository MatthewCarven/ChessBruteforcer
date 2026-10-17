"""Is the opening book worth anything? Our engine against Stockfish, half the games with the book and half without.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/book-match.py <results .jsonl>
        [--games 400] [--tc 60+1] [--sf-elo 2200] [--concurrency 3] [--book main.bin,wide.bin]
        [--eval <file>] [--threads 1] [--arms "book,no book"]

--eval is our evaluation (EvalFile): by default the bot's, chosen as bot/make-config.py chooses it (bot/eval/
nnue1.nnue, else eval-sf1.txt); "none" for the built-in numbers. --threads is our search threads (the bot uses
8; with more than one, play one game at a time). --arms book plays only the arm with the book, as the bot does
(2026-10-17: a few games against Stockfish before the bot goes online). The engine doesn't ponder here.

Games alternate between the arms ("book", "no book"), and our colour alternates every two games, so each
arm gets as many Whites as Blacks. With the book, our moves come from it while it has the position, for
the first 12 moves, chosen as lichess-bot chooses (weighted random, from the first book in --book that
has the position: by default the bot's two, lichess-2200.bin then lichess-2200-wide.bin) and played
instantly (the increment still counts); then the engine. Without it, the engine from move 1. Stockfish 16 (WSL, UCI_LimitStrength
at --sf-elo, its weakened play varies the games) never uses a book. Real clocks: a side whose time runs
out loses. Our engine is a copy of the bot's (bot/lichess-bot/engines/bruteforcer) with our tables.

Each game is appended to the results file as it ends, so a run can be stopped and its games still
count; run it again with the same file to add more (it carries on numbering). The summary at the end
(and `--summary` alone) gives each arm's score with a 95% interval, as Elo against Stockfish-at---sf-elo,
and the difference between the arms.
"""
import json, math, os, random, sys, threading, time
from concurrent.futures import ThreadPoolExecutor
import chess, chess.engine, chess.polyglot

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OURS = os.path.join(REPO, "bot", "lichess-bot", "engines", "bruteforcer", "ChessBruteforcer.Engine.exe")
STOCKFISH = ["wsl.exe", "-d", "Ubuntu-24.04", "--", "/usr/games/stockfish"]
BOOK_MOVES = 12
# As the bot has them (bot/make-config.py): the wide book answers only where the main one can't.
BOOKS = [b for b in (os.path.join(REPO, "bot", "books", name) for name in ("lichess-2200.bin", "lichess-2200-wide.bin"))
         if os.path.exists(b)]
# The bot's evaluation, as bot/make-config.py picks it: the network if there is one, else the tuned numbers.
EVALS = [e for e in (os.path.join(REPO, "bot", "eval", name) for name in ("nnue1.nnue", "eval-sf1.txt")) if os.path.exists(e)]


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


def summary(path):
    games = [json.loads(line) for line in open(path, encoding="utf-8") if line.strip()]
    print(f"{len(games)} games in {path}")
    arms = {}
    for arm in ("book", "no book"):
        rows = [g for g in games if g["arm"] == arm]
        if not rows:
            continue
        points = [g["points"] for g in rows]
        n = len(points)
        mean = sum(points) / n
        sd = math.sqrt(sum((p - mean) ** 2 for p in points) / max(1, n - 1))
        half = 1.96 * sd / math.sqrt(n)
        arms[arm] = (n, mean, half)
        w = sum(p == 1 for p in points); d = sum(p == 0.5 for p in points); l = n - w - d
        flags = sum(g["termination"] == "time" and g["points"] == 0 for g in rows)
        book_plies = [g["book_moves"] for g in rows]
        print(f"  {arm:8s} {n:4d} games  +{w} -{l} ={d}  score {100 * mean:5.1f}% (+-{100 * half:.1f})  "
              f"Elo v Stockfish {elo(mean):+5.0f} [{elo(mean - half):+.0f}, {elo(mean + half):+.0f}]  "
              f"lost on time {flags}  book moves a game {sum(book_plies) / n:.1f}")
    if len(arms) == 2:
        (n1, m1, h1), (n2, m2, h2) = arms["book"], arms["no book"]
        diff, half = m1 - m2, math.sqrt(h1 ** 2 + h2 ** 2)
        print(f"  the book's worth: {100 * diff:+.1f} points per 100 games (+-{100 * half:.1f}), about "
              f"{elo(m1) - elo(m2):+.0f} Elo")


def main():
    opts, args = options(sys.argv[1:], {"games": (400, int), "tc": ("60+1", str), "sf-elo": (2200, int),
                                        "concurrency": (3, int), "book": (",".join(BOOKS), str),
                                        "eval": (EVALS[0] if EVALS else "none", str), "threads": (1, int),
                                        "arms": ("book,no book", str), "summary": (False, bool)})
    arms = [a.strip() for a in opts["arms"].split(",") if a.strip()]
    if not arms or any(a not in ("book", "no book") for a in arms):
        raise SystemExit(f"--arms takes book and/or no book, not {opts['arms']!r}")
    path = args[0]
    if opts["summary"]:
        summary(path)
        return
    base, inc = (float(x) for x in opts["tc"].split("+"))
    done = sum(1 for line in open(path, encoding="utf-8") if line.strip()) if os.path.exists(path) else 0
    lock = threading.Lock()
    local = threading.local()
    opened = []   # every engine started, to quit at the end (left running, they keep Python from exiting)

    def engines():
        if not hasattr(local, "ours"):
            local.ours = chess.engine.SimpleEngine.popen_uci(OURS)
            ours_options = {"EndgameTables": os.path.join(REPO, "tables"), "Hash": 256, "Move Overhead": 30,
                            "Threads": opts["threads"]}
            if opts["eval"] != "none":
                ours_options["EvalFile"] = opts["eval"]
            local.ours.configure(ours_options)
            local.sf = chess.engine.SimpleEngine.popen_uci(STOCKFISH)
            local.sf.configure({"UCI_LimitStrength": True, "UCI_Elo": opts["sf-elo"]})
            with lock:
                opened.extend([local.ours, local.sf])
            local.books = [chess.polyglot.open_reader(b) for b in opts["book"].split(",")]
            local.random = random.Random()
        return local.ours, local.sf, local.books

    def play(number):
        ours, sf, books = engines()
        arm = arms[number % len(arms)]
        we_white = (number // len(arms)) % 2 == 0
        board = chess.Board()
        clock = {chess.WHITE: base, chess.BLACK: base}
        book_moves, termination, game_id = 0, None, object()
        while not board.is_game_over(claim_draw=True) and board.ply() < 600:
            ours_to_move = board.turn == (chess.WHITE if we_white else chess.BLACK)
            move = None
            if ours_to_move and arm == "book" and board.ply() < 2 * BOOK_MOVES:
                for book in books:
                    try:
                        move = book.weighted_choice(board, random=local.random).move
                        book_moves += 1
                        break
                    except IndexError:
                        pass
            if move is None:
                limit = chess.engine.Limit(white_clock=max(0.01, clock[chess.WHITE]), black_clock=max(0.01, clock[chess.BLACK]),
                                           white_inc=inc, black_inc=inc)
                started = time.perf_counter()
                result = (ours if ours_to_move else sf).play(board, limit, game=game_id)
                clock[board.turn] -= time.perf_counter() - started
                move = result.move
                if clock[board.turn] < 0:
                    termination = "time"
                    break
            clock[board.turn] += inc
            board.push(move)
        if termination == "time":
            loser_ours = board.turn == (chess.WHITE if we_white else chess.BLACK)
            points = 0.0 if loser_ours else 1.0
        else:
            outcome = board.outcome(claim_draw=True)
            if outcome is None or outcome.winner is None:
                points = 0.5
            else:
                points = 1.0 if outcome.winner == we_white else 0.0
            termination = outcome.termination.name.lower() if outcome else "move limit"
        row = {"game": number, "arm": arm, "we": "white" if we_white else "black", "points": points,
               "termination": termination, "plies": board.ply(), "book_moves": book_moves,
               "our_clock_end": round(clock[chess.WHITE if we_white else chess.BLACK], 1),
               "tc": opts["tc"], "sf_elo": opts["sf-elo"], "eval": os.path.basename(opts["eval"]),
               "threads": opts["threads"], "moves": " ".join(m.uci() for m in board.move_stack)}
        with lock:
            with open(path, "a", encoding="utf-8") as out:
                out.write(json.dumps(row) + "\n")
            print(f"game {number}: {arm:8s} we {row['we']:5s} {points} ({termination}, {board.ply()} plies, "
                  f"{book_moves} book moves)", flush=True)

    start = time.time()
    try:
        with ThreadPoolExecutor(opts["concurrency"]) as pool:
            list(pool.map(play, range(done, done + opts["games"])))
    finally:
        for engine in opened:
            try:
                engine.quit()
            except Exception:
                pass
    print(f"{opts['games']} games in {(time.time() - start) / 60:.0f} min")
    summary(path)


if __name__ == "__main__":
    main()
