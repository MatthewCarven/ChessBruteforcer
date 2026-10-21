"""collect.py on a made-up bot folder in lichess-bot's formats: the setup each game gets, and the rest of its row.

    python -I scripts/opponents/test_collect.py

Two starts (a network, then the built-in numbers), a game from each (ours by matchmaking, theirs accepted), book moves
from two books, a line naming the token that must change nothing; then the same run again gives the same file, and a missing
games folder fails without touching the record. Prints "ok" or raises.
"""
import csv
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
COLLECT = os.path.join(HERE, "collect.py")
B = chr(92)                                   # a backslash
D = B + B                                     # how lichess-bot's log shows one inside an engine line (its repr)

LOG = [
    "2026-10-22 02:25:06,282 lib.lichess_bot (lichess_bot.py:1337) INFO Checking engine configuration ...",
    "2026-10-22 02:25:06,364 chess.engine (engine.py:1448) WARNING <UciProtocol (pid=1)>: Unexpected engine output: "
    f"'info string endgame tables from C:{D}x{D}tables'",
    "2026-10-22 02:25:06,408 chess.engine (engine.py:1448) WARNING <UciProtocol (pid=1)>: Unexpected engine output: "
    f"'info string evaluation: network C:{D}x{D}bot{D}eval{D}h1024-3m.nnue (768 -> 1024 x2 -> 1, sha256 510a8d0e6c43)'",
    "2026-10-22 02:25:08,300 lib.lichess_bot (lichess_bot.py:1340) INFO Engine configuration OK",
    "2026-10-22 02:26:17,455 lib.matchmaking (matchmaking.py:289) INFO Challenge id is AAAAAAAA.",
    "2026-10-22 02:29:28,900 lib.lichess_bot (lichess_bot.py:809) INFO +++ https://lichess.org/AAAAAAAA/white Blitz "
    "vs BOT foo (2716) (AAAAAAAA)",
    f"2026-10-22 02:29:29,000 lib.engine_wrapper (engine_wrapper.py:778) INFO Got move e2e4 from book C:{B}x{B}bot{B}"
    f"books{B}lichess-2200.bin for game AAAAAAAA",
    f"2026-10-22 02:29:30,000 lib.engine_wrapper (engine_wrapper.py:778) INFO Got move g1f3 from book C:{B}x{B}bot{B}"
    f"books{B}lichess-2200.bin for game AAAAAAAA",
    f"2026-10-22 02:29:31,000 lib.engine_wrapper (engine_wrapper.py:778) INFO Got move d2d4 from book C:{B}x{B}bot{B}"
    f"books{B}ours.bin for game AAAAAAAA",
    "2026-10-22 02:33:38,512 lib.lichess_bot (lichess_bot.py:1058) INFO --- https://lichess.org/AAAAAAAA/white Game over",
    "2026-10-22 03:00:22,797 lib.lichess_bot (lichess_bot.py:1337) INFO Checking engine configuration ...",
    "2026-10-22 03:00:23,000 lib.lichess_bot (lichess_bot.py:1340) INFO Engine configuration OK",
    "2026-10-22 03:05:00,000 lib.lichess_bot (lichess_bot.py:521) INFO Accept Blitz rated challenge from BOT bar (2000) "
    "(BBBBBBBB)",
    "2026-10-22 03:05:01,000 lib.config (config.py:1) INFO Challenge id is BBBBBBBB. (a line naming the token)",
    "2026-10-22 03:05:02,000 lib.lichess_bot (lichess_bot.py:809) INFO +++ https://lichess.org/BBBBBBBB/black Blitz "
    "vs BOT bar (2000) (BBBBBBBB)",
    "2026-10-22 03:09:00,000 lib.lichess_bot (lichess_bot.py:1058) INFO --- https://lichess.org/BBBBBBBB/black Game over",
]


def pgn(white, black, gid, result, moves, welo, belo):
    return (f'[Event "rated blitz game"]\n[Site "https://lichess.org/{gid}"]\n[White "{white}"]\n[Black "{black}"]\n'
            f'[Result "{result}"]\n[GameId "{gid}"]\n[UTCDate "2026.10.21"]\n[UTCTime "15:29:23"]\n'
            f'[WhiteElo "{welo}"]\n[BlackElo "{belo}"]\n[WhiteTitle "BOT"]\n[BlackTitle "BOT"]\n'
            f'[TimeControl "180+2"]\n[ECO "C20"]\n[Opening "King\'s Pawn Game"]\n[Termination "Normal"]\n\n'
            f"{moves} {result}\n")


def run(bot, out):
    return subprocess.run([sys.executable, "-I", COLLECT, bot, out], capture_output=True, text=True)


with tempfile.TemporaryDirectory() as root:
    bot = os.path.join(root, "bot")
    os.makedirs(os.path.join(bot, "logs"))
    os.makedirs(os.path.join(bot, "games"))
    with open(os.path.join(bot, "logs", "bot-2026-10-22.log"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(LOG) + "\n")
    with open(os.path.join(bot, "games", "matthewcarven vs foo - AAAAAAAA.pgn"), "w", encoding="utf-8") as f:
        f.write(pgn("matthewcarven", "foo", "AAAAAAAA", "1-0",
                    "1. e4 { [%clk 0:03:00] } 1... e5 { [%clk 0:03:00] } 2. d4 ( 2. Nf3 Nc6 3. Bb5 { [%eval 0.3,9] } ) "
                    "2... exd4", 2600, 2716))
    with open(os.path.join(bot, "games", "bar vs matthewcarven - BBBBBBBB.pgn"), "w", encoding="utf-8") as f:
        f.write(pgn("bar", "matthewcarven", "BBBBBBBB", "0-1", "1. d4 Nf6", 2000, 2600))
    out = os.path.join(bot, "games", "record.csv")
    r = run(bot, out)
    assert r.returncode == 0, r.stderr
    assert r.stdout.startswith("game record: 2 games"), r.stdout
    rows = {row["id"]: row for row in csv.DictReader(open(out, encoding="utf-8"))}
    a, b = rows["AAAAAAAA"], rows["BBBBBBBB"]
    want_a = {"net": "h1024-3m", "net_sha": "510a8d0e6c43", "challenger": "us", "colour": "white", "opponent": "foo",
              "score": "1.0", "our_elo": "2600", "opp_elo": "2716", "moves": "2", "line": "e4 e5 d4 exd4",
              "book_main": "2", "book_ours": "1", "book_wide": "0", "bot_start": "2026-10-22 02:25:06",
              "speed": "blitz", "rated": "rated", "beside": "0", "build": ""}
    want_b = {"net": "built-in", "net_sha": "", "challenger": "them", "colour": "black", "opponent": "bar",
              "score": "1.0", "our_elo": "2600", "opp_elo": "2000", "bot_start": "2026-10-22 03:00:22"}
    for row, want in ((a, want_a), (b, want_b)):
        for key, value in want.items():
            assert row[key] == value, (row["id"], key, row[key], value)
    first = open(out, "rb").read()
    assert run(bot, out).returncode == 0 and open(out, "rb").read() == first, "a second run changed the record"
    os.rename(os.path.join(bot, "games"), os.path.join(bot, "games-away"))
    os.makedirs(os.path.join(bot, "games"))
    kept = os.path.join(bot, "games-away", "record.csv")
    r = run(bot, os.path.join(bot, "games", "record.csv"))
    assert r.returncode != 0 and "no games" in r.stderr, (r.returncode, r.stderr)
    assert open(kept, "rb").read() == first
print("ok")
