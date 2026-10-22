"""A smoke test of the window (bot/play.py): it opens on screen for a minute or two and plays itself.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I bot\\test-play-window.py --engine <exe> [--eval <net>] [--tables <dir>]
                                                                        [--books <dir>] [--load <pgn>] [--shots <dir>]

Its games, studied list and home book go to a temporary folder (bot\\home and bot\\books are never written; --books
is read only). Dialogs answer themselves (yes to everything). It plays 12 random moves for you against the bot
(1-minute clock), resigns, then loads a game (--load, else the first game), steps to ply 20 and takes over as the
side to move, plays 4 moves and resigns; the study of both games runs meanwhile (--think 1 --check 0.5), and it
waits for that before closing the window. With --shots, a PNG of the window at three points (PowerShell's screen
copy). **A screen copy takes whatever is on top**: with Matthew at the laptop, that's his own windows, not this one,
so use --shots only when he's away (2026-10-22: one caught his browser; deleted). Exit 1 if anything didn't
happen as it should.
"""
import importlib.util
import os
import random
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.argv += ["--think", "1", "--check", "0.5"]
spec = importlib.util.spec_from_file_location("play", os.path.join(HERE, "play.py"))
play = importlib.util.module_from_spec(spec)
spec.loader.exec_module(play)
homegame, learn = play.homegame, play.learn
import chess          # noqa: E402  (after play, which checks the arguments)
import tkinter as tk  # noqa: E402


def option(name, default=None):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default


temp = tempfile.mkdtemp(prefix="play-window-")
homegame.GAMES = os.path.join(temp, "games")
homegame.STUDIED = os.path.join(temp, "studied.txt")
homegame.HOME_ROWS, homegame.HOME_BOOK = os.path.join(temp, "home.jsonl"), os.path.join(temp, "home.bin")
learn.BOOKS = option("--books", os.path.join(temp, "no-books"))
said = []
play.messagebox.askyesno = lambda title, text, **k: said.append(f"[{title}] {text}") or True
play.messagebox.showinfo = play.messagebox.showerror = play.messagebox.showwarning = \
    lambda title, text, **k: said.append(f"[{title}] {text}")
shots, rng, failures = option("--shots"), random.Random(5), []


def shot(app, name):
    if not shots:
        return
    app.root.update()
    x, y, w, h = app.root.winfo_rootx(), app.root.winfo_rooty(), app.root.winfo_width(), app.root.winfo_height()
    path = os.path.join(shots, name + ".png")
    script = (f"Add-Type -AssemblyName System.Drawing; $b = New-Object Drawing.Bitmap {w}, {h}; "
              f"$g = [Drawing.Graphics]::FromImage($b); $g.CopyFromScreen({x}, {y}, 0, 0, $b.Size); "
              f"$b.Save('{path}'); $g.Dispose(); $b.Dispose()")
    subprocess.run(["powershell", "-NoProfile", "-Command", script], timeout=60)
    print("shot:", path, flush=True)


def expect(name, ok):
    print(f"{'ok  ' if ok else 'FAIL'} {name}", flush=True)
    if not ok:
        failures.append(name)


def run(app):
    steps = iter(script(app))

    def next_step():
        try:
            wait = next(steps)
        except StopIteration:
            return
        app.root.after(wait, next_step)

    app.root.after(1500, next_step)


def wait_until(app, condition, limit_ms=180000):
    """Yields 250 ms waits until condition() (or the limit)."""
    waited = 0
    while not condition() and waited < limit_ms:
        yield 250
        waited += 250


def random_moves(app, count):
    played = 0
    while played < count and app.playing():
        yield from wait_until(app, lambda: not app.playing() or app.game.human_to_move())
        if not app.playing():
            break
        board = app.game.board
        move = rng.choice(sorted(board.legal_moves, key=lambda m: m.uci()))
        if app.game.human_move(move):
            played += 1
            app.after_move()
        yield 300


def script(app):
    app.with_engine(lambda: app.begin(chess.WHITE, (60, 1)))
    yield from wait_until(app, lambda: app.game is not None)
    expect("a game started, you White", app.game is not None and app.game.human == chess.WHITE)
    yield from random_moves(app, 12)
    yield from wait_until(app, lambda: not app.playing() or app.game.human_to_move())
    expect("the bot answered (24 plies, or the game ended)", len(app.game.board.move_stack) >= 24 or app.game.over)
    shot(app, "1-playing")
    app.resign()
    expect("resigned: 0-1", app.game.result == "0-1")
    first = app.game.path
    expect("the game is saved", os.path.exists(first))
    yield 500
    load = option("--load")
    source = learn.read_game(load) if load else app.review
    app.game, app.review, app.review_ply = None, source, 0
    for _ in range(20):
        app.step(1)
    expect("stepped to ply 20", app.review_ply == min(20, len(list(source.mainline_moves()))))
    app.draw()
    shot(app, "2-loaded")
    board = app.shown()
    app.with_engine(lambda: app.judge(board, board.turn, (120, 2), source, app.review_ply))
    yield from wait_until(app, lambda: app.game is not None)
    expect("taken over: the verdict was given", any("view" in s or "tables" in s for s in said))
    expect("taken over at ply 20", app.game.start_ply == app.review_ply
           and app.game.game.headers.get("TakeOverPly") == str(app.review_ply))
    yield from random_moves(app, 4)
    shot(app, "3-taken-over")
    app.resign()
    yield from wait_until(app, lambda: not app.studying and homegame.pending() == [], 240000)
    expect("both games studied", homegame.pending() == [] and len(homegame.studied()) == 2)
    print("learning line:", app.learning.cget("text"), flush=True)
    app.close()


def main():
    root = tk.Tk()
    app = play.App(root)
    run(app)
    root.mainloop()
    print("\n".join(said))
    print("games:", sorted(os.listdir(homegame.GAMES)), "\nin", temp)
    print("all passed" if not failures else f"{len(failures)} FAILED: {', '.join(failures)}")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
