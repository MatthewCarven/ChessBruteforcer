"""Play the bot at home: a board and clocks against the engine, with the bot's network, tables and books.

    bot\\play.cmd                    (builds the engine, copies it to bot\\home\\engine, then this)
    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I bot\\play.py [--engine <exe>] [--eval <file>] [--tables <folder>]
                                     [--think <s>] [--check <s>]

- **New game**: your colour and a clock. No take-backs. Resign any time; the bot never resigns.
- **Load game**: one of the bot's games (bot\\games), a home game, or a pasted PGN (Lichess's "Share & export").
  Step through it (the arrows, or click a move) and **Take over from here**: you play one side from that move on,
  the bot the other. First it says what it makes of the position, exactly when 5 pieces or fewer are left (the
  tables), "to see if its even possible".
- **Learn from this game** (a finished home game on screen): the window studies it in the background (homegame.py's
  docstring: 2(a)) and says what it learnt; you can play on meanwhile. Nothing is studied unless you press it
  (Matthew, 2026-10-23: he plays many test games). Closing the window mid-game saves the game unfinished.
- **Copy to a new window**: the game on screen (being played or loaded) opens in a second window at the position
  shown, to try things out there (take over, step through) while this one goes on as it was, clocks included.
  The copy is a file in bot\\home\\copies; the second window is play.py --open <file> --ply <n>, with no console.

The Lichess bot and the learner are never touched: the games go to bot\\home\\games, the learnt moves to
bot\\books\\home.jsonl and home.bin, which only this window plays from.
"""
import importlib.util
import io
import logging
import os
import queue
import random
import subprocess
import sys
import threading
import time
import tkinter as tk
import traceback
from tkinter import filedialog, messagebox

import chess
import chess.engine
import chess.pgn

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("homegame", os.path.join(HERE, "homegame.py"))
homegame = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(homegame)
learn = homegame.learn
# python-chess calls the engine's "info string" lines (its network, its tables) unexpected: quiet in the console.
logging.getLogger("chess.engine").setLevel(logging.ERROR)


def option(name, default, kind=str):
    args = sys.argv
    return kind(args[args.index(name) + 1]) if name in args else default


ENGINE = option("--engine", homegame.ENGINE)
EVAL = option("--eval", learn.EVAL)
EVAL_NAME = os.path.splitext(os.path.basename(EVAL))[0] if EVAL else "built-in"
TABLES = option("--tables", homegame.TABLES if os.path.isdir(homegame.TABLES) else None)
THINK, CHECK = option("--think", homegame.THINK, float), option("--check", homegame.CHECK, float)
OPEN, OPEN_PLY = option("--open", None), option("--ply", None, int)   # a copied game (Copy to a new window)
VERDICT_SECONDS = 3

SQUARE = 64
LIGHT, DARK = "#f0d9b5", "#b58863"
LAST_LIGHT, LAST_DARK = "#cdd26a", "#aaa23a"
SELECTED, CHECK_RED, DOT = "#829769", "#e2514c", "#5b7a3a"
SOLID = {chess.KING: "♚", chess.QUEEN: "♛", chess.ROOK: "♜", chess.BISHOP: "♝",
         chess.KNIGHT: "♞", chess.PAWN: "♟"}
OUTLINE = {chess.KING: "♔", chess.QUEEN: "♕", chess.ROOK: "♖", chess.BISHOP: "♗",
           chess.KNIGHT: "♘", chess.PAWN: "♙"}
CLOCKS = [("3+2", (180, 2)), ("5+3", (300, 3)), ("10+5", (600, 5)), ("15+10", (900, 10)), ("30+20", (1800, 20)),
          ("No clock (the bot thinks 5 s a move)", None)]
PIECE_FONT = ("Segoe UI Symbol", 38)


def san_lines(board):
    """The moves of a board's stack, a line a move number: [(text, the ply of the line's first move)]."""
    root, lines = board.root(), []
    for ply, move in enumerate(board.move_stack):
        san = root.san(move)
        if root.turn == chess.WHITE or not lines:
            dots = "." if root.turn == chess.WHITE else "..."
            lines.append([f"{root.fullmove_number}{dots} {san}", ply])
        else:
            lines[-1][0] = f"{lines[-1][0]:<12}{san}"
        root.push(move)
    return [(text, ply) for text, ply in lines]


class Choice(tk.Toplevel):
    """A small dialog of radio-button groups; .result is {group: value}, or None if cancelled."""

    def __init__(self, parent, title, intro, groups):
        super().__init__(parent)
        self.title(title)
        self.transient(parent)
        self.resizable(False, False)
        self.result, self.vars = None, {}
        if intro:
            tk.Label(self, text=intro, justify="left", wraplength=360).pack(anchor="w", padx=14, pady=(12, 4))
        for name, (label, options, default) in groups.items():
            frame = tk.LabelFrame(self, text=label, padx=8, pady=4)
            frame.pack(fill="x", padx=12, pady=6)
            var = tk.StringVar(value=default)
            for text in options:
                tk.Radiobutton(frame, text=text, value=text, variable=var).pack(anchor="w")
            self.vars[name] = var
        buttons = tk.Frame(self)
        buttons.pack(pady=(4, 12))
        tk.Button(buttons, text="Play", width=10, command=self.ok, default="active").pack(side="left", padx=6)
        tk.Button(buttons, text="Cancel", width=10, command=self.destroy).pack(side="left", padx=6)
        self.bind("<Return>", lambda e: self.ok())
        self.bind("<Escape>", lambda e: self.destroy())
        self.grab_set()
        self.wait_window()

    def ok(self):
        self.result = {name: var.get() for name, var in self.vars.items()}
        self.destroy()


class App:
    def __init__(self, root):
        self.root = root
        root.title("Play the bot")
        root.resizable(False, False)
        self.events = queue.Queue()        # callables from the worker threads, run by tick() on this thread
        self.game = None                   # the HomeGame being played
        self.review, self.review_ply = None, 0   # a loaded or finished game (chess.pgn.Game) and the ply shown
        self.review_name = None            # the review's file name, when it's a home game (the Learn button's)
        self.flipped, self.selected = False, None
        self.engine, self.engine_setup, self.setup = None, None, None
        self.thinking = None               # the bot's search in progress (stoppable)
        self.studying, self.study_again, self.study_engine = False, False, None
        self.closing = False

        self.canvas = tk.Canvas(root, width=8 * SQUARE, height=8 * SQUARE, highlightthickness=0)
        self.canvas.grid(row=0, column=0, rowspan=3, padx=10, pady=10)
        self.canvas.bind("<Button-1>", self.click)

        side = tk.Frame(root)
        side.grid(row=0, column=1, sticky="n", padx=(0, 10), pady=10)
        self.top_clock = tk.Label(side, font=("Consolas", 16, "bold"), width=20, anchor="w")   # fits "Black (bot)  0:09.5"
        self.top_clock.pack(anchor="w")
        self.moves = tk.Listbox(side, width=34, height=17, font=("Consolas", 10), activestyle="none",
                                exportselection=False)
        self.moves.pack(pady=4)
        self.moves.bind("<<ListboxSelect>>", self.pick_move)
        self.bottom_clock = tk.Label(side, font=("Consolas", 16, "bold"), width=20, anchor="w")
        self.bottom_clock.pack(anchor="w")

        nav = tk.Frame(side)
        nav.pack(pady=(6, 2))
        for text, step in (("|<", "start"), ("<", -1), (">", 1), (">|", "end")):
            tk.Button(nav, text=text, width=4, command=lambda s=step: self.step(s)).pack(side="left", padx=1)
        tk.Button(nav, text="Flip", width=5, command=self.flip).pack(side="left", padx=(8, 0))
        self.take_over_button = tk.Button(side, text="Take over from here...", command=self.take_over, width=30)
        self.take_over_button.pack(pady=2)
        self.learn_button = tk.Button(side, text="Learn from this game", command=self.learn_this, width=30)
        self.learn_button.pack(pady=2)
        tk.Button(side, text="Copy to a new window", command=self.copy_to_window, width=30).pack(pady=2)
        row = tk.Frame(side)
        row.pack(pady=(6, 0))
        tk.Button(row, text="New game...", width=10, command=self.new_game).pack(side="left", padx=2)
        tk.Button(row, text="Load game...", width=10, command=self.load_game).pack(side="left", padx=2)
        self.resign_button = tk.Button(row, text="Resign", width=8, command=self.resign)
        self.resign_button.pack(side="left", padx=2)

        self.status = tk.Label(root, anchor="w", justify="left", wraplength=8 * SQUARE + 260, font=("Segoe UI", 10))
        self.status.grid(row=3, column=0, columnspan=2, sticky="w", padx=10)
        self.learning = tk.Label(root, anchor="w", justify="left", wraplength=8 * SQUARE + 260, fg="#555",
                                 font=("Segoe UI", 9))
        self.learning.grid(row=4, column=0, columnspan=2, sticky="w", padx=10)
        self.footer = tk.Label(root, anchor="w", justify="left", wraplength=8 * SQUARE + 260, fg="#555",
                               font=("Segoe UI", 9))
        self.footer.grid(row=5, column=0, columnspan=2, sticky="w", padx=10, pady=(0, 8))

        for key, step in (("<Left>", -1), ("<Right>", 1), ("<Home>", "start"), ("<End>", "end")):
            root.bind(key, lambda e, s=step: self.step(s))
        root.protocol("WM_DELETE_WINDOW", self.close)

        self.say("New game to play the bot; Load game to step through a real one and take over from any move.")
        self.footer_text()
        if not os.path.exists(ENGINE):
            self.say(f"No engine at {ENGINE}: start this with bot\\play.cmd, which builds it first.")
        homegame.log(f"play.py started: engine {ENGINE}, evaluation {EVAL_NAME}, tables {TABLES or 'none'}"
                     + (f"; a copy of {OPEN} at ply {OPEN_PLY}" if OPEN else ""))
        self.draw()
        if OPEN:
            self.open_copy(OPEN, OPEN_PLY)
        self.root.after(500, self.study)   # games asked for and left unstudied last time
        self.tick()

    # ---- the board ----

    def shown(self):
        """The board on screen: the game's, or the loaded game's at the ply chosen."""
        if self.game and not self.game.over:
            return self.game.board
        if self.review is not None:
            board = self.review.board()
            for move in list(self.review.mainline_moves())[:self.review_ply]:
                board.push(move)
            return board
        return self.game.board if self.game else chess.Board()

    def xy(self, square):
        file, rank = chess.square_file(square), chess.square_rank(square)
        if self.flipped:
            return (7 - file) * SQUARE, rank * SQUARE
        return file * SQUARE, (7 - rank) * SQUARE

    def draw(self):
        board, c = self.shown(), self.canvas
        c.delete("all")
        last = board.move_stack[-1] if board.move_stack else None
        targets = set()
        if self.selected is not None and self.playing():
            targets = {m.to_square for m in board.legal_moves if m.from_square == self.selected}
        for square in chess.SQUARES:
            x, y = self.xy(square)
            light = (chess.square_file(square) + chess.square_rank(square)) % 2 == 1
            colour = LIGHT if light else DARK
            if last and square in (last.from_square, last.to_square):
                colour = LAST_LIGHT if light else LAST_DARK
            if square == self.selected:
                colour = SELECTED
            c.create_rectangle(x, y, x + SQUARE, y + SQUARE, fill=colour, width=0)
            piece = board.piece_at(square)
            if piece and piece.piece_type == chess.KING and board.is_check() and piece.color == board.turn:
                c.create_oval(x + 4, y + 4, x + SQUARE - 4, y + SQUARE - 4, fill=CHECK_RED, width=0)
            if piece:
                cx, cy = x + SQUARE // 2, y + SQUARE // 2 + 2
                if piece.color == chess.WHITE:
                    c.create_text(cx, cy, text=SOLID[piece.piece_type], font=PIECE_FONT, fill="white")
                    c.create_text(cx, cy, text=OUTLINE[piece.piece_type], font=PIECE_FONT, fill="black")
                else:
                    c.create_text(cx, cy, text=SOLID[piece.piece_type], font=PIECE_FONT, fill="black")
            if square in targets:
                r = 9 if not piece else 28
                c.create_oval(x + SQUARE // 2 - r, y + SQUARE // 2 - r, x + SQUARE // 2 + r, y + SQUARE // 2 + r,
                              outline=DOT, width=3 if piece else 0, fill="" if piece else DOT)
        for i in range(8):
            file_square = chess.square(i, 0 if not self.flipped else 7)
            x, _ = self.xy(file_square)
            c.create_text(x + SQUARE - 6, 8 * SQUARE - 7, text="abcdefgh"[i], font=("Segoe UI", 8), fill="#333")
            rank_square = chess.square(0 if not self.flipped else 7, i)
            _, y = self.xy(rank_square)
            c.create_text(6, y + 8, text=str(i + 1), font=("Segoe UI", 8), fill="#333")
        self.draw_moves()
        self.draw_clocks()
        playing = self.playing()
        self.resign_button.config(state="normal" if playing else "disabled")
        self.take_over_button.config(state="normal" if self.review is not None and not playing else "disabled")
        self.learn_button.config(state="normal" if self.review_name and not playing else "disabled")

    def draw_moves(self):
        if self.game and not self.game.over:
            board, start, marked = self.game.board, self.game.start_ply, len(self.game.board.move_stack)
        elif self.review is not None:
            board = self.review.end().board()
            start = int(self.review.headers.get("TakeOverPly", "0") or 0)
            marked = self.review_ply
        else:
            board, start, marked = chess.Board(), 0, 0
        lines = san_lines(board)
        self.line_plies = [first for _, first in lines]
        self.moves.delete(0, "end")
        for i, (text, first) in enumerate(lines):
            self.moves.insert("end", text)
            if first + 1 < start:
                self.moves.itemconfig(i, fg="#888")   # the real game's moves, before a take-over
        if lines and marked:
            row = max(i for i, first in enumerate(self.line_plies) if first < marked)
            self.moves.selection_clear(0, "end")
            self.moves.selection_set(row)
            self.moves.see(row)

    def draw_clocks(self):
        top, bottom = (chess.WHITE, chess.BLACK) if self.flipped else (chess.BLACK, chess.WHITE)
        for label, colour in ((self.top_clock, top), (self.bottom_clock, bottom)):
            name = "White" if colour == chess.WHITE else "Black"
            g = self.game
            if g is not None and (not g.over or self.review is g.game):
                who = "you" if colour == g.human else "bot"
                running = g.clock is not None and g.clock.running == colour
                text = f"{name} ({who})"
                if g.clock:
                    text += "  " + homegame.clock_text(g.clock.remaining(colour))
                label.config(text=text, fg="#000" if running or not g.clock else "#666")
            else:
                label.config(text=name, fg="#666")

    def playing(self):
        return self.game is not None and not self.game.over

    def flip(self):
        self.flipped = not self.flipped
        self.draw()

    # ---- moving ----

    def square_at(self, x, y):
        file, row = x // SQUARE, y // SQUARE
        if not (0 <= file < 8 and 0 <= row < 8):
            return None
        return chess.square(7 - file, row) if self.flipped else chess.square(file, 7 - row)

    def click(self, event):
        if not self.playing() or not self.game.human_to_move():
            return
        square, board = self.square_at(event.x, event.y), self.game.board
        if square is None:
            return
        if self.selected is not None:
            moves = [m for m in board.legal_moves if m.from_square == self.selected and m.to_square == square]
            if moves:
                move = moves[0]
                if len(moves) > 1:   # a promotion: which piece
                    piece = self.ask_promotion()
                    if piece is None:
                        return
                    move = next(m for m in moves if m.promotion == piece)
                self.selected = None
                if self.game.human_move(move):
                    self.after_move()
                else:
                    self.draw()
                return
        piece = board.piece_at(square)
        self.selected = square if piece and piece.color == board.turn else None
        self.draw()

    def ask_promotion(self):
        top = tk.Toplevel(self.root)
        top.title("Promote to")
        top.transient(self.root)
        chosen = []
        for piece, name in ((chess.QUEEN, "Queen"), (chess.ROOK, "Rook"), (chess.BISHOP, "Bishop"),
                            (chess.KNIGHT, "Knight")):
            tk.Button(top, text=f"{SOLID[piece]}  {name}", font=("Segoe UI Symbol", 14), width=10,
                      command=lambda p=piece: (chosen.append(p), top.destroy())).pack(padx=10, pady=3)
        top.grab_set()
        top.wait_window()
        return chosen[0] if chosen else None

    def after_move(self):
        self.draw()
        if self.game.over:
            self.end_game()
        elif self.game.board.turn == self.game.bot:
            self.bot_turn()

    def bot_turn(self):
        game = self.game
        move, book = homegame.book_move(game.board, homegame.book_paths())
        if move:
            self.say(f"The bot is playing from its book ({os.path.basename(book)}).")
            self.root.after(400, lambda: self.bot_done(game, move, {}, book))
            return
        self.say("The bot is thinking...")
        board, limit, engine = game.board.copy(), game.bot_limit(), self.engine

        def think():
            try:
                analysis = engine.analysis(board, limit)
                self.thinking = analysis
                best = analysis.wait()
                info = analysis.info
                self.events.put(lambda: self.bot_done(game, best.move, info, None))
            except Exception as error:   # the engine died, or was closed under it
                self.events.put(lambda: self.engine_failed(game, error))
            finally:
                self.thinking = None

        threading.Thread(target=think, daemon=True).start()

    def bot_done(self, game, move, info, book):
        if game is not self.game or game.over or move is None:
            return
        san = game.board.san(move)
        game.bot_played(move, score=info.get("score"), depth=info.get("depth"), book=book)
        if book:
            self.say(f"The bot played {san}, from its book ({os.path.basename(book)}). Your move.")
        elif info.get("score") is not None:
            self.say(f"The bot played {san}. Its view: {self.view(info['score'], game.human)} "
                     f"(depth {info.get('depth', '?')}). Your move.")
        else:
            self.say(f"The bot played {san}. Your move.")
        self.after_move()

    def view(self, score, you):
        mine = score.pov(you)
        if mine.is_mate():
            return f"mate in {abs(mine.mate())} for {'you' if mine.mate() > 0 else 'the bot'}"
        return f"{mine.score() / 100:+.1f} pawns for you"

    def engine_failed(self, game, error):
        homegame.log(f"the engine failed: {error!r}")
        if self.closing or game is not self.game or game.over:
            return
        self.say("The engine stopped; starting it again...")
        self.engine = None
        self.with_engine(lambda: self.bot_turn())

    # ---- games ----

    def with_engine(self, then):
        """Starts the engine if it isn't running with today's setup (a worker thread), then calls `then`."""
        if not os.path.exists(ENGINE):
            messagebox.showerror("No engine", f"No engine at {ENGINE}.\n\nStart this with bot\\play.cmd: it builds it.")
            return
        free = homegame.free_commit_mb()
        if self.engine is None and free is not None and free < homegame.NEED_MB:
            messagebox.showerror("Not enough memory",
                                 f"Only {free / 1024:.1f} GB of memory is free. The engine takes ~1 GB, and 2 GB is "
                                 "kept free so the bot can't run short. Close something, or try later.")
            return
        self.say("Checking what's running (the Lichess bot? a match?)...")
        self.root.update_idletasks()
        setup = homegame.setup_for(homegame.processes())
        if setup.warning and not messagebox.askyesno("Something's running", setup.warning + "\n\nPlay anyway?"):
            self.say("Not started.")
            return
        self.setup = setup
        self.footer_text()
        if self.engine is not None and self.engine_setup == (setup.threads, setup.below_normal, setup.table_cache):
            then()
            return
        old, self.engine = self.engine, None
        self.say("Starting the engine (a few seconds)...")

        def start():
            try:
                if old is not None:
                    old.quit()
                engine = homegame.start_engine(setup, ENGINE, EVAL, TABLES)
                engine.ping()
                self.events.put(lambda: self.engine_ready(engine, setup, then))
            except Exception as error:
                self.events.put(lambda: messagebox.showerror("The engine didn't start", repr(error)))

        threading.Thread(target=start, daemon=True).start()

    def engine_ready(self, engine, setup, then):
        if self.closing:
            engine.quit()
            return
        self.engine, self.engine_setup = engine, (setup.threads, setup.below_normal, setup.table_cache)
        then()

    def clock_choice(self, value):
        return dict(CLOCKS)[value]

    def new_game(self):
        if self.playing() and not messagebox.askyesno("New game", "Resign this game and start a new one?"):
            return
        choice = Choice(self.root, "New game", "", {
            "colour": ("You play", ["White", "Black", "Either (at random)"], "White"),
            "clock": ("Clock (each side)", [name for name, _ in CLOCKS], "10+5")}).result
        if not choice:
            return
        if self.playing():
            self.resign()
        colour = {"White": chess.WHITE, "Black": chess.BLACK}.get(choice["colour"], random.choice([chess.WHITE,
                                                                                                   chess.BLACK]))
        clock = self.clock_choice(choice["clock"])
        self.with_engine(lambda: self.begin(colour, clock))

    def begin(self, colour, clock, source=None, ply=None, note=""):
        self.game = homegame.HomeGame(colour, clock=clock, setup=self.setup, evaluation_name=EVAL_NAME,
                                      source=source, take_over_ply=ply)
        self.review, self.review_name, self.selected = None, None, None
        self.flipped = colour == chess.BLACK
        homegame.log(f"game started: {os.path.basename(self.game.path)}" + (f" ({note})" if note else ""))
        self.say((note + " " if note else "") + ("Your move." if self.game.human_to_move() else ""))
        self.draw()
        if not self.game.human_to_move():
            self.bot_turn()

    def resign(self):
        if not self.playing():
            return
        if self.thinking is not None:
            try:
                self.thinking.stop()
            except Exception:
                pass
        self.game.resign()
        self.end_game()

    def end_game(self):
        game = self.game
        self.selected = None
        self.review, self.review_ply = game.game, len(game.board.move_stack)
        self.review_name = os.path.basename(game.path)
        self.say(f"{game.describe_result()} Saved as {self.review_name}. If it's one to learn from, press Learn from "
                 "this game. Or step back through it, and take over from any move to try again.")
        homegame.log(f"game over: {self.review_name}: {game.result} ({game.termination})")
        self.draw()

    # ---- loading and taking over ----

    def load_game(self):
        if self.playing():
            messagebox.showinfo("Load game", "Finish or resign this game first.")
            return
        top = tk.Toplevel(self.root)
        top.title("Load a game")
        top.transient(self.root)
        picked = []
        for text, folder in (("One of the bot's Lichess games...", os.path.join(HERE, "games")),
                             ("One of your home games...", homegame.GAMES), ("A PGN file anywhere...", None)):
            tk.Button(top, text=text, width=34,
                      command=lambda f=folder: (picked.append(("file", f)), top.destroy())).pack(padx=12, pady=3)
        tk.Button(top, text="Paste a game (PGN text)...", width=34,
                  command=lambda: (picked.append(("paste", None)), top.destroy())).pack(padx=12, pady=(3, 12))
        top.grab_set()
        top.wait_window()
        if not picked:
            return
        kind, folder = picked[0]
        if kind == "file":
            path = filedialog.askopenfilename(parent=self.root, title="Open a game",
                                              initialdir=folder if folder and os.path.isdir(folder) else HERE,
                                              filetypes=[("Chess games", "*.pgn"), ("All files", "*.*")])
            if not path:
                return
            try:
                game = learn.read_game(path)
            except OSError as error:
                messagebox.showerror("Load game", str(error))
                return
        else:
            text = self.ask_text("Paste a game", "Paste the game's PGN (on Lichess: Share & export, then copy the "
                                                 "PGN):")
            if not text:
                return
            game = chess.pgn.read_game(io.StringIO(text))
        if game is None or (not list(game.mainline_moves()) and "FEN" not in game.headers):
            messagebox.showerror("Load game", "That doesn't read as a game with moves in it.")
            return
        if game.errors:
            messagebox.showwarning("Load game", f"Read with {len(game.errors)} problem(s); the moves up to the first "
                                                f"one are kept:\n{game.errors[0]}")
        self.game, self.review, self.review_ply, self.selected = None, game, 0, None
        home = kind == "file" and os.path.dirname(os.path.abspath(path)).lower() == os.path.abspath(homegame.GAMES).lower()
        self.review_name = os.path.basename(path) if home else None
        h = game.headers
        self.flipped = homegame.black_at_bottom(h)
        self.say(f"{h.get('White', '?')} v {h.get('Black', '?')}, {h.get('Date', '?')}: {h.get('Result', '*')}"
                 f"{', ' + h['Termination'] if h.get('Termination') else ''}. Step through it with the arrows, "
                 "then Take over from here.")
        self.draw()

    def ask_text(self, title, prompt):
        top = tk.Toplevel(self.root)
        top.title(title)
        top.transient(self.root)
        tk.Label(top, text=prompt, wraplength=420, justify="left").pack(anchor="w", padx=10, pady=(10, 4))
        box = tk.Text(top, width=70, height=16)
        box.pack(padx=10)
        try:
            box.insert("1.0", self.root.clipboard_get())
        except tk.TclError:
            pass
        result = []
        tk.Button(top, text="Load", width=10,
                  command=lambda: (result.append(box.get("1.0", "end")), top.destroy())).pack(pady=8)
        top.grab_set()
        top.wait_window()
        return result[0].strip() if result else ""

    def step(self, how):
        if self.review is None or self.playing():
            return
        total = len(list(self.review.mainline_moves()))
        if how == "start":
            self.review_ply = 0
        elif how == "end":
            self.review_ply = total
        else:
            self.review_ply = max(0, min(total, self.review_ply + how))
        self.draw()

    def pick_move(self, event):
        if self.review is None or self.playing() or not self.moves.curselection():
            return
        # A line holds White's move and Black's: clicking it shows the position after its last move.
        row = self.moves.curselection()[0]
        total = len(list(self.review.mainline_moves()))
        self.review_ply = self.line_plies[row + 1] if row + 1 < len(self.line_plies) else total
        self.draw()

    def take_over(self):
        if self.review is None or self.playing():
            return
        board = self.shown()
        if board.is_game_over():
            messagebox.showinfo("Take over", "The game is already over at this move: step back first.")
            return
        to_move = "White" if board.turn == chess.WHITE else "Black"
        mover = self.review.headers.get(to_move) or to_move
        choice = Choice(self.root, "Take over from here",
                        f"From this position (move {board.fullmove_number}, {to_move} to move: {mover} in the real "
                        "game), you play one side and the bot the other. First it says what it makes of the "
                        "position.", {
                            "colour": ("You play", ["White", "Black"], to_move),
                            "clock": ("Clock (each side, fresh)", [name for name, _ in CLOCKS], "10+5")}).result
        if not choice:
            return
        colour = chess.WHITE if choice["colour"] == "White" else chess.BLACK
        clock, source, ply = self.clock_choice(choice["clock"]), self.review, self.review_ply
        self.with_engine(lambda: self.judge(board, colour, clock, source, ply))

    def judge(self, board, colour, clock, source, ply):
        """The bot's view of the position (VERDICT_SECONDS), then the game, if you still want it."""
        self.say(f"The bot is looking at the position ({VERDICT_SECONDS} s)...")
        engine, position = self.engine, board.copy()

        def look():
            try:
                info = engine.analyse(position, chess.engine.Limit(time=VERDICT_SECONDS))
                self.events.put(lambda: self.judged(info, board, colour, clock, source, ply))
            except Exception as error:
                self.events.put(lambda: messagebox.showerror("The engine failed", repr(error)))

        threading.Thread(target=look, daemon=True).start()

    def judged(self, info, board, colour, clock, source, ply):
        text = homegame.verdict(info["score"], colour, len(board.piece_map()), bool(TABLES), VERDICT_SECONDS)
        if not messagebox.askyesno("Take over", text + "\n\nPlay it out?"):
            self.say(text)
            return
        note = text + (" (Past the bot's first 12 moves: a game to play, not one it can learn openings from.)"
                       if ply > learn.BOOK_PLIES else "")
        self.begin(colour, clock, source=source, ply=ply, note=note)

    # ---- learning ----

    def learn_this(self):
        """The Learn button: the home game shown goes on the list to learn from (bot\\home\\to-learn.txt), and the
        study starts. Nothing is studied unless asked for (Matthew, 2026-10-23: he plays many test games)."""
        name = self.review_name
        if not name or self.playing():
            return
        good, why = homegame.learn_state(name, self.review)
        if not good:
            messagebox.showinfo("Learn from this game", f"Not this one: {why}.")
            return
        homegame.ask_to_learn(name)
        homegame.log(f"asked to learn from {name}")
        self.learned(f"{name}: asked for.")
        self.study()

    def study(self):
        """Studies the home games asked for and not yet studied, on an engine of its own (1 thread, below normal,
        no tables, Hash 64), in a worker thread; you can play on meanwhile. One window studies at a time (the lock):
        it goes on until none is left, so games asked for in another window are studied too."""
        if self.studying:
            self.study_again = True
            return
        exclude = [self.game.path] if self.playing() else []
        if not homegame.pending(exclude=exclude) or not os.path.exists(ENGINE):
            return
        free = homegame.free_commit_mb()
        if free is not None and free < 2048 + 400:
            self.learned("Not enough memory free to study now; the game waits for the next start.")
            return
        if not homegame.lock_study():
            self.learned("Another window is studying; it takes this game too when it's done.")
            return
        self.studying, self.study_again = True, False
        self.learned("Studying in the background (a few minutes; you can play on)...")

        def run():
            engine = None
            try:
                engine = self.study_engine = homegame.start_engine(homegame.Setup(1, True, 64, "study", hash_mb=64),
                                                                   ENGINE, EVAL, None)
                while not self.closing and homegame.pending(exclude=exclude):
                    homegame.study_pending(engine, say=self.study_said, exclude=exclude, think=THINK, check=CHECK,
                                           evaluation_name=EVAL_NAME)
            except Exception as error:
                if not self.closing:
                    homegame.log(f"the study stopped: {error!r}")
                    self.events.put(lambda: self.learned("The study stopped (the game is studied next time)."))
            finally:
                if engine is not None:
                    try:
                        engine.quit()
                    except Exception:
                        pass
                homegame.unlock_study()
                self.events.put(self.study_finished)

        threading.Thread(target=run, daemon=True).start()

    def study_said(self, text):
        homegame.log(text)
        self.events.put(lambda: self.learned(text))

    def study_finished(self):
        self.studying = False
        if self.study_again and not self.closing:
            self.study()

    def learned(self, text):
        self.learning.config(text="Learning: " + text)

    # ---- a copy in a new window ----

    def copy_to_window(self):
        """Opens a second window on a copy of the game on screen, at the position shown, for trying things out:
        take over there, step through it. It has its own engine (started when you take over there); this window's
        game goes on as it was, its clocks included. The copy is a file in bot\\home\\copies."""
        if self.playing():
            game, ply = self.game.game, len(self.game.board.move_stack)
        elif self.review is not None:
            game, ply = self.review, self.review_ply
        else:
            messagebox.showinfo("Copy to a new window", "Start or load a game first.")
            return
        path = homegame.copy_path()
        with open(path, "w", encoding="utf-8", newline="\n") as out:
            out.write(str(game) + "\n")
        args = [sys.executable, "-I", os.path.abspath(__file__), "--open", path, "--ply", str(ply)]
        for name in ("--engine", "--eval", "--tables", "--think", "--check"):
            if name in sys.argv:
                args += [name, sys.argv[sys.argv.index(name) + 1]]
        try:
            subprocess.Popen(args, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                             creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        except OSError as error:
            messagebox.showerror("Copy to a new window", repr(error))
            return
        homegame.log(f"copied to a new window: {os.path.basename(path)} at ply {ply}")
        self.say("A copy is opening in a new window, at this position." +
                 (" This game's clocks are still running." if self.playing() and self.game.clock else ""))

    def open_copy(self, path, ply):
        """--open: this window shows a copied game (copy_to_window), at the ply it was copied at."""
        try:
            game = learn.read_game(path)
        except OSError as error:
            self.say(f"The copy couldn't be read: {error}")
            return
        if game is None:
            self.say("The copy couldn't be read.")
            return
        total = len(list(game.mainline_moves()))
        self.review, self.review_ply, self.review_name = game, max(0, min(total, ply if ply is not None else total)), None
        self.flipped = homegame.black_at_bottom(game.headers)
        h = game.headers
        self.root.title("Play the bot (a copy)")
        self.say(f"A copy of {h.get('White', '?')} v {h.get('Black', '?')}, at move {self.shown().fullmove_number}. "
                 "Take over from here to play on from it, or step through it. The first window's game goes on as it "
                 "was.")
        self.draw()

    # ---- the rest ----

    def say(self, text):
        self.status.config(text=text)

    def footer_text(self):
        size = len(homegame.home_rows())
        where = self.setup.note if self.setup else "its threads are chosen when a game starts (2 if the Lichess bot is playing)"
        self.footer.config(text=f"The bot: {EVAL_NAME}, endgame tables {'on' if TABLES else 'off'}, books "
                                f"{', '.join(os.path.basename(p) for p in homegame.book_paths()) or 'none'} "
                                f"(home book: {size} learnt); {where}.")

    def tick(self):
        while True:
            try:
                self.events.get_nowait()()
            except queue.Empty:
                break
        if self.playing():
            if self.game.check_flag():
                if self.thinking is not None:
                    try:
                        self.thinking.stop()
                    except Exception:
                        pass
                self.end_game()
            else:
                self.draw_clocks()
        if not self.closing:
            self.root.after(100, self.tick)

    def close(self):
        if self.playing():
            if not messagebox.askyesno("Leave", "Leave this game? It's saved unfinished, and isn't learnt from."):
                return
            if self.thinking is not None:
                try:
                    self.thinking.stop()
                except Exception:
                    pass
            self.game.abandon()
            homegame.log(f"game left unfinished: {os.path.basename(self.game.path)}")
        self.closing = True
        for engine in (self.engine, self.study_engine):
            if engine is not None:
                try:
                    engine.quit()   # (a study under way stops; its game is studied next time)
                except Exception:
                    pass
        homegame.unlock_study()
        self.root.destroy()


def main():
    root = tk.Tk()

    def report(kind, value, trace):
        # A copied window has no console: an error goes to the log and on screen, not to a print nobody sees.
        homegame.log("error in the window:\n" + "".join(traceback.format_exception(kind, value, trace)))
        messagebox.showerror("Something went wrong", f"{value!r}\n\nThe details are in bot\\logs\\play-<date>.log.")

    root.report_callback_exception = report
    App(root)
    root.mainloop()


if __name__ == "__main__":
    main()
