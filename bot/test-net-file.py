"""A test for bot/lichess-bot-net-file.patch: each game plays on the net bot\\eval\\net.txt names when it starts, so the
net changes between games with no restart; otherwise, the config's.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe bot\\test-net-file.py

2026-10-25, Matthew: "can we dynamically change which neural network we are using?", then (a): a one-line file read
as each game starts. lichess-bot's own hook for that, extra_game_handlers.game_specific_options(), as patched, with
nets written here (our format: "CBNNUE01" first, the SHA-256 of the rest last) and lichess-bot's own
EngineWrapper.configure() sending its options to a pretend engine. Cases: no BRUTEFORCER_NET, no net.txt, a blank
or notes-only net.txt (the config's net); a name in the folder, with notes, quotes, a BOM and spaces about it; a full
path; a switch between two games and back to the config's; a name that isn't there, a cut net, a damaged one and a
file that isn't a net (each logged, the config's net plays). Also the bot's own nets in bot\\eval pass the check, so
it reads real nets as the engine does. The lichess-bot tested is bot\\lichess-bot, or the folder in LICHESS_BOT (a
copy, so a running bot is left alone).
"""
import glob
import hashlib
import logging
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
BOT = os.environ.get("LICHESS_BOT") or os.path.join(HERE, "lichess-bot")
os.chdir(BOT)
sys.path.insert(0, BOT)

import extra_game_handlers                                      # noqa: E402
from lib import engine_wrapper                                  # noqa: E402

CONFIG = {"EvalFile": r"C:\bot\eval\h1536-3m-ob8.nnue", "Threads": 8}


class PretendGame:
    def __init__(self, game_id: str) -> None:
        self.id = game_id


class PretendEngine:
    def __init__(self) -> None:
        self.sent = None

    def configure(self, options: dict) -> None:
        self.sent = dict(options)


class Logged(logging.Handler):
    def __init__(self) -> None:
        super().__init__()
        self.lines = []

    def emit(self, record: logging.LogRecord) -> None:
        self.lines.append(record.getMessage())


def net_bytes(payload: bytes) -> bytes:
    body = b"CBNNUE01" + payload
    return body + hashlib.sha256(body).digest()


def evalfile(game_id: str) -> str:
    """The EvalFile lichess-bot sends the engine for a game starting now, as configure() merges the hook's options."""
    engine = engine_wrapper.EngineWrapper.__new__(engine_wrapper.EngineWrapper)
    engine.engine = PretendEngine()
    engine.configure(dict(CONFIG), PretendGame(game_id))
    if engine.engine.sent["Threads"] != 8:
        raise AssertionError("the config's other options were lost")
    return engine.engine.sent["EvalFile"]


def main() -> None:
    logged = Logged()
    logging.getLogger().addHandler(logged)
    logging.getLogger().setLevel(logging.INFO)
    bad = []

    def check(name: str, got: str, want: str) -> None:
        if os.path.normcase(got) != os.path.normcase(want):
            bad.append(f"{name}: got {got}, wanted {want}")

    def logs(name: str, text: str) -> None:
        if not any(text in line for line in logged.lines[-2:]):
            bad.append(f"{name}: nothing logged with {text!r}; last: {logged.lines[-2:]}")

    with tempfile.TemporaryDirectory() as d:
        evals = os.path.join(d, "eval")
        os.makedirs(evals)
        listed = os.path.join(evals, "net.txt")
        a = os.path.join(evals, "h512-6m-l32.nnue")
        b = os.path.join(d, "elsewhere", "h1536-6m-ob8.nnue")
        os.makedirs(os.path.dirname(b))
        whole = net_bytes(os.urandom(5000))
        open(a, "wb").write(whole)
        open(b, "wb").write(net_bytes(os.urandom(3000)))
        open(os.path.join(evals, "cut.nnue"), "wb").write(whole[:-100])
        damaged = bytearray(whole)
        damaged[1000] ^= 1
        open(os.path.join(evals, "damaged.nnue"), "wb").write(bytes(damaged))
        open(os.path.join(evals, "eval-sf1.txt"), "w").write("not a net\n" * 10)

        def write(text: str, bom: bool = False) -> None:
            with open(listed, "w", encoding="utf-8-sig" if bom else "utf-8") as f:
                f.write(text)

        os.environ.pop("BRUTEFORCER_NET", None)
        write("h512-6m-l32.nnue\n")
        check("no BRUTEFORCER_NET", evalfile("g0000001"), CONFIG["EvalFile"])
        os.environ["BRUTEFORCER_NET"] = os.path.join(d, "no-such-folder", "net.txt")
        check("no net.txt", evalfile("g0000002"), CONFIG["EvalFile"])
        os.environ["BRUTEFORCER_NET"] = listed
        write("")
        check("an empty net.txt", evalfile("g0000003"), CONFIG["EvalFile"])
        write("# back to make-config.py's net\n\n   \n")
        check("notes only", evalfile("g0000004"), CONFIG["EvalFile"])
        write("# trying the 512\n\n  \"h512-6m-l32.nnue\"  \r\n# a note after\n", bom=True)
        check("a name with notes, quotes, a BOM and spaces", evalfile("g0000005"), a)
        logs("a name", "Game g0000005 plays on h512-6m-l32.nnue")
        write(b + "\n")
        check("a full path", evalfile("g0000006"), b)
        write("h512-6m-l32.nnue\n")
        check("the switch: game 7", evalfile("g0000007"), a)
        write(b + "\n")
        check("the switch: game 8", evalfile("g0000008"), b)
        write("")
        check("the switch: back to the config's for game 9", evalfile("g0000009"), CONFIG["EvalFile"])
        for name, text in (("no-such-net.nnue", "which isn't there"), ("cut.nnue", "isn't a whole .nnue net"),
                           ("damaged.nnue", "isn't a whole .nnue net"), ("eval-sf1.txt", "isn't a whole .nnue net")):
            write(name + "\n")
            check(name, evalfile("g0000010"), CONFIG["EvalFile"])
            logs(name, text)

    real = sorted(glob.glob(os.path.join(HERE, "eval", "*.nnue")))
    for path in real:
        data = open(path, "rb").read()
        if not data.startswith(extra_game_handlers.NNUE_MAGIC) or hashlib.sha256(data[:-32]).digest() != data[-32:]:
            bad.append(f"the bot's own {os.path.basename(path)} fails the check")
    if not real:
        bad.append("no nets in bot\\eval to check the format against")

    for line in bad:
        print(line)
    print("FAILED" if bad else f"passed: net.txt picks each game's net, the config's otherwise; {len(real)} real nets "
          "check out")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
