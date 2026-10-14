"""The bot's own book, learnt from its games (Matthew, 2026-10-14: "background job first").

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I bot/learn.py            (bot\\learn.cmd: its own window)
    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I bot/learn.py --summary  (what it has learnt so far)

Bots knock ours out of the human book with rare moves, mostly by our move 6-8. After every game the bot
finishes (bot/games), this looks at the positions where it was our move, within the book's reach (our first
12 moves, as lichess-bot's max_depth), and:
- where no book had a move, thinks about it for --think seconds (45; the bot had a few) and keeps the best
  move;
- where only the wide book had one (a move 2200+ players chose just 3-9 times), checks it: the best move's
  score against the wide move's (both --check seconds, 20); if the wide move is --worse centipawns (75) or
  more behind, the best move is kept, and being in our book it is played first.
At most --per-game positions a game (4), nearest the book first: those are the likeliest to come again.

The moves go to bot/books/ours.jsonl (one line each: position, move, scores, why) and the book
bot/books/ours.bin is rebuilt from it after each game (to a temporary file, moved over; lichess-bot opens
its books afresh every move, so it is live at once). make-config.py lists it between the main book and the
wide one. It always holds one inert entry (position key 0), since lichess-bot can't open an empty book.

Runs at below-normal priority (its engine inherits it), so the bot's own engine comes first; the engine is
its own copy of the engine build (bot/learn.cmd makes it), with the tuned evaluation and without endgame tables (openings never need them).
Games done are listed in bot/books/ours-done.txt; stop it any time, and it carries on where it was.
"""
import ctypes, glob, io, json, os, struct, sys, time, traceback
import chess, chess.engine, chess.pgn, chess.polyglot

HERE = os.path.dirname(os.path.abspath(__file__))
BOOKS = os.path.join(HERE, "books")
ENGINE = os.path.join(HERE, "lichess-bot", "engines", "learner", "ChessBruteforcer.Engine.exe")   # its own copy (learn.cmd)
EVAL = os.path.join(HERE, "eval", "eval-sf1.txt")
MAIN, WIDE, OURS = (os.path.join(BOOKS, name) for name in ("lichess-2200.bin", "lichess-2200-wide.bin", "ours.bin"))
LEARNT, DONE = os.path.join(BOOKS, "ours.jsonl"), os.path.join(BOOKS, "ours-done.txt")
ME = "matthewcarven"
BOOK_PLIES = 23   # lichess-bot consults its books while the game has at most max_depth * 2 - 1 plies


def option(name, default, kind=float):
    args = sys.argv
    return kind(args[args.index(name) + 1]) if name in args else default


THINK, CHECK, WORSE, PER_GAME = option("--think", 45.0), option("--check", 20.0), option("--worse", 75), option("--per-game", 4, int)


def log(text):
    """To the window and to bot/logs/learn-<date>.log (the window can't be read from elsewhere)."""
    line = time.strftime("%Y-%m-%d %H:%M:%S  ") + text
    print(line, flush=True)
    try:
        os.makedirs(os.path.join(HERE, "logs"), exist_ok=True)
        with open(os.path.join(HERE, "logs", time.strftime("learn-%Y-%m-%d.log")), "a", encoding="utf-8") as out:
            out.write(line + "\n")
    except OSError:
        pass


def studied():
    """The games done, one file name a line (names have spaces in them, so never split on whitespace)."""
    if not os.path.exists(DONE):
        return set()
    return {line.strip() for line in open(DONE, encoding="utf-8") if line.strip()}


def polyglot_move(board, move):
    """A move in Polyglot's 16 bits: castling as the king taking its own rook."""
    to = move.to_square
    if board.is_castling(move):
        to = chess.square(7 if chess.square_file(move.to_square) == 6 else 0, chess.square_rank(move.from_square))
    promotion = {None: 0, chess.KNIGHT: 1, chess.BISHOP: 2, chess.ROOK: 3, chess.QUEEN: 4}[move.promotion]
    return (chess.square_file(to) | chess.square_rank(to) << 3 | chess.square_file(move.from_square) << 6
            | chess.square_rank(move.from_square) << 9 | promotion << 12)


def book_moves(path, board):
    if not os.path.exists(path):
        return []
    with chess.polyglot.open_reader(path) as reader:
        return [entry.move for entry in reader.find_all(board)]


def learnt():
    """Everything learnt so far, newest last: {position key: row}."""
    rows = {}
    if os.path.exists(LEARNT):
        for line in open(LEARNT, encoding="utf-8"):
            if line.strip():
                row = json.loads(line)
                rows[row["key"]] = row
    return rows


def write_book():
    """ours.bin from ours.jsonl: one move per position (the latest), plus the inert entry; moved over the old."""
    rows = learnt()
    entries = sorted((row["key"], row["polyglot"]) for row in rows.values())
    temp = OURS + ".tmp"
    with open(temp, "wb") as out:
        out.write(struct.pack(">QHHI", 0, 0, 1, 0))   # inert: no position has key 0, and the file is never empty
        for key, move in entries:
            out.write(struct.pack(">QHHI", key, move, 1, 0))
    for attempt in range(40):
        try:
            os.replace(temp, OURS)
            return len(entries)
        except PermissionError:   # lichess-bot has it open this instant
            time.sleep(0.25)
    log("couldn't replace ours.bin (in use); trying again after the next game")
    return len(entries)


def score(info, turn):
    """The score for the side to move, in centipawns (mates as +-10000 less the distance)."""
    return info["score"].pov(turn).score(mate_score=10000)


def study(engine, game, site):
    """The positions of one game worth learning, thought about; the rows kept."""
    us = chess.WHITE if game.headers.get("White") == ME else chess.BLACK
    board = game.board()
    candidates = []
    for move in game.mainline_moves():
        if len(board.move_stack) > BOOK_PLIES:
            break
        if board.turn == us and not book_moves(MAIN, board):
            wide = book_moves(WIDE, board)
            kind = "check" if wide and move in wide else "think" if not wide else None
            if kind:
                candidates.append((board.copy(), move, kind))
        board.push(move)
    kept = []
    known = learnt()
    for position, played, kind in candidates[:PER_GAME]:
        key = chess.polyglot.zobrist_hash(position)
        if key in known:
            continue   # learnt already (a position seen before: the thing this is for)
        if kind == "think":
            info = engine.analyse(position, chess.engine.Limit(time=THINK))
            best = info["pv"][0]
            row = {"why": "no book", "best_cp": score(info, position.turn)}
        else:
            info = engine.analyse(position, chess.engine.Limit(time=CHECK))
            best = info["pv"][0]
            if best == played:
                continue   # the wide book's move is the engine's own choice: nothing to add
            after = position.copy()
            after.push(played)
            reply = engine.analyse(after, chess.engine.Limit(time=CHECK))
            best_cp, played_cp = score(info, position.turn), -score(reply, after.turn)
            if best_cp - played_cp < WORSE:
                continue   # the wide move is good enough
            row = {"why": "wide book vetoed", "best_cp": best_cp, "wide_move": played.uci(), "wide_cp": played_cp}
        row.update(key=key, polyglot=polyglot_move(position, best), move=best.uci(), san=position.san(best),
                   fen=position.fen(), game=site, played=played.uci(), depth=info.get("depth"),
                   when=time.strftime("%Y-%m-%d %H:%M"))
        with open(LEARNT, "a", encoding="utf-8") as out:
            out.write(json.dumps(row) + "\n")
        kept.append(row)
    return kept, len(candidates)


def read_game(path):
    raw = open(path, "rb").read()
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError:
        text = raw.decode("cp1252")
    return chess.pgn.read_game(io.StringIO(text))


def summary():
    rows = list(learnt().values())
    done = studied()
    think = [r for r in rows if r["why"] == "no book"]
    veto = [r for r in rows if r["why"] == "wide book vetoed"]
    print(f"{len(done)} games studied; {len(rows)} positions in our book: {len(think)} thought about where no book had a "
          f"move, {len(veto)} where the wide book's move was vetoed")
    for r in veto[-10:]:
        print(f"  vetoed {r['wide_move']} ({r['wide_cp']:+} cp) for {r['san']} ({r['best_cp']:+} cp) in {r['fen']}")
    used = 0
    for path in glob.glob(os.path.join(HERE, "logs", "bot-*.log")):
        used += sum(1 for line in open(path, encoding="utf-8", errors="replace") if "ours.bin" in line and "Got move" in line)
    print(f"moves the bot has played from our book: {used}")


def start_engine():
    engine = chess.engine.SimpleEngine.popen_uci(ENGINE)
    options = {"Hash": 256}
    if os.path.exists(EVAL):
        options["EvalFile"] = EVAL
    engine.configure(options)
    return engine


def main():
    if "--summary" in sys.argv:
        summary()
        return
    # Below normal, before the engine starts (a child of a below-normal process inherits it). The types matter:
    # without them ctypes passes the 64-bit pseudo-handle as a 32-bit int, and the call quietly fails.
    kernel32 = ctypes.windll.kernel32
    kernel32.GetCurrentProcess.restype = ctypes.c_void_p
    kernel32.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    if not kernel32.SetPriorityClass(kernel32.GetCurrentProcess(), 0x4000):
        log("couldn't lower my priority; carrying on")
    os.makedirs(BOOKS, exist_ok=True)
    write_book()
    done = studied()
    engine = start_engine()
    log(f"learning from the bot's games (think {THINK:g} s, check {CHECK:g} s, veto at {WORSE} cp, {PER_GAME} a game); "
        f"{len(done)} games studied before, {len(learnt())} positions in our book")
    failures = {}
    try:
        while True:
            # Only finished games: lichess-bot writes a game's file when it ends.
            games = sorted(glob.glob(os.path.join(HERE, "games", "*.pgn")), key=os.path.getmtime)
            new = [p for p in games if os.path.basename(p) not in done and time.time() - os.path.getmtime(p) > 5]
            for path in new:
                game = read_game(path)
                name = os.path.basename(path)
                site = game.headers.get("Site", name) if game else name
                if game is not None and game.headers.get("Variant", "Standard") == "Standard" and "FEN" not in game.headers:
                    try:
                        kept, considered = study(engine, game, site)
                    except chess.engine.EngineTerminatedError:
                        failures[name] = failures.get(name, 0) + 1
                        log(f"{site}: the engine died ({failures[name]} time(s) on this game); starting it again"
                            + ("" if failures[name] < 2 else ", and leaving this game"))
                        engine = start_engine()
                        if failures[name] < 2:
                            continue
                        kept, considered = [], 0
                    except Exception:
                        log(f"{site}: skipped after an error:\n" + traceback.format_exc())
                        kept, considered = [], 0
                    if kept:
                        size = write_book()
                        log(f"{site}: {len(kept)} kept of {considered} out-of-book positions -> our book {size}: "
                            + "; ".join(f"{r['san']} ({r['why']})" for r in kept))
                    else:
                        log(f"{site}: nothing new ({considered} out-of-book positions)")
                done.add(name)
                with open(DONE, "a", encoding="utf-8") as out:
                    out.write(name + "\n")
            time.sleep(30)
    except KeyboardInterrupt:
        log("stopped")
    except Exception:
        log("stopped by an error:\n" + traceback.format_exc())
        raise
    finally:
        engine.quit()


if __name__ == "__main__":
    main()
