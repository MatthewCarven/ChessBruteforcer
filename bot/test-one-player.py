"""A test for bot/lichess-bot-one-player.patch: when lichess.org sends gameStart again for a game already being played
(as it does for every ongoing game when the event stream reconnects), lichess-bot doesn't start a second player for it.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe bot\\test-one-player.py

2026-10-19, Ravager_BOT: a connection drop closed both streams. The game's player rejoined its stream (the rejoin
patch), and the event stream's gameStart started a second player for the same game; lichess.org keeps one stream open a
game, so the two closed each other's in turn, ~1.5 a second, until lichess.org rate-limited them and one died.

Uses lichess-bot's own pretend lichess.org (test_bot/lichess.py: no network, no token) and its opponent, which plays
white; the bot under test plays black with lichess-bot's homemade test engine (Scholar's mate). The game's first stream
drops after the opponent's first move, and the event stream sends gameStart twice. Passes when the bot wins with
exactly two game streams opened (the first, and its one rejoin): a second player opens more. The lichess-bot tested
is bot\\lichess-bot, or the folder in the environment variable LICHESS_BOT (a copy, so a running bot is left alone).
"""
import os
import sys
import threading
import types

HERE = os.path.dirname(os.path.abspath(__file__))
BOT = os.environ.get("LICHESS_BOT") or os.path.join(HERE, "lichess-bot")
os.chdir(BOT)
sys.path.insert(0, BOT)
sys.modules.setdefault("pytest", types.ModuleType("pytest"))   # test_bot.test_bot refuses to load without it

import datetime                                                 # noqa: E402
import multiprocessing                                          # noqa: E402
import logging                                                  # noqa: E402
import tempfile                                                 # noqa: E402
import time                                                     # noqa: E402
from multiprocessing import Manager                             # noqa: E402
from queue import Empty, Queue                                  # noqa: E402
import chess                                                    # noqa: E402
import yaml                                                     # noqa: E402
import test_bot.lichess as pretend                              # noqa: E402
from lib import config, lichess_bot                             # noqa: E402
from lib.engine_wrapper import test_suffix                      # noqa: E402

GAME = {"gameId": "zzzzzzzz", "speed": "bullet", "opponent": {"username": "bo"}}


class DroppingGameStream(pretend.GameStream):
    """A game stream that lichess.org closes after the full game and one move, the game still on: once the second
    gameStart is out, so the game can't be over before lichess-bot has had it (or the test would pass by luck)."""

    def __init__(self, board_queue, clock_queue, second_start) -> None:
        super().__init__(board_queue, clock_queue)
        self.second_start = second_start

    def iter_lines(self, *args, **kwargs):
        lines = super().iter_lines(*args, **kwargs)
        yield next(lines)          # the full game
        yield next(lines)          # the opponent's first move
        self.second_start.wait(20)
        time.sleep(2)              # for lichess-bot to act on it
        lines.close()              # ... and the stream ends


class ReconnectingLichess(pretend.Lichess):
    """The pretend lichess.org: each player's first game stream drops, every game stream opened is counted in
    `opened`, and the first two event streams each start with gameStart, the second setting `second_start` (both
    shared between processes)."""

    def __init__(self, *args, opened, second_start) -> None:
        super().__init__(*args)
        self.opened = opened
        self.second_start = second_start
        self.streams = 0
        self.event_streams = 0

    def get_event_stream(self) -> pretend.EventStream:
        self.event_streams += 1
        if self.event_streams == 2:
            self.second_start.set()
        return pretend.EventStream(sent_game=self.event_streams > 2)

    def get_game_stream(self, game_id: str) -> pretend.GameStream:
        stream = super().get_game_stream(game_id)   # from the second on, this has the opponent resend the position
        self.opened.put(1)
        self.streams += 1
        return DroppingGameStream(self.board_queue, self.clock_queue, self.second_start) if self.streams == 1 else stream

    def get_ongoing_games(self) -> list:
        return [dict(GAME)]


def main() -> None:
    from test_bot.test_bot import lichess_org_simulator
    with open("./config.yml.default") as f:
        raw = yaml.safe_load(f)
    with tempfile.TemporaryDirectory() as temp:
        raw["token"] = ""
        raw["engine"]["name"] = f"ScholarsMate{test_suffix}"
        raw["engine"]["protocol"] = "homemade"
        raw["pgn_directory"] = os.path.join(temp, "games")
        config.insert_default_values(raw)
        cfg = config.Configuration(raw)
        lichess_bot.logging_configurer(logging.INFO, None, True)
        manager = Manager()
        board_queue: Queue[chess.Board] = manager.Queue()
        clock_queue: Queue[tuple[datetime.timedelta, ...]] = manager.Queue()
        move_queue: Queue[chess.Move | None] = manager.Queue()
        results: Queue[bool] = manager.Queue()
        opened: Queue[int] = manager.Queue()
        li = ReconnectingLichess(move_queue, board_queue, clock_queue, opened=opened, second_start=manager.Event())
        lichess_bot.disable_restart()

        def streams_opened() -> int:
            count = 0
            while True:
                try:
                    opened.get_nowait()
                    count += 1
                except Empty:
                    return count

        def finish(code: int, message: str) -> None:
            print(message, flush=True)
            sys.stderr = open(os.devnull, "w")   # threads still waiting on the queues complain as they close
            for child in multiprocessing.active_children():
                child.terminate()    # lichess-bot's players, left waiting on a game when the test fails
            manager.shutdown()       # or its server process outlives us, holding the console open
            os._exit(code)

        def give_up() -> None:
            finish(1, f"FAILED: no result in 60 s ({streams_opened()} game streams opened; a second player?)")
        watchdog = threading.Timer(60, give_up)
        watchdog.daemon = True
        watchdog.start()

        opponent = threading.Thread(target=lichess_org_simulator, args=[move_queue, board_queue, clock_queue, results],
                                    daemon=True)
        opponent.start()
        lichess_bot.start(li, li.get_profile(), cfg, logging.INFO, None, True, one_game=True)
        won = results.get(timeout=60)
        watchdog.cancel()
        count = streams_opened()
        if won and count == 2:
            finish(0, "passed: gameStart came twice, one player played; its stream dropped, it rejoined and won")
        finish(1, f"FAILED: {'won' if won else 'not won'}, {count} game streams opened (2 = one player and its rejoin)")


if __name__ == "__main__":
    main()
