"""Keep the games of a Lichess .pgn.zst that Lichess analysed, with Stockfish's evaluation after every move.

    python -I scripts/eval-games.py <in .pgn.zst> <out .pgn> [--max-bytes N]

About one game in ten carries [%eval ...] comments (Lichess's Stockfish, after each move, for white:
pawns, or #N for a mate in N, #-N for being mated). Those games are kept, with the tags the tuner uses
(Event, Site, Result, WhiteElo, BlackElo, TimeControl, Termination) and one more that `game import` keeps:
[Evals "e1 e2 ..."], the evaluation after each ply in centipawns for white, M<n> / M-<n> for mates, ? where
a ply has none. Comments go. For Texel tuning towards Stockfish's judgement (`tune positions --target eval`).

Reads across the file's Zstandard frames and stops at a cut; --max-bytes reads only the start.
"""
import io, re, sys, time
from compression import zstd

KEEP = ("Event", "Site", "Result", "WhiteElo", "BlackElo", "TimeControl", "Termination")
TOKEN = re.compile(r"\{[^}]*\}|[^\s{}]+")
EVAL = re.compile(r"%eval (#?-?[\d.]+)")
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
    """The game with its evaluations as a tag; None if Lichess didn't analyse it."""
    if "%eval" not in movetext or any(name == "FEN" for name, _ in tags):
        return None
    moves, evals = [], []
    for token in TOKEN.findall(movetext):
        if token[0] == "{":
            if (m := EVAL.search(token)) and len(evals) == len(moves) - 1:
                value = m[1]
                evals.append(("M" + value[1:]) if value[0] == "#" else str(round(float(value) * 100)))
        elif token in RESULTS or token[-1] == "." or token[0] == "$":
            continue
        else:
            if len(evals) < len(moves):
                evals.append("?")   # a move with no evaluation
            moves.append(token)
    while len(evals) < len(moves):
        evals.append("?")
    lines = [f'[{name} "{value}"]' for name, value in tags if name in KEEP]
    lines.append(f'[Evals "{" ".join(evals)}"]')
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
                    if movetext:
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
            if movetext:
                games += 1
                if (pgn := convert(tags, " ".join(movetext))) is not None:
                    out.write(pgn)
                    kept += 1
        except (EOFError, zstd.ZstdError) as e:
            print(f"stopped at the cut ({e}); the game it was in dropped", file=sys.stderr)
    print(f"{games:,} games read, {kept:,} analysed kept ({100 * kept / max(1, games):.1f}%), {time.time() - start:.0f}s")


if __name__ == "__main__":
    main()
