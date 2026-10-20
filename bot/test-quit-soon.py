"""A test for bot/lichess-bot-quit-soon.patch: with bot\\quit.soon there, lichess-bot takes no new games, ends the games
still on with Matthew's goodbye, and stops once none is left.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe bot\\test-quit-soon.py

2026-10-20, Matthew: a flag "we can drop in the folder bot called quit.soon and if the file exists gracefully refuse any
challenges and exit when all existing games are finished", the bot saying something like "Sorry cannot accept any new
challenges at this time! We're going to go to sleep and when we wake up we will be even better!" A decline carries only
one of lichess.org's own reasons, so a challenger gets "later"; his line is the goodbye in the games it finishes.

Uses lichess-bot's own pretend lichess.org (test_bot/lichess.py: no network, no token) and its opponent (white, against
lichess-bot's homemade test engine), at concurrency 2 as the bot plays. While the game is on, a human's challenge comes
(the game waits for it). Three runs, each in a process of its own:
  quit     quit.soon there from the start: the game is played and ends with Matthew's goodbye, the challenge is
           declined "later", and lichess-bot stops by itself once the game is over.
  control  no quit.soon: the challenge is accepted, the goodbye is "Good game!", and lichess-bot carries on until this
           test stops it.
  idle     quit.soon there and no game: lichess-bot stops at once.
The lichess-bot tested is bot\\lichess-bot, or the folder in LICHESS_BOT (a copy, so a running bot is left alone).
"""
import json
import os
import subprocess
import sys
import threading
import time
import types

HERE = os.path.dirname(os.path.abspath(__file__))
BOT = os.environ.get("LICHESS_BOT") or os.path.join(HERE, "lichess-bot")
os.chdir(BOT)
sys.path.insert(0, BOT)
sys.modules.setdefault("pytest", types.ModuleType("pytest"))   # test_bot.test_bot refuses to load without it

import logging                                                  # noqa: E402
import multiprocessing                                          # noqa: E402
import tempfile                                                 # noqa: E402
from multiprocessing import Manager                             # noqa: E402
from queue import Empty                                         # noqa: E402
import yaml                                                     # noqa: E402
import test_bot.lichess as pretend                              # noqa: E402
from lib import config, lichess_bot                             # noqa: E402
from lib.engine_wrapper import test_suffix                      # noqa: E402

CHALLENGE = {"type": "challenge", "challenge": {
    "id": "chall001", "url": "https://lichess.org/chall001", "status": "created",
    "challenger": {"id": "newcomer", "name": "Newcomer", "rating": 2000, "title": None, "online": True},
    "destUser": {"id": "b", "name": "b", "rating": 2200, "title": "BOT", "online": True},
    "variant": {"key": "standard", "name": "Standard", "short": "Std"}, "rated": False, "speed": "bullet",
    "timeControl": {"type": "clock", "limit": 60, "increment": 1, "show": "1+1"},
    "color": "random", "finalColor": "white", "perf": {"icon": "", "name": "Bullet"}}}


class OneLine(pretend.EventStream):
    """An event stream with one event in it, then closed (lichess-bot opens the next)."""

    def __init__(self, line: bytes) -> None:
        super().__init__(True)
        self.line = line

    def iter_lines(self, *args, **kwargs):
        yield self.line


class HeldGameStream(pretend.GameStream):
    """The game, held after its first line until the challenge has been out 2 s, so it comes while the game is on."""

    def __init__(self, board_queue, clock_queue, game_on, sent) -> None:
        super().__init__(board_queue, clock_queue)
        self.game_on = game_on
        self.sent = sent

    def iter_lines(self, *args, **kwargs):
        lines = super().iter_lines(*args, **kwargs)
        yield next(lines)          # the full game
        self.game_on.set()
        self.sent.wait(30)
        time.sleep(2)              # for lichess-bot to act on the challenge
        yield from lines


class PretendLichess(pretend.Lichess):
    """The pretend lichess.org, with a game (or none), a challenge once the game is on, and what lichess-bot did with
    them recorded in queues shared between its processes."""

    def __init__(self, *args, with_game, game_on, sent, declines, accepts, chats) -> None:
        super().__init__(*args)
        self.with_game = with_game
        self.game_on = game_on
        self.sent = sent
        self.declines = declines
        self.accepts = accepts
        self.chats = chats
        self.events = 0

    def get_event_stream(self) -> pretend.EventStream:
        self.events += 1
        if self.with_game and self.events == 1:
            return pretend.EventStream(False)                  # gameStart
        if self.game_on.is_set() and not self.sent.is_set():
            self.sent.set()
            return OneLine(json.dumps(CHALLENGE).encode("utf-8"))
        return pretend.EventStream(True)                       # a keep-alive, then a second's wait

    def get_game_stream(self, game_id: str) -> pretend.GameStream:
        super().get_game_stream(game_id)
        return HeldGameStream(self.board_queue, self.clock_queue, self.game_on, self.sent)

    def accept_challenge(self, challenge_id: str) -> None:
        self.accepts.put(challenge_id)

    def decline_challenge(self, challenge_id: str, reason: str = "generic") -> None:
        self.declines.put([challenge_id, reason])

    def chat(self, game_id: str, room: str, text: str) -> None:
        self.chats.put([room, text])


def drain(queue) -> list:
    items = []
    while True:
        try:
            items.append(queue.get_nowait())
        except Empty:
            return items


def run(arm: str) -> None:
    """One run, in this process: prints RESULT and a JSON line, then ends the process."""
    from test_bot.test_bot import lichess_org_simulator
    temp = tempfile.mkdtemp()
    flag = os.path.join(temp, "quit.soon")
    os.environ["BRUTEFORCER_QUIT_SOON"] = flag    # before lichess-bot starts its processes: they inherit it
    if arm in ("quit", "idle"):
        open(flag, "w").close()
    with open("./config.yml.default") as f:
        raw = yaml.safe_load(f)
    raw["token"] = ""
    raw["engine"]["name"] = f"ScholarsMate{test_suffix}"
    raw["engine"]["protocol"] = "homemade"
    raw["pgn_directory"] = os.path.join(temp, "games")
    raw["challenge"]["concurrency"] = 2
    config.insert_default_values(raw)
    cfg = config.Configuration(raw)
    lichess_bot.logging_configurer(logging.INFO, None, True)
    manager = Manager()
    board_queue, clock_queue, move_queue, results = (manager.Queue() for _ in range(4))
    declines, accepts, chats = (manager.Queue() for _ in range(3))
    li = PretendLichess(move_queue, board_queue, clock_queue, with_game=arm != "idle", game_on=manager.Event(),
                        sent=manager.Event(), declines=declines, accepts=accepts, chats=chats)
    lichess_bot.disable_restart()
    facts = {"arm": arm, "stopped_by": "itself", "game_over_after": None, "won": None}
    began = time.monotonic()
    game_over = threading.Event()

    def finish(code: int) -> None:
        facts["declines"] = drain(declines)
        facts["accepts"] = drain(accepts)
        facts["player_chat"] = [text for room, text in drain(chats) if room == "player"]
        print("RESULT " + json.dumps(facts), flush=True)
        sys.stderr = open(os.devnull, "w")    # threads still waiting on the queues complain as they close
        for child in multiprocessing.active_children():
            child.terminate()
        manager.shutdown()                    # or its server process outlives us, holding the console open
        os._exit(code)

    def watch_game() -> None:
        facts["won"] = results.get(timeout=60)
        facts["game_over_after"] = round(time.monotonic() - began, 1)
        game_over.set()

    def stop_it() -> None:                    # the control run: lichess-bot carries on, so stop it
        if game_over.wait(60):
            time.sleep(4)
        facts["stopped_by"] = "the test"
        lichess_bot.stop.terminated = True

    def give_up() -> None:
        facts["stopped_by"] = "nothing (still running after 40 s)"
        finish(1)

    watchdog = threading.Timer(40, give_up)
    watchdog.daemon = True
    watchdog.start()
    if arm != "idle":
        threading.Thread(target=lichess_org_simulator, args=[move_queue, board_queue, clock_queue, results],
                         daemon=True).start()
        threading.Thread(target=watch_game, daemon=True).start()
    if arm == "control":
        threading.Thread(target=stop_it, daemon=True).start()
    lichess_bot.start(li, li.get_profile(), cfg, logging.INFO, None, True, one_game=False)
    watchdog.cancel()
    facts["ended_after"] = round(time.monotonic() - began, 1)
    finish(0)


def main() -> int:
    goodbye = getattr(lichess_bot, "QUIT_SOON_GOODBYE", None)
    expected = {
        "quit": {"stopped_by": "itself", "won": True, "declines": [["chall001", "later"]], "accepts": [],
                 "goodbye": goodbye},
        "control": {"stopped_by": "the test", "won": True, "declines": [], "accepts": ["chall001"],
                    "goodbye": "Good game!"},
        "idle": {"stopped_by": "itself", "won": None, "declines": [], "accepts": [], "goodbye": None},
    }
    failures = 0
    print("lichess-bot with bot\\quit.soon (concurrency 2, a challenge mid-game):")
    for arm, want in expected.items():
        done = subprocess.run([sys.executable, os.path.abspath(__file__), "--run", arm], capture_output=True,
                              text=True, encoding="utf-8", errors="replace", timeout=120)
        lines = [line for line in done.stdout.splitlines() if line.startswith("RESULT ")]
        if not lines:
            failures += 1
            print(f"  FAIL {arm}: no result (exit {done.returncode}): {done.stderr.strip()[-300:]}")
            continue
        got = json.loads(lines[-1][len("RESULT "):])
        got["goodbye"] = got["player_chat"][-1] if got["player_chat"] else None
        wrong = [f"{key} {got[key]!r} (expected {value!r})" for key, value in want.items() if got[key] != value]
        if arm == "quit" and not wrong and got["ended_after"] - got["game_over_after"] > 15:
            wrong.append(f"stopped {got['ended_after'] - got['game_over_after']:.1f} s after the game, not soon")
        failures += bool(wrong)
        timing = (f"game over at {got['game_over_after']} s, " if got["game_over_after"] else "") + \
            f"lichess-bot ended at {got.get('ended_after', '-')} s, stopped by {got['stopped_by']}"
        print(f"  {'ok  ' if not wrong else 'FAIL'} {arm}: {timing}; declined {got['declines']}, "
              f"accepted {got['accepts']}, goodbye {got['goodbye']!r}")
        for line in wrong:
            print(f"         wrong: {line}")
    print("passed" if not failures else f"FAILED: {failures} run(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--run":
        run(sys.argv[2])
    sys.exit(main())
