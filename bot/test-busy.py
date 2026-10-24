"""A test for bot/lichess-bot-busy.patch: a challenge that arrives while every game slot is taken is declined at once
with "later", not queued (2026-10-18: 133 of the 168 accepts made straight after a game had failed without a word,
their challenges withdrawn while they waited in the queue).

    bot\\lichess-bot\\.venv\\Scripts\\python.exe bot\\test-busy.py

Runs lichess-bot's own handle_challenge (lib/lichess_bot.py, as patched) with its default settings at concurrency 2
and 3 (the bot's from 2026-10-25), against a pretend lichess.org that only lists ongoing games and records declines; no network, no token. LICHESS_BOT=<dir>
tests another copy of lichess-bot.
"""
import os
import sys
import types
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
BOT = os.environ.get("LICHESS_BOT") or os.path.join(HERE, "lichess-bot")
os.chdir(BOT)
sys.path.insert(0, BOT)
sys.modules.setdefault("pytest", types.ModuleType("pytest"))

import yaml                                  # noqa: E402
from lib import config, lichess_bot, model   # noqa: E402
from lib.blocklist import OnlineBlocklist    # noqa: E402


def challenge(cid: str, who: str) -> dict:
    """A casual bullet challenge from a human, as lichess.org sends it (lichess-bot's own test_model.py shape)."""
    return {"type": "challenge", "challenge": {
        "id": cid, "url": f"https://lichess.org/{cid}", "status": "created",
        "challenger": {"id": who.lower(), "name": who, "rating": 2000, "title": None, "online": True},
        "destUser": {"id": "b", "name": "b", "rating": 2200, "title": "BOT", "online": True},
        "variant": {"key": "standard", "name": "Standard", "short": "Std"}, "rated": False, "speed": "bullet",
        "timeControl": {"type": "clock", "limit": 60, "increment": 1, "show": "1+1"},
        "color": "random", "finalColor": "white", "perf": {"icon": "", "name": "Bullet"}}}


PROFILE = {"id": "b", "username": "b", "title": "BOT",
           "perfs": {"bullet": {"games": 100, "rating": 2200, "rd": 60, "prog": 0}}}


class PretendLichess:
    """The two calls handle_challenge makes: the games in progress, and declining."""

    def __init__(self, playing: list[str]) -> None:
        self.games = [{"gameId": f"game{i}", "opponent": {"username": name}} for i, name in enumerate(playing)]
        self.declined: list[tuple[str, str]] = []

    def get_ongoing_games(self) -> list:
        return self.games

    def decline_challenge(self, challenge_id: str, reason: str = "generic") -> None:
        self.declined.append((challenge_id, reason))


def settings(concurrency: int):
    """lichess-bot's default challenge settings at this concurrency."""
    with open("./config.yml.default") as f:
        raw = yaml.safe_load(f)
    raw["token"] = ""
    raw["challenge"]["concurrency"] = concurrency
    config.insert_default_values(raw)
    return config.Configuration(raw).challenge


def main() -> int:
    cfg = settings(2)
    failures = 0

    def case(name: str, playing: list[str], queued: list[str], expect: str, event: dict | None = None) -> None:
        nonlocal failures
        li = PretendLichess(playing)
        queue = [model.Challenge(challenge(f"q{i}", who)["challenge"], PROFILE) for i, who in enumerate(queued)]
        event = event or challenge("newchall", "Newcomer")
        lichess_bot.handle_challenge(event, li, queue, cfg, PROFILE, defaultdict(list), OnlineBlocklist([]))
        queued = any(c.id == event["challenge"]["id"] for c in queue)
        got = f"declined: {li.declined[0][1]}" if li.declined else ("queued" if queued else "dropped")
        ok = got == expect
        failures += not ok
        print(f"  {'ok  ' if ok else 'FAIL'} {name}: {got} (expected {expect})")

    print("handle_challenge at concurrency 2:")
    case("no games", [], [], "queued")
    case("one game", ["Alice"], [], "queued")
    case("two games", ["Alice", "Bob"], [], "declined: later")
    case("one game, one challenge queued", ["Alice"], ["Carol"], "declined: later")
    chess960 = challenge("variant1", "Newcomer")
    chess960["challenge"]["variant"] = {"key": "chess960", "name": "Chess960", "short": "960"}
    case("a variant it doesn't play, slots free", [], [], "declined: variant", chess960)
    case("a variant it doesn't play, slots full", ["Alice", "Bob"], [], "declined: variant", chess960)
    cfg = settings(3)                        # the bot's from 2026-10-25 (bot/make-config.py)
    print("handle_challenge at concurrency 3:")
    case("two games", ["Alice", "Bob"], [], "queued")
    case("three games", ["Alice", "Bob", "Dave"], [], "declined: later")
    case("two games, one challenge queued", ["Alice", "Bob"], ["Carol"], "declined: later")
    case("one game, one challenge queued", ["Alice"], ["Carol"], "queued")
    print("passed" if not failures else f"FAILED: {failures} case(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
