"""A test for bot/lichess-bot-rejoin.patch: a game whose stream lichess.org closes mid-game is rejoined and played to
the end, not left to run out of time (2026-10-17: PeachFruit, lost on time after its stream dropped twice).

    bot\\lichess-bot\\.venv\\Scripts\\python.exe bot\\test-rejoin.py

Uses lichess-bot's own pretend lichess.org (test_bot/lichess.py: no network, no token) and its opponent, which plays
white; the bot under test plays black with lichess-bot's homemade test engine (Scholar's mate). The game's first
stream is closed after the opponent's first move. The bot can only win by opening the stream again, so a win passes;
no result within 60 s fails (the old lichess-bot leaves the game and the opponent waits for ever).
"""
import os
import sys
import threading
import types

HERE = os.path.dirname(os.path.abspath(__file__))
BOT = os.path.join(HERE, "lichess-bot")
os.chdir(BOT)
sys.path.insert(0, BOT)
sys.modules.setdefault("pytest", types.ModuleType("pytest"))   # test_bot.test_bot refuses to load without it

import datetime                                                 # noqa: E402
import logging                                                  # noqa: E402
import tempfile                                                 # noqa: E402
from multiprocessing import Manager                             # noqa: E402
from queue import Queue                                         # noqa: E402
import chess                                                    # noqa: E402
import yaml                                                     # noqa: E402
import test_bot.lichess as pretend                              # noqa: E402
from lib import config, lichess_bot                             # noqa: E402
from lib.engine_wrapper import test_suffix                      # noqa: E402

GAME = {"gameId": "zzzzzzzz", "speed": "bullet", "opponent": {"username": "bo"}}


class DroppingGameStream(pretend.GameStream):
    """A game stream that lichess.org closes after the full game and one move, the game still on."""

    def iter_lines(self, *args, **kwargs):
        lines = super().iter_lines(*args, **kwargs)
        yield next(lines)          # the full game
        yield next(lines)          # the opponent's first move
        lines.close()              # ... and the stream ends


class DroppingLichess(pretend.Lichess):
    """The pretend lichess.org, whose first game stream drops; the game counts as ongoing throughout."""

    def __init__(self, *args) -> None:
        super().__init__(*args)
        self.streams = 0

    def get_game_stream(self, game_id: str) -> pretend.GameStream:
        stream = super().get_game_stream(game_id)   # from the second on, this has the opponent resend the position
        self.streams += 1
        return DroppingGameStream(self.board_queue, self.clock_queue) if self.streams == 1 else stream

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
        li = DroppingLichess(move_queue, board_queue, clock_queue)
        lichess_bot.disable_restart()

        def finish(code: int, message: str) -> None:
            print(message, flush=True)
            sys.stderr = open(os.devnull, "w")   # threads still waiting on the queues complain as they close
            manager.shutdown()       # or its server process outlives us, holding the console open
            os._exit(code)

        def give_up() -> None:
            finish(1, "FAILED: no result in 60 s (the bot left the game when its stream dropped)")
        watchdog = threading.Timer(60, give_up)
        watchdog.daemon = True
        watchdog.start()

        opponent = threading.Thread(target=lichess_org_simulator, args=[move_queue, board_queue, clock_queue, results],
                                    daemon=True)
        opponent.start()
        lichess_bot.start(li, li.get_profile(), cfg, logging.INFO, None, True, one_game=True)
        won = results.get(timeout=60)
        watchdog.cancel()
        finish(0 if won else 1, "passed: the stream dropped, the bot rejoined and won" if won else "FAILED: the bot didn't win")


if __name__ == "__main__":
    main()
