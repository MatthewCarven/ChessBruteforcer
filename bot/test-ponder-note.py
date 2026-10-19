"""A test for bot/lichess-bot-ponder-note.patch: a move the engine found after a ponder hit says so in the bot's log.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe bot\\test-ponder-note.py

2026-10-20: 23% of the bot's engine moves were logged as "Source: Engine" and nothing else (no Evaluation, Depth or
Pv), every one of them a ponder hit: python-chess keeps only what the engine reports after "ponderhit", and when no
new depth finishes before the move is played, there is nothing. Now the source says "Engine, ponder hit", with why
the stats are missing when they are.

lichess-bot's own search() and get_stats() with a pretend engine that plays a script of moves and replies. Five
searches: the first (nothing pondered yet), a hit with no stats, a miss (the opponent played something else), a hit
with stats, and a predicted reply searched with pondering off (which python-chess doesn't count as a hit). The
lichess-bot tested is bot\\lichess-bot, or the folder in LICHESS_BOT (a copy, so a running bot is left alone).
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
BOT = os.environ.get("LICHESS_BOT") or os.path.join(HERE, "lichess-bot")
os.chdir(BOT)
sys.path.insert(0, BOT)

import chess                                                    # noqa: E402
import chess.engine                                             # noqa: E402
from lib import engine_wrapper                                  # noqa: E402
from lib.config import Configuration                            # noqa: E402

SCORE = {"score": chess.engine.PovScore(chess.engine.Cp(30), chess.WHITE), "depth": 14, "nodes": 1_000_000}


class ScriptedEngine:
    """Plays the next (move, ponder, info) of its script, whatever it's asked."""

    def __init__(self, script):
        self.script = list(script)

    def play(self, board, limit, **kwargs):
        move, ponder, info = self.script.pop(0)
        return chess.engine.PlayResult(chess.Move.from_uci(move), ponder and chess.Move.from_uci(ponder), dict(info))


def wrapper(script):
    engine = engine_wrapper.EngineWrapper.__new__(engine_wrapper.EngineWrapper)
    engine.engine = ScriptedEngine(script)
    engine.scores = []
    engine.draw_or_resign = Configuration({"offer_draw_enabled": False, "offer_draw_moves": 10, "offer_draw_score": 0,
                                           "offer_draw_pieces": 10, "resign_enabled": False, "resign_moves": 3,
                                           "resign_score": -1000})
    engine.go_commands = Configuration({})
    engine.move_commentary = []
    engine.comment_start_index = -1
    return engine


def source(engine, board, ponder):
    result = engine.search(board, chess.engine.Limit(time=1), ponder, False, None)
    engine.add_comment(result, board)
    board.push(result.move)
    return engine.get_stats()[0]


def main() -> None:
    engine = wrapper([("e2e4", "e7e5", SCORE),     # 1. first search: nothing pondered
                      ("g1f3", "b8c6", {}),        # 2. e5 was predicted: a hit, nothing reported after it
                      ("d2d4", "e5d4", SCORE),     # 3. d6 instead of Nc6: no hit
                      ("f3d4", "g8f6", SCORE),     # 4. exd4 was predicted: a hit, with stats
                      ("b1c3", None, {})])         # 5. Nf6 was predicted, but searched with pondering off
    board = chess.Board()
    want = ["Source: Engine",
            "Source: Engine, ponder hit (thought in the opponent's time; no depth finished after the hit to report)",
            "Source: Engine",
            "Source: Engine, ponder hit",
            "Source: Engine"]
    got = [source(engine, board, True)]
    board.push_san("e5")
    got.append(source(engine, board, True))
    board.push_san("d6")
    got.append(source(engine, board, True))
    board.push_san("exd4")
    got.append(source(engine, board, True))
    board.push_san("Nf6")
    got.append(source(engine, board, False))
    bad = [(i + 1, g, w) for i, (g, w) in enumerate(zip(got, want)) if g != w]
    for i, g, w in bad:
        print(f"search {i}: got {g!r}, wanted {w!r}")
    print("FAILED" if bad else "passed: hits are marked, with or without stats; misses and unpondered searches aren't")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
