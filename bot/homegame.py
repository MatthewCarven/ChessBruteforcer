"""Games against the bot at home (Matthew, 2026-10-22: "A user interface where I can play against the bot, and it learn
the games if they are any good like"; his picks 1(a) 2(a) 3(a), plus taking over a real game from any move, "to see
if its even possible"). Everything here but the window: bot/play.py is the window, bot/test-play.py the tests.

- **The bot**: the engine, with the bot's evaluation (learn.bot_evaluation: make-config.py's list) and the endgame
  tables, playing from the bot's books for its first 12 moves as lichess-bot does (main, ours, then home, then the
  wide one). Beside the live Lichess bot it thinks on 2 threads at below-normal priority (the bot's games come
  first); with the bot off, on 8 like the bot. No pondering (the Lichess bot ponders), so it's a little weaker.
- **The games**: bot/home/games/<date time> <White> v <Black>.pgn, saved after every move; never in bot/games, so
  the bot's Lichess record (record.csv) and the learner never see them. A game taken over from a real one holds
  the real game's moves up to the take-over, with [TakeOverPly] and [Source] headers.
- **What it learns** (2(a)), and only from the games you ask it to (the window's "Learn from this game" button;
  Matthew, 2026-10-23: "maybe only save the human book when i ask like i play a lot of bad games in testing"):
  from finished games with 10+ moves each (after any take-over), the positions in the
  bot's first 12 moves where none of main, ours and home had a move: a --think search (45 s) for the best move,
  then the best and the bot's own move each scored by a --check search (20 s) of the position after it. Kept only
  if the bot's move was --worse (75 cp) or more behind: the bot learns from its mistakes against you, and a game
  with none teaches nothing. At most 4 positions a game, nearest the start first, as the learner does. The moves go
  to bot/books/home.jsonl, and bot/books/home.bin is rebuilt from it: only the home app plays from it. The Lichess
  bot doesn't, until make-config.py lists it (not done; Matthew's call).
- bot/home/to-learn.txt lists the games you asked it to learn from, and bot/home/studied.txt the games studied (or
  found not worth it), one name a line each. A game asked for and not yet studied (the window closed mid-study) is
  studied at the window's next start; a game never asked for never is.
"""
import ctypes
import dataclasses
import datetime
import importlib.util
import json
import math
import os
import random
import struct
import subprocess
import time

import chess
import chess.engine
import chess.pgn
import chess.polyglot

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

_spec = importlib.util.spec_from_file_location("learn", os.path.join(HERE, "learn.py"))
learn = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(learn)

HOME = os.path.join(HERE, "home")
GAMES = os.path.join(HOME, "games")
STUDIED, TO_LEARN = os.path.join(HOME, "studied.txt"), os.path.join(HOME, "to-learn.txt")
STUDY_LOCK, COPIES = os.path.join(HOME, "study.lock"), os.path.join(HOME, "copies")
ENGINE = os.path.join(HOME, "engine", "ChessBruteforcer.Engine.exe")   # its own copy (bot\\play.cmd makes it)
TABLES = os.path.join(REPO, "tables")
HOME_ROWS, HOME_BOOK = (os.path.join(learn.BOOKS, name) for name in ("home.jsonl", "home.bin"))
BOT, PLAYER = learn.ME, "Matthew"
THINK, CHECK, WORSE, PER_GAME, MIN_MOVES = 45.0, 20.0, 75, 4, 10
UNTIMED_THINK = 5.0          # seconds a move for the bot when the game has no clock
NEED_MB = 1024 + 2048        # an engine with Hash 256 and the tables (~1 GB), and the 2 GB kept free


def log(text):
    """To the window's console and bot/logs/play-<date>.log."""
    line = time.strftime("%Y-%m-%d %H:%M:%S  ") + text
    print(line, flush=True)
    try:
        os.makedirs(os.path.join(HERE, "logs"), exist_ok=True)
        with open(os.path.join(HERE, "logs", time.strftime("play-%Y-%m-%d.log")), "a", encoding="utf-8") as out:
            out.write(line + "\n")
    except OSError:
        pass


# ---- the machine: is the Lichess bot playing, or a match running? -------------------------------------------------

@dataclasses.dataclass
class Setup:
    threads: int
    below_normal: bool
    table_cache: int          # MB
    note: str                 # for the window: what the bot plays with, and why
    warning: str = ""         # something running that a game would disturb (a match or a bench)
    hash_mb: int = 256        # the bot's (make-config.py); the window's study engine takes 64


def processes():
    """[(name, command line)] of every process, through PowerShell (a second or two); [] if that fails."""
    try:
        out = subprocess.run(
            ["powershell", "-NoProfile", "-Command",
             "Get-CimInstance Win32_Process | Select-Object Name, CommandLine | ConvertTo-Json -Compress"],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).stdout
        return [(p.get("Name") or "", p.get("CommandLine") or "") for p in json.loads(out)]
    except (OSError, ValueError, subprocess.SubprocessError):
        return []


def setup_for(procs, cpus=None):
    """How the home bot thinks, given what's running: beside the Lichess bot, 2 threads at below normal (its games
    come first); alone, 8 threads like the bot. An engine that isn't the bot's, the learner's or ours is a match or a
    bench: 1 thread, and a warning, since a game would disturb its timing."""
    cpus = cpus or os.cpu_count() or 8
    lines = [(name.lower(), (command or "").lower()) for name, command in procs]
    bot = any("lichess-bot.py" in command for _, command in lines)
    ours = ("\\engines\\bruteforcer\\", "\\engines\\learner\\", "\\home\\engine\\")
    others = [command for name, command in lines
              if ("chessbruteforcer.engine" in command or "chessbruteforcer.match" in command
                  or "ponder-match.py" in command)
              and not any(part in command for part in ours)
              and name in ("chessbruteforcer.engine.exe", "dotnet.exe", "python.exe", "chessbruteforcer.match.exe")]
    if others:
        return Setup(1, True, 128, "1 thread at low priority: a match or bench is running",
                     f"Another engine job is running ({len(others)} process(es): a match or a bench?). A game now "
                     "would disturb its timing; the queue's rule is a quiet machine for those.")
    if bot:
        return Setup(2, True, 128, "2 threads at low priority, since the Lichess bot is playing (its games come first)")
    threads = min(8, cpus)
    return Setup(threads, False, 512, f"{threads} threads, like the Lichess bot (it's off)")


def free_commit_mb():
    """Commit charge still free (what a new process can take before Windows refuses), in MB; None if unknown."""
    class Status(ctypes.Structure):
        _fields_ = [("length", ctypes.c_uint32), ("load", ctypes.c_uint32), ("total_phys", ctypes.c_uint64),
                    ("avail_phys", ctypes.c_uint64), ("total_page", ctypes.c_uint64), ("avail_page", ctypes.c_uint64),
                    ("total_virtual", ctypes.c_uint64), ("avail_virtual", ctypes.c_uint64),
                    ("avail_extended", ctypes.c_uint64)]
    status = Status()
    status.length = ctypes.sizeof(Status)
    try:
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            return status.avail_page // (1024 * 1024)
    except (AttributeError, OSError):
        pass
    return None


BELOW_NORMAL = 0x4000


def _kernel32():
    # The types matter: without them ctypes passes a 64-bit handle as a 32-bit int, and calls quietly fail (learn.py).
    kernel32 = ctypes.windll.kernel32
    kernel32.OpenProcess.restype = ctypes.c_void_p
    kernel32.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
    kernel32.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    kernel32.GetPriorityClass.argtypes = [ctypes.c_void_p]
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    return kernel32


def set_below_normal(pid):
    """Below normal for one process (the engine, not the window, which stays quick). False if it didn't take."""
    try:
        kernel32 = _kernel32()
        handle = kernel32.OpenProcess(0x0200 | 0x1000, False, pid)   # set information, query limited information
        if not handle:
            return False
        try:
            return bool(kernel32.SetPriorityClass(handle, BELOW_NORMAL))
        finally:
            kernel32.CloseHandle(handle)
    except (AttributeError, OSError):
        return False


def priority_class(pid):
    """A process's priority class (0x4000 below normal, 0x20 normal), or None."""
    try:
        kernel32 = _kernel32()
        handle = kernel32.OpenProcess(0x1000, False, pid)
        if not handle:
            return None
        try:
            return kernel32.GetPriorityClass(handle) or None
        finally:
            kernel32.CloseHandle(handle)
    except (AttributeError, OSError):
        return None


def engine_options(setup, evaluation=None, tables=None):
    """The UCI options: the bot's (make-config.py) with this setup's threads and table cache."""
    options = {"Hash": setup.hash_mb, "Threads": setup.threads, "Move Overhead": 100}
    if evaluation:
        options["EvalFile"] = evaluation
    if tables:
        options["EndgameTables"] = tables
        options["TableCache"] = setup.table_cache
    return options


def engine_pid(process):
    try:
        return process.transport.get_pid()
    except AttributeError:
        return None


def start_engine(setup, engine=ENGINE, evaluation=None, tables=None):
    # No console window of its own: a copied window (play.py --open) runs without one, and the engine would get one.
    process = chess.engine.SimpleEngine.popen_uci(engine, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if setup.below_normal:
        pid = engine_pid(process)
        if pid is None or not set_below_normal(pid):
            log("couldn't lower the engine's priority; carrying on")
    process.configure(engine_options(setup, evaluation, tables))
    return process


# ---- the books ----------------------------------------------------------------------------------------------------

def book_paths(books=None):
    """The books in lichess-bot's order (make-config.py), with ours and home between the main and the wide one."""
    books = books or learn.BOOKS
    main = os.path.join(books, "lichess-2200.bin")
    if not os.path.exists(main):
        main = os.path.join(books, "lichess-2026-09-2200.bin")
    names = (main, os.path.join(books, "ours.bin"), os.path.join(books, "home.bin"),
             os.path.join(books, "lichess-2200-wide.bin"))
    return [path for path in names if os.path.exists(path)]


def in_book_reach(board):
    """lichess-bot consults its books while the game has at most max_depth * 2 - 1 plies, from the usual start."""
    return len(board.move_stack) <= learn.BOOK_PLIES and board.root().board_fen() == chess.STARTING_BOARD_FEN


def book_move(board, paths, rng=random):
    """(move, book path) from the first book with a move here (weighted at random, as lichess-bot's
    weighted_random with min_weight 1), or (None, None)."""
    if not in_book_reach(board):
        return None, None
    for path in paths:
        with chess.polyglot.open_reader(path) as reader:
            entries = [entry for entry in reader.find_all(board) if entry.weight >= 1]
        if entries:
            return rng.choices(entries, weights=[entry.weight for entry in entries])[0].move, path
    return None, None


# ---- the clock ----------------------------------------------------------------------------------------------------

class Clock:
    """Two clocks: base seconds each, inc seconds added after each move. `now` is injectable for tests."""

    def __init__(self, base, inc, now=time.monotonic):
        self.base, self.inc, self.now = base, inc, now
        self.left = {chess.WHITE: float(base), chess.BLACK: float(base)}
        self.running, self.since = None, None

    def start(self, colour):
        self.running, self.since = colour, self.now()

    def remaining(self, colour):
        left = self.left[colour]
        if self.running == colour:
            left -= self.now() - self.since
        return max(0.0, left)

    def press(self, colour):
        """`colour` has moved: its time used comes off, its increment goes on, and the other clock starts."""
        self.left[colour] = self.remaining(colour) + self.inc
        self.start(not colour)

    def stop(self):
        if self.running is not None:
            self.left[self.running] = self.remaining(self.running)
        self.running = None

    def flagged(self):
        """The colour whose time is up, or None."""
        if self.running is not None and self.remaining(self.running) <= 0:
            return self.running
        return None


def clock_text(seconds):
    seconds = max(0, seconds)
    if seconds < 10:
        return f"{int(seconds // 60)}:{seconds % 60:04.1f}"
    seconds = int(math.ceil(seconds))
    return f"{seconds // 60}:{seconds % 60:02d}"


# ---- the bot's view of a position, in words ------------------------------------------------------------------------

def expected_score(cp):
    """Lichess's formula from centipawns to an expected score (wins plus half the draws) between equal players."""
    return 0.5 + 0.5 * (2 / (1 + math.exp(-0.00368208 * cp)) - 1)


def verdict(score, you, pieces, tables, seconds):
    """What the bot thinks of the position, for you (`you` a colour), in a sentence. `score` a PovScore."""
    mine = score.pov(you)
    exact = tables and pieces <= 5
    if mine.is_mate():
        moves = mine.mate()
        who = "You can" if moves > 0 else "The bot can"
        text = f"{who} force mate in {abs(moves)} move{'s' if abs(moves) != 1 else ''}"
        return (f"The endgame tables know this one exactly: {text[0].lower() + text[1:]}." if exact
                else f"The bot's view after {seconds:g} s: {text}.")
    cp = mine.score()
    if exact and cp == 0:
        return "The endgame tables know this one exactly: a draw with best play from both sides."
    words = ("you're winning" if cp >= 300 else "you're better" if cp >= 150 else "you're a little better"
             if cp >= 50 else "about level" if cp > -50 else "the bot is a little better" if cp > -150
             else "the bot is better" if cp > -300 else "the bot is winning")
    return (f"The bot's view after {seconds:g} s: {words} ({cp / 100:+.1f} pawns for you; Lichess's formula makes "
            f"that an expected score of {expected_score(cp):.0%} between equal players).")


# ---- one game: the board, the record, the clocks ------------------------------------------------------------------

class HomeGame:
    """A game in progress. The window drives it: human_move, then (in a worker thread) bot_limit / bot_played. The
    record is saved after every move, so a game left unfinished is on disk with result "*"."""

    def __init__(self, human, clock=None, setup=None, evaluation_name="", source=None, take_over_ply=None,
                 folder=None, now=time.monotonic, when=None):
        folder = folder or GAMES
        self.human, self.bot = human, not human
        self.setup = setup or Setup(1, False, 128, "")
        self.clock = Clock(*clock, now=now) if clock else None
        self.result, self.termination = "*", ""
        when = when or datetime.datetime.now()
        game = chess.pgn.Game()
        if source is not None:
            # A real game, taken over: its start (any FEN) and its moves up to the take-over, comments left behind.
            game.setup(source.board())
            node = game
            for move in list(source.mainline_moves())[:take_over_ply]:
                node = node.add_variation(move)
        names = {human: PLAYER, self.bot: BOT}
        game.headers.update({
            "Event": "Home game, taken over" if source is not None else "Home game", "Site": "home",
            "Date": when.strftime("%Y.%m.%d"), "Round": "-", "White": names[chess.WHITE], "Black": names[chess.BLACK],
            "Result": "*", "Time": when.strftime("%H:%M:%S"),
            "TimeControl": f"{clock[0]:g}+{clock[1]:g}" if clock else "-",
            "BotEval": evaluation_name or "built-in", "BotThreads": str(self.setup.threads),
            "BotSetup": self.setup.note})
        if source is not None:
            h = source.headers
            game.headers["TakeOverPly"] = str(take_over_ply)
            game.headers["Source"] = (f"{h.get('White', '?')} v {h.get('Black', '?')}, {h.get('Date', '?')}, "
                                      f"{h.get('Site', '?')}, result {h.get('Result', '*')}")
        self.game, self.node = game, game.end()
        self.board = self.node.board()
        self.start_ply = len(self.board.move_stack)
        name = f"{when:%Y-%m-%d %H%M%S} {names[chess.WHITE]} v {names[chess.BLACK]}.pgn"
        self.path = os.path.join(folder, name)
        if self.clock:
            self.clock.start(self.board.turn)
        self.save()

    @property
    def over(self):
        return self.result != "*"

    def human_to_move(self):
        return not self.over and self.board.turn == self.human

    def _push(self, move, colour):
        if self.clock:
            self.clock.press(colour)
        self.node = self.node.add_variation(move)
        if self.clock:
            self.node.set_clock(self.clock.left[colour])
        self.board.push(move)
        self._check_end()
        self.save()

    def human_move(self, move):
        """True if played; False if it isn't your move, the move isn't legal, or your time ran out first."""
        if self.check_flag() or not self.human_to_move() or move not in self.board.legal_moves:
            return False
        self._push(move, self.human)
        return True

    def bot_limit(self):
        if not self.clock:
            return chess.engine.Limit(time=UNTIMED_THINK)
        return chess.engine.Limit(white_clock=self.clock.remaining(chess.WHITE),
                                  black_clock=self.clock.remaining(chess.BLACK),
                                  white_inc=self.clock.inc, black_inc=self.clock.inc)

    def bot_played(self, move, score=None, depth=None, book=None):
        """The bot's move, from its search (score, depth) or from a book (its path). Dropped if the game is over
        (resigned, or the bot's time ran out while it thought)."""
        if self.check_flag() or self.over or self.board.turn != self.bot:
            return
        self._push(move, self.bot)
        if book:
            self.node.comment = (self.node.comment + " " if self.node.comment else "") + f"book {os.path.basename(book)}"
        elif score is not None:
            self.node.set_eval(score, depth)
        self.save()

    def check_flag(self):
        """Ends the game if a clock has run out; True if it did."""
        if self.over or not self.clock:
            return False
        colour = self.clock.flagged()
        if colour is None:
            return False
        winner = not colour
        if self.board.has_insufficient_material(winner):
            self._finish("1/2-1/2", "Time forfeit")   # the side with time left can't mate: a draw
        else:
            self._finish("1-0" if winner == chess.WHITE else "0-1", "Time forfeit")
        return True

    def resign(self):
        if not self.over:
            self._finish("0-1" if self.human == chess.WHITE else "1-0", "Normal", f"{PLAYER} resigned")

    def abandon(self):
        """Closed before the end: saved as it stands, result "*" (never studied)."""
        if not self.over:
            if self.clock:
                self.clock.stop()
            self.termination = "Abandoned"
            self.game.headers["Termination"] = "Abandoned"
            self.save()

    def _check_end(self):
        # Threefold and the 50-move rule end it at once, as on Lichess; so do mate, stalemate and too little material.
        outcome = self.board.outcome(claim_draw=True)
        if outcome:
            text = {chess.Termination.CHECKMATE: "checkmate", chess.Termination.STALEMATE: "stalemate",
                    chess.Termination.INSUFFICIENT_MATERIAL: "too little material to mate",
                    chess.Termination.THREEFOLD_REPETITION: "the same position three times",
                    chess.Termination.FIFTY_MOVES: "50 moves without a capture or a pawn move",
                    chess.Termination.FIVEFOLD_REPETITION: "the same position five times",
                    chess.Termination.SEVENTYFIVE_MOVES: "75 moves without a capture or a pawn move"}
            self._finish(outcome.result(), "Normal", text.get(outcome.termination, outcome.termination.name.lower()))

    def _finish(self, result, termination, why=""):
        self.result, self.termination = result, why or termination.lower()
        if self.clock:
            self.clock.stop()
        self.game.headers["Result"] = result
        self.game.headers["Termination"] = termination
        self.save()

    def describe_result(self):
        if not self.over:
            return ""
        if self.result == "1/2-1/2":
            return f"Draw: {self.termination}."
        you_won = (self.result == "1-0") == (self.human == chess.WHITE)
        return f"{'You won' if you_won else 'The bot won'}: {self.termination}."

    def save(self):
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        temp = self.path + ".tmp"
        with open(temp, "w", encoding="utf-8", newline="\n") as out:
            out.write(str(self.game) + "\n")
        os.replace(temp, self.path)


# ---- learning from finished games (2(a)) --------------------------------------------------------------------------

def read_game(path):
    return learn.read_game(path)


def bot_colour(game):
    if game.headers.get("White") == BOT:
        return chess.WHITE
    if game.headers.get("Black") == BOT:
        return chess.BLACK
    return None


def worth_studying(game):
    """(True, "") for a game to study; else (False, why not), in words."""
    if game is None:
        return False, "it doesn't read as a game"
    if game.headers.get("Result", "*") == "*":
        return False, "it wasn't finished"
    if game.headers.get("Variant", "Standard") != "Standard" or "FEN" in game.headers:
        return False, "it didn't start from the usual position, so the books don't apply"
    us = bot_colour(game)
    if us is None:
        return False, "the bot isn't one of its players"
    start = int(game.headers.get("TakeOverPly", "0") or 0)
    if start > learn.BOOK_PLIES:
        return False, "it was taken over after the bot's first 12 moves, past the book's reach"
    moves = list(game.mainline_moves())[start:]
    board = game.board()
    for move in list(game.mainline_moves())[:start]:
        board.push(move)
    ours = sum(1 for i in range(len(moves)) if (board.turn == us) == (i % 2 == 0))
    theirs = len(moves) - ours
    if ours < MIN_MOVES or theirs < MIN_MOVES:
        return False, f"it was short ({len(moves)} plies after the start; {MIN_MOVES}+ moves each are wanted)"
    return True, ""


def candidates(game, paths):
    """The positions to think about: the bot to move, from the take-over on, within the books' reach, where neither
    main, ours nor home had a move (where the wide book had one, its move is checked like the engine's own). At
    most PER_GAME, nearest the start first. [(board, move the bot played, "engine" or "wide book")]"""
    us, start = bot_colour(game), int(game.headers.get("TakeOverPly", "0") or 0)
    wide = [p for p in paths if os.path.basename(p) == "lichess-2200-wide.bin"]
    firm = [p for p in paths if p not in wide]
    board, found = game.board(), []
    for ply, move in enumerate(game.mainline_moves()):
        if len(board.move_stack) > learn.BOOK_PLIES:
            break
        if ply >= start and board.turn == us and not any(learn.book_moves(p, board) for p in firm):
            source = "wide book" if any(move in learn.book_moves(p, board) for p in wide) else "engine"
            found.append((board.copy(), move, source))
        board.push(move)
    return found[:PER_GAME]


def value_after(engine, board, move, seconds):
    """The move's value for the side making it, in centipawns, from a search of the position after it."""
    after = board.copy()
    after.push(move)
    outcome = after.outcome(claim_draw=True)
    if outcome:
        return 0 if outcome.winner is None else 10000
    return -learn.score(engine.analyse(after, chess.engine.Limit(time=seconds)), after.turn)


# (The paths below default to None and are read when called, so the tests can point the module elsewhere.)

def home_rows(path=None):
    """Everything learnt at home so far: {position key: row}, the latest per position."""
    path, rows = path or HOME_ROWS, {}
    if os.path.exists(path):
        for line in open(path, encoding="utf-8"):
            if line.strip():
                row = json.loads(line)
                rows[row["key"]] = row
    return rows


def write_book(rows_path=None, book_path=None):
    """home.bin from home.jsonl, as learn.write_book makes ours.bin: one move a position, an inert entry first."""
    book_path = book_path or HOME_BOOK
    entries = sorted((row["key"], row["polyglot"]) for row in home_rows(rows_path).values())
    temp = book_path + ".tmp"
    with open(temp, "wb") as out:
        out.write(struct.pack(">QHHI", 0, 0, 1, 0))
        for key, move in entries:
            out.write(struct.pack(">QHHI", key, move, 1, 0))
    for attempt in range(40):
        try:
            os.replace(temp, book_path)
            return len(entries)
        except PermissionError:   # the window has it open this instant
            time.sleep(0.25)
    return len(entries)


def study(engine, game, name, paths, think=THINK, check=CHECK, worse=WORSE, rows_path=None, evaluation_name=""):
    """The rows learnt from one game (already found worth studying), appended to rows_path."""
    rows_path = rows_path or HOME_ROWS
    known, kept = home_rows(rows_path), []
    for position, played, source in candidates(game, paths):
        key = chess.polyglot.zobrist_hash(position)
        if key in known:
            continue
        info = engine.analyse(position, chess.engine.Limit(time=think))
        if not info.get("pv"):
            continue
        best = info["pv"][0]
        if best == played:
            continue   # the bot played what a long think plays: nothing to learn
        best_cp = value_after(engine, position, best, check)
        played_cp = value_after(engine, position, played, check)
        if best_cp - played_cp < worse:
            continue   # the bot's move was good enough
        row = {"why": f"home: the {source}'s move was worse", "key": key,
               "polyglot": learn.polyglot_move(position, best), "move": best.uci(), "san": position.san(best),
               "best_cp": best_cp, "played": played.uci(), "played_san": position.san(played),
               "played_cp": played_cp, "fen": position.fen(), "move_number": position.fullmove_number,
               "game": name, "depth": info.get("depth"), "when": time.strftime("%Y-%m-%d %H:%M"),
               "eval": evaluation_name or "built-in"}
        os.makedirs(os.path.dirname(rows_path), exist_ok=True)
        with open(rows_path, "a", encoding="utf-8") as out:
            out.write(json.dumps(row) + "\n")
        known[key] = row
        kept.append(row)
    return kept


def _names(path):
    """A list file's names, in order, one a line (names have spaces in them, so never split on whitespace)."""
    if not os.path.exists(path):
        return []
    return [line.strip() for line in open(path, encoding="utf-8") if line.strip()]


def _append(path, name):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "a", encoding="utf-8") as out:
        out.write(name + "\n")


def studied(path=None):
    return set(_names(path or STUDIED))


def mark_studied(name, path=None):
    _append(path or STUDIED, name)


def asked(path=None):
    """The games you asked it to learn from (the Learn button), in the order asked."""
    return _names(path or TO_LEARN)


def ask_to_learn(name, path=None):
    """Adds a game to the ones to learn from (once)."""
    if name not in asked(path):
        _append(path or TO_LEARN, name)


def learn_state(name, game, done_path=None, asked_path=None):
    """Whether the Learn button can take this home game: (True, "") or (False, why not), in words."""
    if name in studied(done_path):
        return False, "it's been studied already"
    if name in asked(asked_path):
        return False, "it's waiting to be studied"
    return worth_studying(game)


def lock_study(path=None):
    """One window studies at a time (Copy to a new window makes two): True if this one may, and the lock file holds
    its process id. A lock left by a window that has gone (closed mid-study, crashed) is taken over."""
    path = path or STUDY_LOCK
    os.makedirs(os.path.dirname(path), exist_ok=True)
    for attempt in range(2):
        try:
            handle = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        except FileExistsError:
            try:
                pid = int(open(path, encoding="utf-8").read().strip() or 0)
            except (OSError, ValueError):
                pid = 0
            if pid and pid != os.getpid() and priority_class(pid) is not None:
                return False   # another window, still running, is studying
            try:
                os.remove(path)
            except OSError:
                return False
            continue
        with os.fdopen(handle, "w", encoding="utf-8") as out:
            out.write(str(os.getpid()))
        return True
    return False


def unlock_study(path=None):
    path = path or STUDY_LOCK
    try:
        with open(path, encoding="utf-8") as lock:
            mine = int(lock.read().strip() or 0) == os.getpid()
        if mine:
            os.remove(path)
    except (OSError, ValueError):
        pass


def copy_path(folder=None, when=None):
    """A new file for a copied game (the window's Copy to a new window), in bot/home/copies."""
    folder = folder or COPIES
    os.makedirs(folder, exist_ok=True)
    stem = f"{(when or datetime.datetime.now()):%Y-%m-%d %H%M%S} copy"
    path, n = os.path.join(folder, stem + ".pgn"), 1
    while os.path.exists(path):
        n += 1
        path = os.path.join(folder, f"{stem} {n}.pgn")
    return path


def black_at_bottom(headers):
    """Which way up to show a loaded game: your side at the bottom in a home game, the bot's in one of its own."""
    if headers.get("Black") == PLAYER:
        return True
    return headers.get("Black") == BOT and headers.get("White") not in (BOT, PLAYER)


def pending(folder=None, done_path=None, exclude=(), asked_path=None):
    """Home games you asked it to learn from and not yet studied, in the order asked (the game in progress, and any
    game no longer on disk, left out). A game never asked for is never pending."""
    folder = folder or GAMES
    done = studied(done_path)
    return [n for n in asked(asked_path) if n not in done and os.path.exists(os.path.join(folder, n))
            and os.path.join(folder, n) not in exclude]


def describe_row(row):
    """One learnt move, in words, for the window."""
    return (f"move {row['move_number']}: {row['san']} instead of {row['played_san']} "
            f"(the bot's move was {(row['best_cp'] - row['played_cp']) / 100:.1f} pawns worse)")


def study_pending(engine, say=log, folder=None, done_path=None, rows_path=None, book_path=None, paths=None,
                  exclude=(), think=THINK, check=CHECK, evaluation_name="", asked_path=None):
    """Studies every home game asked for and not yet studied; says what each taught. Returns the rows learnt."""
    folder, learnt = folder or GAMES, []
    for name in pending(folder, done_path, exclude, asked_path):
        path = os.path.join(folder, name)
        game = read_game(path)
        good, why = worth_studying(game)
        if not good:
            say(f"{name}: not studied: {why}.")
            mark_studied(name, done_path)
            continue
        say(f"{name}: studying (up to {PER_GAME} positions, {think:g} s + 2 x {check:g} s each)...")
        rows = study(engine, game, name, paths if paths is not None else book_paths(), think, check,
                     rows_path=rows_path, evaluation_name=evaluation_name)
        if rows:
            size = write_book(rows_path, book_path)
            say(f"{name}: learnt {len(rows)}: " + "; ".join(describe_row(r) for r in rows) + f". Home book: {size}.")
        else:
            say(f"{name}: nothing to learn: no move of the bot's in its first 12 was {WORSE / 100:g}+ pawns "
                "worse than a long think's.")
        mark_studied(name, done_path)
        learnt += rows
    return learnt
