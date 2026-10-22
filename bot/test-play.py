"""Tests for bot/homegame.py, everything of the home games but the window (bot/play.py).

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I bot\\test-play.py
    set PLAY_ENGINE=<path to ChessBruteforcer.Engine.exe>   (optional: a few short searches with the real engine too)

Books, games and the studied list live in a temporary folder; the real bot\\books and bot\\home are never written. The
study's engine is a fake with set answers (which move is best, what each move is worth), so what's kept is exact.
With PLAY_ENGINE set, the real engine also plays a move on the clock and studies a game for a fraction of a second a
search (1 thread, below normal: a light run).
"""
import datetime
import importlib.util
import io
import os
import random
import struct
import sys
import tempfile

import chess
import chess.engine
import chess.pgn
import chess.polyglot

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("homegame", os.path.join(HERE, "homegame.py"))
homegame = importlib.util.module_from_spec(spec)
spec.loader.exec_module(homegame)
learn = homegame.learn

failures = []


def check(name, got, want):
    ok = got == want
    print(f"{'ok  ' if ok else 'FAIL'} {name}: {got!r}" + ("" if ok else f", wanted {want!r}"))
    if not ok:
        failures.append(name)


def write_book(path, entries):
    """A Polyglot book from [(board, move, weight)]."""
    rows = sorted((chess.polyglot.zobrist_hash(b), learn.polyglot_move(b, m), w) for b, m, w in entries)
    with open(path, "wb") as out:
        for key, move, weight in rows:
            out.write(struct.pack(">QHHI", key, move, weight, 0))


def random_game(plies, seed=7):
    """A game of seeded random legal moves that doesn't end early."""
    rng, board = random.Random(seed), chess.Board()
    for _ in range(plies):
        moves = sorted(board.legal_moves, key=lambda m: m.uci())
        rng.shuffle(moves)
        for move in moves:
            board.push(move)
            if not board.is_game_over(claim_draw=True):
                break
            board.pop()
    return board


def as_game(board, white, black, result, headers=None):
    game = chess.pgn.Game.from_board(board)
    game.headers.update({"White": white, "Black": black, "Result": result, **(headers or {})})
    return game


def at_ply(game, ply):
    board = game.board()
    for move in list(game.mainline_moves())[:ply]:
        board.push(move)
    return board


class FakeEngine:
    """analyse() with set answers: `values` {(fen, uci): cp for the mover} for the position after a move (it reads
    the move off the board's stack); `best` {fen: uci} at a root; any other root, no line (the study skips it)."""

    def __init__(self, best, values):
        self.best, self.values, self.calls = best, values, []

    def analyse(self, board, limit):
        self.calls.append((board.fen(), limit.time))
        before = board.copy()
        move = before.pop() if before.move_stack else None
        if move and (before.fen(), move.uci()) in self.values:
            value = self.values[(before.fen(), move.uci())]
            return {"score": chess.engine.PovScore(chess.engine.Cp(-value), board.turn), "depth": 10}
        score = chess.engine.PovScore(chess.engine.Cp(0), board.turn)
        if board.fen() in self.best:
            return {"pv": [chess.Move.from_uci(self.best[board.fen()])], "depth": 12, "score": score}
        return {"pv": [], "depth": 1, "score": score}


# ---- the machine ----

bot_engine = r"C:\x\bot\lichess-bot\engines\bruteforcer\ChessBruteforcer.Engine.exe"
learner_engine = r"C:\x\bot\lichess-bot\engines\learner\ChessBruteforcer.Engine.exe"
home_engine = r"C:\x\bot\home\engine\ChessBruteforcer.Engine.exe"
bot_python = ("python.exe", r"C:\x\.venv\Scripts\python.exe lichess-bot.py -l C:\x\logs")
alone = homegame.setup_for([("explorer.exe", "explorer"), ("ChessBruteforcer.Engine.exe", home_engine)], cpus=16)
check("alone: 8 threads, normal priority, no warning", (alone.threads, alone.below_normal, alone.warning),
      (8, False, ""))
check("alone on 4 CPUs: 4 threads", homegame.setup_for([], cpus=4).threads, 4)
beside = homegame.setup_for([bot_python, ("ChessBruteforcer.Engine.exe", bot_engine),
                             ("ChessBruteforcer.Engine.exe", learner_engine)], cpus=16)
check("beside the bot: 2 threads below normal, cache 128, no warning",
      (beside.threads, beside.below_normal, beside.table_cache, beside.warning), (2, True, 128, ""))
match = homegame.setup_for([bot_python, ("dotnet.exe", r"dotnet C:\scratch\new\ChessBruteforcer.Engine.dll")], cpus=16)
check("a match's engine (dotnet ...Engine.dll): 1 thread, warned", (match.threads, bool(match.warning)), (1, True))
check("a ponder-match.py: warned", bool(homegame.setup_for([("python.exe", r"python scripts\ponder-match.py")]).warning),
      True)
check("a shell whose command line names the engine isn't a match",
      homegame.setup_for([("pwsh.exe", "grep ChessBruteforcer.Engine")]).warning, "")
check("engine options with net and tables",
      homegame.engine_options(beside, "net.nnue", "tables"),
      {"Hash": 256, "Threads": 2, "Move Overhead": 100, "EvalFile": "net.nnue", "EndgameTables": "tables",
       "TableCache": 128})
check("engine options with neither", homegame.engine_options(alone), {"Hash": 256, "Threads": 8, "Move Overhead": 100})
free = homegame.free_commit_mb()
check("free commit reads as a number of MB", isinstance(free, int) and free > 0, True)

# ---- the clock ----

now = [100.0]
clock = homegame.Clock(60, 2, now=lambda: now[0])
clock.start(chess.WHITE)
now[0] += 10
check("white's clock runs", clock.remaining(chess.WHITE), 50.0)
check("black's waits", clock.remaining(chess.BLACK), 60.0)
clock.press(chess.WHITE)
check("white pressed: 50 + 2", clock.left[chess.WHITE], 52.0)
now[0] += 61
check("black's flag falls", clock.flagged(), chess.BLACK)
clock.stop()
check("stopped: nothing runs", (clock.running, clock.remaining(chess.BLACK)), (None, 0.0))
check("clock text", [homegame.clock_text(s) for s in (300, 61.2, 9.46, 0)], ["5:00", "1:02", "0:09.5", "0:00.0"])

# ---- the verdict ----

pov = chess.engine.PovScore
check("expected score, level", round(homegame.expected_score(0), 3), 0.5)
check("you mate in 3", homegame.verdict(pov(chess.engine.Mate(3), chess.WHITE), chess.WHITE, 20, True, 3),
      "The bot's view after 3 s: You can force mate in 3 moves.")
check("the tables: the bot mates in 1", homegame.verdict(pov(chess.engine.Mate(1), chess.BLACK), chess.WHITE, 4, True, 3),
      "The endgame tables know this one exactly: the bot can force mate in 1 move.")
check("the tables: a draw", homegame.verdict(pov(chess.engine.Cp(0), chess.WHITE), chess.BLACK, 5, True, 3),
      "The endgame tables know this one exactly: a draw with best play from both sides.")
check("no tables: 0 is only level", homegame.verdict(pov(chess.engine.Cp(0), chess.WHITE), chess.BLACK, 5, False, 3)
      .startswith("The bot's view after 3 s: about level (+0.0 pawns"), True)
check("the bot is winning, from your side", "the bot is winning (-4.5 pawns for you" in
      homegame.verdict(pov(chess.engine.Cp(450), chess.WHITE), chess.BLACK, 20, True, 3), True)
check("you're better", "you're better (+1.5 pawns for you; Lichess's formula makes that an expected score of 63%" in
      homegame.verdict(pov(chess.engine.Cp(150), chess.WHITE), chess.WHITE, 20, True, 3), True)
check("the words by size", [homegame.verdict(pov(chess.engine.Cp(cp), chess.WHITE), chess.WHITE, 20, True, 3)
                            .split(": ")[1].split(" (")[0] for cp in (300, 299, 149, 50, 49, -49, -50, -149, -150, -300)],
      ["you're winning", "you're better", "you're a little better", "you're a little better", "about level",
       "about level", "the bot is a little better", "the bot is a little better", "the bot is better",
       "the bot is winning"])

with tempfile.TemporaryDirectory() as root:
    books, games = os.path.join(root, "books"), os.path.join(root, "games")
    os.makedirs(books)
    rows, home_book, done = os.path.join(books, "home.jsonl"), os.path.join(books, "home.bin"), os.path.join(root, "studied.txt")

    # ---- a game: fool's mate, the bot Black ----
    when = datetime.datetime(2026, 10, 22, 23, 30, 5)
    now = [0.0]
    g = homegame.HomeGame(chess.WHITE, clock=(180, 2), setup=beside, evaluation_name="h1536-3m", folder=games,
                          now=lambda: now[0], when=when)
    check("file name", os.path.basename(g.path), "2026-10-22 233005 Matthew v matthewcarven.pgn")
    check("saved at once", os.path.exists(g.path), True)
    check("your move first", g.human_to_move(), True)
    check("an illegal move is refused", g.human_move(chess.Move.from_uci("e2e5")), False)
    now[0] += 4
    check("f3", g.human_move(chess.Move.from_uci("f2f3")), True)
    check("not your move now", g.human_move(chess.Move.from_uci("g2g4")), False)
    check("the bot's limit is its clock", (g.bot_limit().white_clock, g.bot_limit().black_clock, g.bot_limit().white_inc),
          (178.0, 180.0, 2))
    g.bot_played(chess.Move.from_uci("e7e5"), book=os.path.join(books, "lichess-2200.bin"))
    g.human_move(chess.Move.from_uci("g2g4"))
    now[0] += 1.5
    g.bot_played(chess.Move.from_uci("d8h4"), score=pov(chess.engine.Mate(1), chess.BLACK), depth=1)
    check("mate: the bot won", (g.over, g.result, g.describe_result()), (True, "0-1", "The bot won: checkmate."))
    check("no moves after the end", g.human_move(chess.Move.from_uci("a2a3")), False)
    saved = learn.read_game(g.path)
    check("saved headers", {k: saved.headers.get(k) for k in ("White", "Black", "Result", "TimeControl", "Termination",
                                                               "BotEval", "BotThreads", "Event")},
          {"White": "Matthew", "Black": "matthewcarven", "Result": "0-1", "TimeControl": "180+2",
           "Termination": "Normal", "BotEval": "h1536-3m", "BotThreads": "2", "Event": "Home game"})
    nodes = list(saved.mainline())
    check("saved moves", [n.san() for n in nodes], ["f3", "e5", "g4", "Qh4#"])
    check("clocks after each move", [n.clock() for n in nodes], [178.0, 182.0, 180.0, 182.5])
    check("the book move says its book", "book lichess-2200.bin" in nodes[1].comment, True)
    check("the searched move has its eval", nodes[3].eval() is not None, True)
    check("no .tmp left", [n for n in os.listdir(games) if n.endswith(".tmp")], [])

    # ---- time, resignation, abandoning ----
    now = [0.0]
    t = homegame.HomeGame(chess.BLACK, clock=(5, 0), folder=games, now=lambda: now[0],
                          when=when + datetime.timedelta(seconds=1))
    check("you're Black: the bot first", (t.human_to_move(), t.board.turn), (False, chess.WHITE))
    now[0] += 6
    check("the bot's flag falls while it thinks", t.check_flag(), True)
    check("you won on time", (t.result, t.describe_result()), ("0-1", "You won: time forfeit."))
    t.bot_played(chess.Move.from_uci("e2e4"), score=pov(chess.engine.Cp(20), chess.WHITE), depth=5)
    check("a move after the flag is dropped", len(t.board.move_stack), 0)
    bare = homegame.HomeGame(chess.WHITE, clock=(5, 0), folder=games, now=lambda: now[0],
                             when=when + datetime.timedelta(seconds=2))
    bare.board.set_fen("8/8/8/4k3/8/8/8/4K2Q w - - 0 1")   # (a test only: the bot has a bare king)
    bare.clock.start(chess.WHITE)
    now[0] += 6
    bare.check_flag()
    check("your flag with the bot's bare king: a draw", bare.result, "1/2-1/2")
    r = homegame.HomeGame(chess.WHITE, folder=games, when=when + datetime.timedelta(seconds=3))
    check("no clock: the bot thinks a fixed time", r.bot_limit().time, homegame.UNTIMED_THINK)
    r.resign()
    check("resigned", (r.result, r.describe_result()), ("0-1", "The bot won: Matthew resigned."))
    a = homegame.HomeGame(chess.WHITE, folder=games, when=when + datetime.timedelta(seconds=4))
    a.human_move(chess.Move.from_uci("e2e4"))
    a.abandon()
    check("abandoned: saved unfinished", (learn.read_game(a.path).headers["Result"],
                                          learn.read_game(a.path).headers["Termination"]), ("*", "Abandoned"))

    # ---- taking over a real game ----
    real = as_game(random_game(60), "someone", "matthewcarven", "1-0", {"Site": "https://lichess.org/AbCdEfGh",
                                                                       "Date": "2026.10.20"})
    takeover = homegame.HomeGame(chess.BLACK, clock=(300, 3), source=real, take_over_ply=30, folder=games,
                                 when=when + datetime.timedelta(seconds=5))
    check("taken over: the position at ply 30", takeover.board.fen(), at_ply(real, 30).fen())
    check("taken over: the history kept (for repetitions and the books)", len(takeover.board.move_stack), 30)
    check("taken over: headers", (takeover.game.headers["TakeOverPly"], takeover.game.headers["Event"],
                                  takeover.game.headers["Source"]),
          ("30", "Home game, taken over", "someone v matthewcarven, 2026.10.20, https://lichess.org/AbCdEfGh, result 1-0"))
    check("taken over as Black at ply 30: White (the bot) to move first", takeover.human_to_move(), False)
    fen_game = chess.pgn.read_game(io.StringIO('[FEN "8/8/8/4k3/8/8/4P3/4K3 w - - 0 1"]\n[SetUp "1"]\n\n1. e4 *'))
    from_fen = homegame.HomeGame(chess.WHITE, source=fen_game, take_over_ply=0, folder=games,
                                 when=when + datetime.timedelta(seconds=6))
    check("a game from a set-up position keeps it", from_fen.board.fen(), "8/8/8/4k3/8/8/4P3/4K3 w - - 0 1")
    check("... and saves its FEN", learn.read_game(from_fen.path).headers.get("FEN"), "8/8/8/4k3/8/8/4P3/4K3 w - - 0 1")

    # ---- the books ----
    played = random_game(60, seed=11)
    game = as_game(played, homegame.PLAYER, homegame.BOT, "1-0")
    moves = list(game.mainline_moves())

    def other(ply):
        """A legal move at `ply` that isn't the one played."""
        return next(m for m in sorted(at_ply(game, ply).legal_moves, key=lambda m: m.uci()) if m != moves[ply]).uci()

    write_book(os.path.join(books, "lichess-2200.bin"), [(at_ply(game, 0), moves[0], 5), (at_ply(game, 1), moves[1], 3),
                                                         (at_ply(game, 1), chess.Move.from_uci(other(1)), 0)])
    # The wide book: the bot's own move at ply 3; another move at ply 5 (so ply 5's move is the engine's).
    write_book(os.path.join(books, "lichess-2200-wide.bin"), [(at_ply(game, 3), moves[3], 2),
                                                              (at_ply(game, 5), chess.Move.from_uci(other(5)), 2)])
    with open(os.path.join(books, "ours.bin"), "wb") as out:
        out.write(struct.pack(">QHHI", 0, 0, 1, 0))
    paths = homegame.book_paths(books)
    check("book order: main, ours, wide (no home yet)", [os.path.basename(p) for p in paths],
          ["lichess-2200.bin", "ours.bin", "lichess-2200-wide.bin"])
    check("the main book's move", homegame.book_move(at_ply(game, 1), paths)[0], moves[1])
    check("the wide book answers where the main one can't", [os.path.basename(p or "") for p in
                                                            [homegame.book_move(at_ply(game, 3), paths)[1]]],
          ["lichess-2200-wide.bin"])
    check("no book: none", homegame.book_move(at_ply(game, 7), paths), (None, None))
    deep = at_ply(game, 24)
    check("past ply 23: no book even where there's a move", homegame.in_book_reach(deep), False)
    check("ply 23 is still in reach", homegame.in_book_reach(at_ply(game, 23)), True)
    check("a set-up position: no book", homegame.in_book_reach(chess.Board("8/8/8/4k3/8/8/4P3/4K3 w - - 0 1")), False)

    # ---- what's worth studying ----
    check("this game: worth it", homegame.worth_studying(game), (True, ""))
    check("unfinished", homegame.worth_studying(as_game(played, homegame.PLAYER, homegame.BOT, "*"))[1],
          "it wasn't finished")
    check("short", homegame.worth_studying(as_game(random_game(16), homegame.PLAYER, homegame.BOT, "1-0"))[1],
          "it was short (16 plies after the start; 10+ moves each are wanted)")
    check("10 moves each is enough", homegame.worth_studying(as_game(random_game(20), homegame.PLAYER, homegame.BOT,
                                                                     "1-0"))[0], True)
    check("19 plies isn't", homegame.worth_studying(as_game(random_game(19), homegame.PLAYER, homegame.BOT, "1-0"))[0],
          False)
    late = as_game(played, homegame.PLAYER, homegame.BOT, "1-0", {"TakeOverPly": "24"})
    check("taken over past the books' reach", homegame.worth_studying(late)[1],
          "it was taken over after the bot's first 12 moves, past the book's reach")
    early = as_game(played, homegame.PLAYER, homegame.BOT, "1-0", {"TakeOverPly": "40"})
    check("taken over at ply 40: not studied", homegame.worth_studying(early)[0], False)
    check("the bot not playing", homegame.worth_studying(as_game(played, "a", "b", "1-0"))[1],
          "the bot isn't one of its players")

    # ---- candidates: the bot is Black; ply 1 is the main book's, ply 3 the wide book's own move ----
    found = homegame.candidates(game, paths)
    check("candidates: the bot's plies outside main/ours/home, 4 at most", [len(b.move_stack) for b, _, _ in found],
          [3, 5, 7, 9])
    check("their sources", [s for _, _, s in found], ["wide book", "engine", "engine", "engine"])
    check("their moves are the bot's", [m for _, m, _ in found], [moves[3], moves[5], moves[7], moves[9]])
    taken = as_game(played, homegame.PLAYER, homegame.BOT, "1-0", {"TakeOverPly": "6"})
    check("from a take-over at ply 6", [len(b.move_stack) for b, _, _ in homegame.candidates(taken, paths)],
          [7, 9, 11, 13])

    # ---- the study, with set answers ----
    fens = {ply: at_ply(game, ply).fen() for ply in (3, 5, 7, 9)}
    best = {fens[3]: moves[3].uci(), fens[5]: other(5), fens[7]: other(7), fens[9]: other(9)}
    values = {(fens[5], other(5)): 50, (fens[5], moves[5].uci()): -60,     # 1.1 pawns: kept
              (fens[7], other(7)): 20, (fens[7], moves[7].uci()): 0,       # 0.2: the bot's was good enough
              (fens[9], other(9)): 300, (fens[9], moves[9].uci()): 225}    # 0.75 exactly: kept
    fake = FakeEngine(best, values)
    kept = homegame.study(fake, game, "g.pgn", paths, think=45, check=20, rows_path=rows, evaluation_name="test")
    check("kept: where the bot's move was 75+ cp worse", [r["move"] for r in kept], [other(5), other(9)])
    check("their sizes", [(r["best_cp"], r["played_cp"]) for r in kept], [(50, -60), (300, 225)])
    check("the search times: 45 s to find, 20 s to score", sorted({t for _, t in fake.calls}), [20, 45])
    check("no scoring where the long think agreed", sum(1 for f, _ in fake.calls if f == fens[3]), 1)
    check("in words", homegame.describe_row(kept[0]),
          f"move 3: {at_ply(game, 5).san(chess.Move.from_uci(other(5)))} instead of {at_ply(game, 5).san(moves[5])} "
          "(the bot's move was 1.1 pawns worse)")
    check("the home book", homegame.write_book(rows, home_book), 2)
    with chess.polyglot.open_reader(home_book) as reader:
        check("home.bin holds the learnt move", [e.move.uci() for e in reader.find_all(at_ply(game, 5))], [other(5)])
        check("and the inert entry", len(list(reader)), 3)
    paths = homegame.book_paths(books)
    check("book order with home: main, ours, home, wide", [os.path.basename(p) for p in paths],
          ["lichess-2200.bin", "ours.bin", "home.bin", "lichess-2200-wide.bin"])
    check("the next game plays it from home.bin", homegame.book_move(at_ply(game, 5), paths)[0].uci(), other(5))
    second = FakeEngine(best, values)
    again = homegame.study(second, game, "g.pgn", paths, rows_path=rows)
    check("the same game again: nothing new", again, [])
    check("... and the positions learnt aren't thought about again (home.bin has them)",
          [f for f, _ in second.calls if f in (fens[5], fens[9])], [])

    # ---- the games asked for (the Learn button), and only those ----
    home_games, asks = os.path.join(root, "home-games"), os.path.join(root, "to-learn.txt")
    os.makedirs(home_games)
    for name, g2 in (("a b.pgn", game), ("b.pgn", as_game(random_game(10), homegame.PLAYER, homegame.BOT, "0-1")),
                     ("c.pgn", as_game(played, homegame.PLAYER, homegame.BOT, "*")), ("d.pgn", game)):
        with open(os.path.join(home_games, name), "w", encoding="utf-8") as out:
            out.write(str(g2) + "\n")
    in_progress = [os.path.join(home_games, "c.pgn")]
    check("nothing asked for: nothing pending", homegame.pending(home_games, done, in_progress, asks), [])
    check("the Learn button takes a finished game", homegame.learn_state("a b.pgn", game, done, asks), (True, ""))
    check("... not a short one", homegame.learn_state("b.pgn", learn.read_game(os.path.join(home_games, "b.pgn")),
                                                      done, asks)[1].startswith("it was short"), True)
    for name in ("b.pgn", "a b.pgn", "c.pgn", "b.pgn", "gone.pgn"):
        homegame.ask_to_learn(name, asks)
    check("asked: in order, each once (a name with a space kept whole)", homegame.asked(asks),
          ["b.pgn", "a b.pgn", "c.pgn", "gone.pgn"])
    check("... and pressed again: waiting", homegame.learn_state("a b.pgn", game, done, asks),
          (False, "it's waiting to be studied"))
    check("pending: those asked, not the game in progress, not one gone, not d (never asked)",
          homegame.pending(home_games, done, in_progress, asks), ["b.pgn", "a b.pgn"])
    said = []
    fresh_rows = os.path.join(root, "fresh.jsonl")
    learnt = homegame.study_pending(FakeEngine(best, values), said.append, home_games, done, fresh_rows,
                                    os.path.join(root, "fresh.bin"), paths=homegame.book_paths(books)[:2] + [
                                        os.path.join(books, "lichess-2200-wide.bin")],
                                    exclude=in_progress, think=45, check=20, asked_path=asks)
    check("study_pending: a b.pgn's two", len(learnt), 2)
    check("b.pgn: said why not", any(s.startswith("b.pgn: not studied: it was short") for s in said), True)
    check("both marked studied, d never", sorted(homegame.studied(done)), ["a b.pgn", "b.pgn"])
    check("nothing pending after", homegame.pending(home_games, done, in_progress, asks), [])
    check("studied: the button says so", homegame.learn_state("a b.pgn", game, done, asks),
          (False, "it's been studied already"))

    # ---- one window studies at a time ----
    lock = os.path.join(root, "study.lock")
    check("the first window takes the lock", homegame.lock_study(lock), True)
    with open(lock, "w", encoding="utf-8") as out:
        out.write(str(os.getppid()))   # (as if held by another window still running: this test's parent)
    check("another window's lock holds", homegame.lock_study(lock), False)
    homegame.unlock_study(lock)
    check("... and isn't ours to remove", os.path.exists(lock), True)
    with open(lock, "w", encoding="utf-8") as out:
        out.write("999999999")   # a window long gone
    check("a gone window's lock is taken over", homegame.lock_study(lock), True)
    homegame.unlock_study(lock)
    check("unlocked", os.path.exists(lock), False)

    # ---- copies, and which way up ----
    copies = os.path.join(root, "copies")
    first = homegame.copy_path(copies, when)
    open(first, "w").close()
    check("copy names", [os.path.basename(first), os.path.basename(homegame.copy_path(copies, when))],
          ["2026-10-22 233005 copy.pgn", "2026-10-22 233005 copy 2.pgn"])
    check("which way up", [homegame.black_at_bottom({"White": w, "Black": b}) for w, b in
                           (("Matthew", "matthewcarven"), ("matthewcarven", "Matthew"), ("xPOI", "matthewcarven"),
                            ("matthewcarven", "xPOI"), ("a", "b"))], [False, True, True, False, False])

    # ---- the real engine (optional) ----
    engine_path = os.environ.get("PLAY_ENGINE")
    if engine_path:
        light = homegame.Setup(1, True, 64, "test")
        engine = homegame.start_engine(light, engine_path)
        try:
            check("the engine runs below normal (and only it)",
                  (homegame.priority_class(homegame.engine_pid(engine)), homegame.priority_class(os.getpid())),
                  (homegame.BELOW_NORMAL, 0x20))
            h = homegame.HomeGame(chess.BLACK, clock=(10, 0), setup=light, folder=games,
                                  when=when + datetime.timedelta(seconds=9))
            analysis = engine.analysis(h.board, h.bot_limit())
            move = analysis.wait().move
            info = analysis.info
            h.bot_played(move, score=info.get("score"), depth=info.get("depth"))
            check("the real engine played a legal first move on its clock", (len(h.board.move_stack), "score" in info),
                  (1, True))
            stopped = engine.analysis(h.board, chess.engine.Limit(time=30))
            stopped.stop()
            check("a search can be stopped (resigning mid-think)", stopped.wait().move in h.board.legal_moves, True)
            real_rows = os.path.join(root, "real.jsonl")
            kept = homegame.study(engine, game, "real.pgn", homegame.book_paths(books)[:2], think=0.3, check=0.2,
                                  rows_path=real_rows)
            check("the real engine studies a game (rows or none, no error)", isinstance(kept, list), True)
            print(f"     (the real engine kept {len(kept)}: " + "; ".join(homegame.describe_row(r) for r in kept) + ")")
        finally:
            engine.quit()
    else:
        print("     (PLAY_ENGINE not set: the real engine's part skipped)")

print(f"\n{'all passed' if not failures else f'{len(failures)} FAILED: ' + ', '.join(failures)}")
sys.exit(1 if failures else 0)
