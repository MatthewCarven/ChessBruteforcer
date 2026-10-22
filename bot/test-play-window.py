"""A smoke test of the window (bot/play.py): it opens on screen for a minute or two and plays itself.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I bot\\test-play-window.py --engine <exe> [--eval <net>] [--tables <dir>]
                                                                        [--books <dir>] [--load <pgn>] [--shots <dir>]
    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I bot\\test-play-window.py --picker [--file <pgn of many games>]
                                   (only the Load game list, off screen, no engine: fine beside a match)

Its games, lists, copies and home book go to a temporary folder (bot\\home and bot\\books are never written;
--books is read only). Dialogs answer themselves (yes to everything). It plays 12 random moves for you against the
bot (1-minute clock) and copies the game to a new window mid-game: the second window (a real one, for a few
seconds) must start on the copy, and is then closed. It resigns, checks nothing was studied unasked, and presses
Learn from this game (the study runs with --think 1 --check 0.5). Then it opens the copy here as the second window
does, loads a game (--load, else the first game), steps to ply 20, takes over as the side to move, plays 4 moves,
resigns, and presses Learn again (too short: it must say so). It waits for the study before closing. With --shots,
a PNG of the window at three points (PowerShell's screen copy). **A screen copy takes whatever is on top**: with
Matthew at the laptop, that's his own windows, not this one, so use --shots only when he's away (2026-10-22: one
caught his browser; deleted). Exit 1 if anything didn't happen as it should.
"""
import importlib.util
import os
import random
import subprocess
import sys
import tempfile
import time

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
homegame.GAMES, homegame.COPIES = os.path.join(temp, "games"), os.path.join(temp, "copies")
homegame.STUDIED, homegame.TO_LEARN = os.path.join(temp, "studied.txt"), os.path.join(temp, "to-learn.txt")
homegame.STUDY_LOCK = os.path.join(temp, "study.lock")
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


def logged(text):
    """Whether today's play log (the one both windows write) has `text` in it."""
    path = os.path.join(HERE, "logs", time.strftime("play-%Y-%m-%d.log"))
    return os.path.exists(path) and text in open(path, encoding="utf-8", errors="replace").read()


def script(app):
    app.with_engine(lambda: app.begin(chess.WHITE, (60, 1)))
    yield from wait_until(app, lambda: app.game is not None)
    expect("a game started, you White", app.game is not None and app.game.human == chess.WHITE)
    yield from random_moves(app, 12)
    yield from wait_until(app, lambda: not app.playing() or app.game.human_to_move())
    expect("the bot answered (24 plies, or the game ended)", len(app.game.board.move_stack) >= 24 or app.game.over)
    shot(app, "1-playing")

    # Copy to a new window, mid-game: a real second window, which must start on the copy.
    spawned, real_popen = [], play.subprocess.Popen
    play.subprocess.Popen = lambda args, **kw: spawned.append((args, real_popen(args, **kw))) or spawned[-1][1]
    plies = len(app.game.board.move_stack)
    app.copy_to_window()
    play.subprocess.Popen = real_popen
    copies = os.listdir(homegame.COPIES) if os.path.isdir(homegame.COPIES) else []
    expect("a copy written to the copies folder", len(copies) == 1)
    args, child = spawned[0] if spawned else ([], None)
    copy = os.path.join(homegame.COPIES, copies[0]) if copies else ""
    at = args.index("--open") if "--open" in args else len(args)
    expect("the new window is play.py on the copy, at the ply on screen",
           args[at:at + 4] == ["--open", copy, "--ply", str(plies)] and args[2].endswith("play.py"))
    yield from wait_until(app, lambda: logged(f"a copy of {copy} at ply {plies}"), 30000)
    expect("the new window started on it (its line in the log)", logged(f"a copy of {copy} at ply {plies}"))
    if child is not None:   # the venv's python.exe is a launcher with the real interpreter under it: the tree
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(child.pid)], capture_output=True)
    expect("the game here goes on", app.playing() or app.game.over)

    app.resign()
    expect("resigned: 0-1", app.game.result == "0-1")
    first = app.game.path
    expect("the game is saved", os.path.exists(first))
    yield 1500
    expect("nothing studied unasked", (app.studying, homegame.asked(), homegame.studied()) == (False, [], set()))
    expect("the Learn button is on for the game just played", str(app.learn_button.cget("state")) == "normal")
    app.learn_this()
    short = any(s.startswith("[Learn from this game] Not this one") for s in said)
    expect("Learn: asked for (or told why not, if the game ended short)",
           homegame.asked() == [os.path.basename(first)] or short)

    # The copy, opened here as the second window opens it.
    app.game = None
    app.open_copy(copy, plies)
    expect("the copy opens at its ply, not a home game (no Learn)",
           (app.review_ply, app.review_name, str(app.learn_button.cget("state"))) == (plies, None, "disabled"))
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
    app.learn_this()
    expect("Learn on the short take-over: told why not",
           any("[Learn from this game] Not this one: it was short" in s for s in said))
    yield from wait_until(app, lambda: not app.studying and homegame.pending() == [], 240000)
    expect("the game asked for is studied, and only it",
           homegame.pending() == [] and homegame.studied() == set(homegame.asked()))
    expect("the study lock is let go", not os.path.exists(homegame.STUDY_LOCK))
    print("learning line:", app.learning.cget("text"), flush=True)
    app.close()


def picker_check():
    """--picker [--file <pgn>]: only the list of games (Load game, a file of many), off screen, no engine. The file
    defaults to two games made up here; Lichess's export of the account (600-odd games) is the real test."""
    path = option("--file")
    text = homegame.read_text(path) if path else (
        '[White "RustBot2003"]\n[Black "matthewcarven"]\n[Result "1-0"]\n\n1. e4 1-0\n\n'
        '[White "matthewcarven"]\n[Black "entebot"]\n[Result "0-1"]\n\n1. d4 0-1\n')
    started = time.perf_counter()
    index = homegame.game_index(text)
    rows = [homegame.game_row(headers) for _, headers in index]
    print(f"     ({len(rows)} games indexed in {time.perf_counter() - started:.2f} s)")
    root = tk.Tk()
    root.withdraw()
    picker = play.GamePicker(root, rows, wait=False)
    picker.withdraw()   # (off screen: Matthew may be at the laptop)
    picker.update()
    expect("the list holds every game", picker.count.cget("text") == f"{len(rows)} of {len(rows)}")
    expect("the newest (the file's first) is chosen to start with", picker.tree.selection() == ("0",))
    picker.find.set("RUSTBOT")
    wanted = [n for n, row in enumerate(rows) if "rustbot" in " ".join(row).lower()]
    expect("Find narrows it, any case", len(picker.tree.get_children()) == len(wanted) > 0)
    picker.find.set("rustbot 1-0")
    both = [n for n, row in enumerate(rows) if "rustbot" in " ".join(row).lower() and "1-0" in " ".join(row)]
    expect("every word typed must match", len(picker.tree.get_children()) == len(both))
    picker.find.set("no such player anywhere")
    picker.ok()
    expect("nothing shown: Open does nothing", picker.result is None and picker.winfo_exists())
    picker.find.set("rustbot")
    last = picker.tree.get_children()[-1]
    picker.tree.selection_set(last)
    picker.ok()
    expect("Open: the game's number in the file", picker.result == int(last) == wanted[-1])
    game = homegame.game_at(text, index[picker.result][0])
    expect("... which reads as that game", game.headers.get("White") == index[picker.result][1].get("White")
           and game.headers.get("Black") == index[picker.result][1].get("Black"))
    root.destroy()
    print("all passed" if not failures else f"{len(failures)} FAILED: {', '.join(failures)}")
    sys.exit(1 if failures else 0)


def main():
    if "--picker" in sys.argv:
        picker_check()
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
