"""A test for bot/lichess-bot-book-repetition.patch: the opening book never plays a move back to a position the game
has had; another book move does, or the engine decides.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe bot\\test-book-repetition.py

2026-10-25: in rsJhY1E7 every one of our moves came from the vetted Black book, 6...Qa5+ 7.Nc3 Qb6 8.Na4 Qa5+ 9.Nc3
Qb6, a draw by threefold repetition at move 9 against a bot rated ~140 below us. Matthew's 1(a): skip a book move
that repeats a position, and let the engine decide.

lichess-bot's own get_book_move() with small books written here (the bot's books are git-ignored), in that game's
positions. Cases: 6...Qa5+ the first time (played), 7...Qb6 with Nf6 beside it (Nf6, under each selection), 7...Qb6
alone (no book move: the engine's), 8...Qa5+ the second time (the same), a second book with a move the first lacks
(the second book's), and 1.e4 from the start (untouched). The skips are logged. The lichess-bot tested is
bot\\lichess-bot, or the folder in LICHESS_BOT (a copy, so a running bot is left alone).
"""
import logging
import os
import struct
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
BOT = os.environ.get("LICHESS_BOT") or os.path.join(HERE, "lichess-bot")
os.chdir(BOT)
sys.path.insert(0, BOT)

import chess                                                    # noqa: E402
import chess.polyglot                                           # noqa: E402
from lib import engine_wrapper                                  # noqa: E402
from lib.config import Configuration                            # noqa: E402

GAME = "1. e4 c5 2. Nf3 e6 3. d4 cxd4 4. Nxd4 Qb6 5. Nc3 Bc5 6. Na4 Qa5+ 7. Nc3 Qb6 8. Na4 Qa5+ 9. Nc3 Qb6".split()
SANS = [s for s in GAME if not s.endswith(".")]


class PretendGame:
    id = "rsJhY1E7"


class Logged(logging.Handler):
    def __init__(self) -> None:
        super().__init__()
        self.lines = []

    def emit(self, record: logging.LogRecord) -> None:
        self.lines.append(record.getMessage())


def board_after(plies: int) -> chess.Board:
    board = chess.Board()
    for san in SANS[:plies]:
        board.push_san(san)
    return board


def write_book(path: str, entries: list[tuple[chess.Board, str, int]]) -> str:
    """A polyglot book: (position, move, weight) entries, sorted by the position's key as the format wants."""
    rows = []
    for board, uci, weight in entries:
        move = chess.Move.from_uci(uci)
        code = move.to_square | move.from_square << 6
        rows.append((chess.polyglot.zobrist_hash(board), code, weight))
    with open(path, "wb") as f:
        for key, code, weight in sorted(rows):
            f.write(struct.pack(">QHHI", key, code, weight, 0))
    return path


def book_move(books: list[str], board: chess.Board, selection: str) -> str | None:
    cfg = Configuration({"enabled": True, "max_depth": 12, "book": {"standard": list(books)}, "selection": selection,
                         "min_weight": 1, "normalization": "none"})
    move = engine_wrapper.get_book_move(board, PretendGame(), cfg).move
    return move and move.uci()


def main() -> None:
    logged = Logged()
    engine_wrapper.logger.addHandler(logged)
    engine_wrapper.logger.setLevel(logging.INFO)
    after_na4, after_nc3, after_na4_again = board_after(11), board_after(13), board_after(15)
    start = chess.Board()
    bad = []

    def check(name: str, got: str | None, want: str | None) -> None:
        if got != want:
            bad.append(f"{name}: got {got}, wanted {want}")

    with tempfile.TemporaryDirectory() as d:
        main_book = write_book(os.path.join(d, "main.bin"), [
            (start, "e2e4", 50),
            (after_na4, "b6a5", 100),                    # 6...Qa5+, the first time
            (after_nc3, "a5b6", 100), (after_nc3, "g8f6", 1),   # 7...Qb6 back to 5...Bc5's position, or 7...Nf6
            (after_na4_again, "b6a5", 100)])             # 8...Qa5+, back to 6...Qa5+'s position
        only_qb6 = write_book(os.path.join(d, "only-qb6.bin"), [(after_nc3, "a5b6", 100)])
        other = write_book(os.path.join(d, "other.bin"), [(after_nc3, "c5e7", 5)])

        for selection in ("weighted_random", "uniform_random", "best_move"):
            check(f"start, {selection}", book_move([main_book], start, selection), "e2e4")
            check(f"6...Qa5+ the first time, {selection}", book_move([main_book], after_na4, selection), "b6a5")
            picks = {book_move([main_book], after_nc3, selection) for _ in range(40)}
            check(f"7...Qb6 or Nf6, {selection} (40 picks)", ",".join(sorted(picks)), "g8f6")
            check(f"7...Qb6 alone, {selection}", book_move([only_qb6], after_nc3, selection), None)
            check(f"8...Qa5+ the second time, {selection}", book_move([main_book], after_na4_again, selection), None)
            check(f"7...Qb6 alone, then a second book, {selection}",
                  book_move([only_qb6, other], after_nc3, selection), "c5e7")
        if [b.move_stack[-1].uci() for b in (after_na4, after_nc3, after_na4_again)] != ["c3a4", "a4c3", "c3a4"]:
            bad.append("the test's own positions aren't the game's")

    skips = [line for line in logged.lines if " skips " in line]
    if not any("skips a5b6" in line and "rsJhY1E7" in line for line in skips):
        bad.append(f"no 'skips a5b6' line logged; logged: {logged.lines[:6]}")
    if any("e2e4" in line or "g8f6" in line for line in skips):
        bad.append(f"a move that doesn't repeat was logged as skipped: {skips}")
    if any(" skips " in line and "other.bin" in line for line in skips):
        bad.append("the second book logged a skip it has no reason for")

    for line in bad:
        print(line)
    print("FAILED" if bad else "passed: book moves back to a position the game has had are skipped; others play")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
