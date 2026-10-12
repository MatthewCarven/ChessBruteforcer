"""Keep the games of a Lichess .pgn.zst that get down to 5 pieces or fewer, with their clocks.

    python -I scripts/table-games.py <in .pgn.zst> <out .pgn> [--max-bytes N]

A game has 32 - captures pieces on the board, so it reaches 5 or fewer exactly
when it makes 27 captures (SAN marks every capture with an x, en passant
included). Only those games are kept, about 6.5% of them, so a whole month
(~200 GB of PGN unpacked) never touches the disk: it streams through here.

Kept tags: Event, Site, Result, WhiteElo, BlackElo, TimeControl, Termination
(and FEN, SetUp if there): what the scorecards and the examples use. Comments
go, but the clock readings ([%clk h:mm:ss]: the mover's time left after the
move, increment added) are kept from two plies before the 27th capture on, as
two tags that `game import` keeps: [ClockPly "n"], the 0-based ply of the
first reading, and [Clocks "s s s ..."], whole seconds, one a ply from there.

Reads across the file's Zstandard frames and stops at a cut (a partial
download), dropping the game it was in. --max-bytes reads only the start of
the compressed file (a sample, or the part already downloaded).
"""
import io, re, sys, time
from compression import zstd

KEEP = ("Event", "Site", "Result", "WhiteElo", "BlackElo", "TimeControl", "Termination", "FEN", "SetUp")
CAPTURES = 27   # 32 pieces - 27 captures = 5
TOKEN = re.compile(r"\{[^}]*\}|[^\s{}]+")
CLOCK = re.compile(r"%clk (\d+):(\d+):(\d+)")
RESULTS = {"1-0", "0-1", "1/2-1/2", "*"}


class Limited(io.RawIOBase):
    """The first `limit` bytes of a file."""

    def __init__(self, raw, limit):
        self.raw, self.left = raw, limit

    def readable(self):
        return True

    def readinto(self, buffer):
        n = min(len(buffer), self.left)
        if n <= 0:
            return 0
        got = self.raw.readinto(memoryview(buffer)[:n])
        self.left -= got
        return got


def convert(tags, movetext):
    """The game with fewer tags, no comments and its clocks from before the 27th capture; None if it never gets there."""
    if movetext.count("x") < CAPTURES:   # quick: comments ([%clk], [%eval]) hold no x
        return None
    moves, clocks, captures, at = [], [], 0, None
    for token in TOKEN.findall(movetext):
        if token[0] == "{":
            if (m := CLOCK.search(token)) and len(clocks) == len(moves) - 1:
                clocks.append(int(m[1]) * 3600 + int(m[2]) * 60 + int(m[3]))
        elif token in RESULTS or token[-1] == "." or token[0] == "$":
            continue
        else:
            if len(clocks) < len(moves):
                clocks.append(None)   # a move with no reading
            moves.append(token)
            if "x" in token:
                captures += 1
                if captures == CAPTURES:
                    at = len(moves) - 1
    if at is None:
        return None
    lines = [f'[{name} "{value}"]' for name, value in tags if name in KEEP]
    first = max(0, at - 2)
    readings = clocks[first:]
    if readings and all(r is not None for r in readings):
        lines.append(f'[ClockPly "{first}"]')
        lines.append(f'[Clocks "{" ".join(map(str, readings))}"]')
    words = []
    for ply, move in enumerate(moves):
        if ply % 2 == 0:
            words.append(f"{ply // 2 + 1}.")
        words.append(move)
    result = next((v for n, v in tags if n == "Result"), "*")
    return "\n".join(lines) + "\n\n" + " ".join(words) + " " + result + "\n\n"


def main():
    args = sys.argv[1:]
    limit = None
    if "--max-bytes" in args:
        i = args.index("--max-bytes")
        limit = int(args[i + 1])
        del args[i:i + 2]
    src, dst = args
    start = time.time()
    games = kept = 0
    raw = open(src, "rb")
    source = Limited(raw, limit) if limit else raw
    tags, movetext = [], []
    with zstd.open(source, "rb") as packed, open(dst, "w", encoding="utf-8", newline="\n") as out:
        text = io.TextIOWrapper(packed, encoding="utf-8", newline="\n")
        try:
            for line in text:
                if line.startswith("["):
                    if movetext:   # a new game begins: finish the last
                        games += 1
                        if (pgn := convert(tags, " ".join(movetext))) is not None:
                            out.write(pgn)
                            kept += 1
                        tags, movetext = [], []
                        if games % 1_000_000 == 0:
                            print(f"{games:,} games, kept {kept:,}, {time.time() - start:.0f}s", file=sys.stderr, flush=True)
                    name, _, value = line.strip()[1:-1].partition(" ")
                    tags.append((name, value.strip('"')))
                elif line.strip():
                    movetext.append(line.strip())
            if movetext:   # the whole file was read: the last game is complete
                games += 1
                if (pgn := convert(tags, " ".join(movetext))) is not None:
                    out.write(pgn)
                    kept += 1
        except (EOFError, zstd.ZstdError) as e:
            print(f"stopped at the cut ({e}); the game it was in dropped", file=sys.stderr)
    print(f"{games:,} games read, {kept:,} kept ({100 * kept / max(1, games):.2f}%), {time.time() - start:.0f}s")


if __name__ == "__main__":
    main()
