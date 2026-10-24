"""A test for bot/lichess-bot-goodbye-stats.patch and !stats (bot/chat_commands.py): the goodbye at a game's end says
roughly how many positions our engine searched in it, in the same message, and !stats says it at any time.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe bot\\test-goodbye-stats.py

2026-10-25, Matthew: "some of the other bots print them, i dont want the message to be long but a figure on the total
computed positions would be nice", and it "does not need be 100% acurate". The figure is the sum of the nodes the
engine reported for its moves (a ponder hit's count runs from the start of the ponder; a miss isn't reported), "~".

lichess-bot's own Conversation (lib/conversation.py, as start-bot.cmd patches it) with a pretend game, engine and
lichess.org that keeps what goes to the chat, and bot/chat_commands.py from this folder as lib/bruteforcer_chat.py
(start-bot.cmd's copy may be older). The engine's moves are a game like the bot's median one (2026-10-22 to 24): 5 book
moves, then 50 searches of 136 million positions in 9.7 s each, some reporting their time and some only their speed.
Cases: the hello, and a short message with searches behind it but the game on (both left alone), !stats before any
search and partway, the goodbyes to the player and to spectators, Matthew's quit.soon goodbye (too long to take the
count, so left alone), a game with no search, a restart mid-game, and a fault in chat_commands.py (the goodbye still
goes, as it was). Every message is 140 characters at most.
The lichess-bot tested is bot\\lichess-bot, or the folder in LICHESS_BOT (a copy, so a running bot is left alone).
"""
import importlib.util
import inspect
import logging
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
BOT = os.environ.get("LICHESS_BOT") or os.path.join(HERE, "lichess-bot")
os.chdir(BOT)
sys.path.insert(0, BOT)

import lib                                                      # noqa: E402
from lib import lichess_bot                                     # noqa: E402
from lib.conversation import ChatLine, Conversation             # noqa: E402

spec = importlib.util.spec_from_file_location("lib.bruteforcer_chat", os.path.join(HERE, "chat_commands.py"))
chat = importlib.util.module_from_spec(spec)
sys.modules["lib.bruteforcer_chat"] = chat
spec.loader.exec_module(chat)
lib.bruteforcer_chat = chat

BOOK = {"string": chat.BOOK_SOURCE}
TIMED = {"depth": 15, "nodes": 136_000_000, "nps": 14_000_000, "time": 9.71}
UNTIMED = {"depth": 15, "nodes": 136_000_000, "nps": 14_000_000}   # (speed only: the seconds come from nodes / nps)
GAME = [BOOK] * 5 + [TIMED if i % 3 else UNTIMED for i in range(50)]


class PretendLichess:
    def __init__(self) -> None:
        self.sent = []

    def chat(self, game_id: str, room: str, text: str) -> None:
        self.sent.append((room, text))


class PretendGame:
    username = "matthewcarven"
    id = "zzzzzzzz"
    initial_fen = "startpos"

    def __init__(self) -> None:
        self.state = {"status": "started", "moves": ""}

    def url(self) -> str:
        return "https://lichess.org/zzzzzzzz/white"


class PretendEngine:
    def __init__(self, commentary, start: int = 0) -> None:
        self.move_commentary = list(commentary) if commentary is not None else None
        self.comment_start_index = start if commentary else -1


class Errors(logging.Handler):
    """The ERROR lines lichess-bot logs (kept, not printed)."""

    def __init__(self) -> None:
        super().__init__(logging.ERROR)
        self.messages = []

    def emit(self, record: logging.LogRecord) -> None:
        self.messages.append(record.getMessage())


def talk(commentary, start=0, over=False, said=None, room="player", message="Good game!"):
    """What reaches the chat: lichess-bot sending `message` unasked, or someone saying `said`."""
    li, game = PretendLichess(), PretendGame()
    if over:
        game.state["status"] = "mate"
    conversation = Conversation(game, PretendEngine(commentary, start), li, "test", [])
    if said:
        conversation.react(ChatLine({"room": room, "username": "someone", "text": said}))
    else:
        conversation.send_message(room, message)
    return li.sent


def main() -> int:
    if "bruteforcer_chat.outgoing" not in inspect.getsource(Conversation.send_message):
        print(f"FAILED: the patch isn't in {BOT}: start-bot.cmd applies bot\\lichess-bot-goodbye-stats.patch")
        return 1
    errors = Errors()
    logging.getLogger("lib.conversation").addHandler(errors)
    logging.getLogger("lib.conversation").propagate = False
    quit_goodbye = getattr(lichess_bot, "QUIT_SOON_GOODBYE", "")
    hello = ("Hi Bots! - I am matthewcarven: a brute-force engine that plays every position with 5 pieces or fewer "
             "perfectly. Good luck! (!help).")
    cases = [
        ("hello, at the start", talk([], message=hello), [("player", hello)]),
        ("a short message, game on", talk(GAME[:22], message="Good luck!"), [("player", "Good luck!")]),
        ("!stats, book moves only", talk(GAME[:5], said="!stats"),
         [("player", "I haven't searched any positions yet this game.")]),
        ("!stats, 17 searches in", talk(GAME[:22], said="!stats"),
         [("player", "So far I've searched ~2.3 billion positions this game, ~14 million a second.")]),
        ("!stats from a spectator", talk(GAME[:22], said="!stats", room="spectator"),
         [("spectator", "So far I've searched ~2.3 billion positions this game, ~14 million a second.")]),
        ("goodbye, to the player", talk(GAME, over=True),
         [("player", "Good game! I searched ~6.8 billion positions this game, ~14 million a second.")]),
        ("goodbye, to spectators", talk(GAME, over=True, room="spectator", message="Thanks for watching!"),
         [("spectator", "Thanks for watching! I searched ~6.8 billion positions this game, ~14 million a second.")]),
        ("quit.soon goodbye", talk(GAME, over=True, message=quit_goodbye), [("player", quit_goodbye)]),
        ("goodbye, no search", talk(GAME[:5], over=True), [("player", "Good game!")]),
        ("goodbye, restarted mid-game", talk(GAME[20:], start=40, over=True),
         [("player", "Good game! I searched ~4.8 billion positions since my restart, ~14 million a second.")]),
        ("goodbye, a fault", talk(None, over=True), [("player", "Good game!")]),
    ]
    failures = 0
    print("Goodbyes and !stats with the positions searched (bot/chat_commands.py through lichess-bot's Conversation):")
    for name, got, want in cases:
        wrong = got != want
        if name == "quit.soon goodbye" and not quit_goodbye:
            wrong = True   # (no quit.soon patch in this lichess-bot: nothing to test against)
        failures += wrong
        print(f"  {'ok  ' if not wrong else 'FAIL'} {name}: {got!r}" + (f"\n         wanted {want!r}" if wrong else ""))
    if errors.messages != ["bruteforcer_chat failed"]:
        failures += 1
        print(f"  FAIL the fault wasn't logged once as 'bruteforcer_chat failed': {errors.messages!r}")
    longest = max(len(text) for _, got, _ in cases for _, text in got)
    if longest > chat.CHAT_LIMIT:
        failures += 1
        print(f"  FAIL a message of {longest} characters: Lichess takes {chat.CHAT_LIMIT}")
    if len(chat.HELP) > chat.CHAT_LIMIT:
        failures += 1
        print(f"  FAIL !help is {len(chat.HELP)} characters")
    print("passed" if not failures else f"FAILED: {failures} case(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
