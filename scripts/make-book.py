"""An opening book from Lichess months: what strong players played, and how it scored, as a Polyglot .bin.

Two steps, so a month's download can go once it has been counted (Matthew, 2026-10-13):

    python -I scripts/make-book.py count <in .pgn.zst> <out .counts> [--min-elo 2200] [--plies 24] [--max-bytes N]
    python -I scripts/make-book.py book <out .bin> <counts>... [--min-games 10] [--min-score 0.40]
    python -I scripts/make-book.py verify <.pgn.zst> [--counts <its .counts>]

(python: the bot's venv, bot\\lichess-bot\\.venv\\Scripts\\python.exe, which has python-chess for the
Polyglot keys.)

count: games kept are rated, from the standard start, with a result, both players rated --min-elo or
more, and blitz or slower (base + 40 x increment >= 180 s: bullet moves are rushed). For each of their
first --plies positions, the move played and how the game went for the side that played it. Written
sorted by position and move, about 50 MB a month. Position-moves seen only once are dropped whenever
the count passes 6 M, to keep memory under a gigabyte; a move near the book's edge (--min-games) can
lose the odd game that way, nothing more. Reads across the file's Zstandard frames and stops at a cut
(a partial download); --max-bytes reads only the start. The file can be on a network share (the months
live on Matthew's desktop): if the share drops, the read waits and carries on from the same byte, for up
to 30 minutes at a time. A whole file read is checked against Lichess's SHA-256 (<file>.sha256 beside it,
which the download saves); a mismatch stops the count with nothing written.

book: the counts files merged a record at a time (memory stays flat however many months). A move goes
in if it was played --min-games times or more and scored --min-score or better for the side playing
it (a draw is half). Its weight is 2 x wins + draws (Polyglot make-book's rule), scaled within the
position so the largest fits 65,535: lichess-bot's "weighted_random" then plays the popular,
successful moves most often. Written to a temporary file and moved over the old book, so a running
bot (which opens the book afresh each move) never reads half a file.
"""
import array, hashlib, heapq, io, json, os, re, struct, sys, time
from compression import zstd

MAGIC = b"CBCOUNT1"
RECORD = struct.Struct(">QHIII")   # position key, Polyglot move, wins, draws, losses (for the side moving)
COMMENT = re.compile(r"\{[^}]*\}")
PRUNE_AT = 6_000_000


def options(args, spec):
    """Pull --name value pairs out of args (spec: name -> (default, type)); the rest are returned."""
    values = {}
    for name, (default, kind) in spec.items():
        values[name] = default
        if "--" + name in args:
            i = args.index("--" + name)
            values[name] = kind(args[i + 1])
            del args[i:i + 2]
    return values, args


def on_network(path):
    """Whether a path is on a network share: a UNC path (\\\\host\\share\\...), or a drive letter mapped to one."""
    path = os.path.abspath(path)
    if path.startswith(("\\\\", "//")):
        return True
    drive = os.path.splitdrive(path)[0]
    if os.name == "nt" and len(drive) == 2:
        import ctypes
        return ctypes.windll.kernel32.GetDriveTypeW(drive + "\\") == 4   # DRIVE_REMOTE
    return False


class Steady(io.RawIOBase):
    """A file read from start to end that rides out its network share dropping for a while (the months live on
    Matthew's desktop from 2026-10-13): a failed read, or one that finds nothing before the file's end, waits,
    reopens the file and carries on from the same byte, giving up after `patience` seconds of failures in a
    row. It also hashes what it reads, so the whole file can be checked against Lichess's SHA-256.
    A file on a network share is read at most `share_rate` bytes a second (Matthew, 2026-10-16: much faster
    than ~8 MB/s and the share traffic knocks the laptop off the internet, the bot's games with it).
    CB_SHARE_RATE sets another rate in bytes a second; 0 means no cap."""
    opener = staticmethod(open)   # tests swap in a flaky one
    first_pause, longest_pause, patience = 5, 60, 30 * 60
    share_rate = int(os.environ.get("CB_SHARE_RATE", 8_000_000))

    def __init__(self, path):
        self.path, self.offset, self.file, self.drops = path, 0, None, 0
        self.size = os.path.getsize(path)
        self.sha256 = hashlib.sha256()
        self.rate = self.share_rate if on_network(path) else 0
        self.paced_from = None                    # (time, offset) the pace counts from

    def pace(self):
        """Sleep as long as it takes to keep the reading at or under self.rate bytes a second."""
        if not self.rate:
            return
        now = time.monotonic()
        if self.paced_from is None:
            self.paced_from = (now, self.offset)
            return
        start, base = self.paced_from
        due = start + (self.offset - base) / self.rate
        if due > now:
            time.sleep(due - now)

    def readable(self):
        return True

    def readinto(self, buffer):
        waited, pause = 0, self.first_pause
        while True:
            try:
                if self.file is None:
                    self.file = self.opener(self.path, "rb")
                    self.file.seek(self.offset)
                got = self.file.readinto(buffer)
                if not got and self.offset < self.size:
                    raise OSError(f"read nothing at byte {self.offset:,} of {self.size:,}")
                break
            except OSError as e:
                try:
                    if self.file is not None:
                        self.file.close()
                except OSError:
                    pass
                self.file = None
                if waited >= self.patience:
                    raise
                if not waited:
                    self.drops += 1
                print(f"lost {self.path} at byte {self.offset:,} ({e}); trying again in {pause}s", file=sys.stderr, flush=True)
                time.sleep(pause)
                waited += pause
                pause = min(2 * pause, self.longest_pause)
        if waited:
            print(f"back at byte {self.offset:,} after {waited}s", file=sys.stderr, flush=True)
            self.paced_from = None                # no catching up on the time spent waiting
        if got:
            self.sha256.update(memoryview(buffer)[:got])
            self.offset += got
            self.pace()
        return got

    def close(self):
        if self.file is not None:
            self.file.close()
            self.file = None
        super().close()


def expected_sha256(path):
    """Lichess's SHA-256 for a month, from the <name>.sha256 the download saved beside it (sha256sum's
    format: the hash, two spaces, the name), or None."""
    try:
        with open(path + ".sha256", encoding="ascii") as f:
            return f.read().split()[0].lower()
    except (OSError, IndexError):
        return None


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


def polyglot_move(board, move):
    """A move in Polyglot's 16 bits: castling as the king taking its own rook."""
    import chess
    to = move.to_square
    if board.is_castling(move):
        to = chess.square(7 if chess.square_file(move.to_square) == 6 else 0, chess.square_rank(move.from_square))
    promotion = {None: 0, chess.KNIGHT: 1, chess.BISHOP: 2, chess.ROOK: 3, chess.QUEEN: 4}[move.promotion]
    return (chess.square_file(to) | chess.square_rank(to) << 3 | chess.square_file(move.from_square) << 6
            | chess.square_rank(move.from_square) << 9 | promotion << 12)


def count(args):
    import chess, chess.polyglot
    opts, args = options(args, {"min-elo": (2200, int), "plies": (24, int), "max-bytes": (None, int)})
    src, dst = args
    min_elo, plies = opts["min-elo"], opts["plies"]
    # Per (position key << 16 | move): an index into three parallel arrays.
    state = {"index": {}, "w": array.array("I"), "d": array.array("I"), "l": array.array("I"), "pruned": 0}
    games = kept = bad = 0
    start = time.time()

    def prune():
        index, w, d, l = state["index"], state["w"], state["d"], state["l"]
        keep, w2, d2, l2 = {}, array.array("I"), array.array("I"), array.array("I")
        for key, i in index.items():
            if w[i] + d[i] + l[i] >= 2:
                keep[key] = len(w2)
                w2.append(w[i]); d2.append(d[i]); l2.append(l[i])
        state["pruned"] += len(index) - len(keep)
        state.update(index=keep, w=w2, d=d2, l=l2)

    def finish(tags, movetext):
        nonlocal kept, bad
        t = dict(tags)
        if "FEN" in t or "Rated" not in t.get("Event", "") or t.get("Result") not in ("1-0", "0-1", "1/2-1/2"):
            return
        try:
            if int(t.get("WhiteElo", 0)) < min_elo or int(t.get("BlackElo", 0)) < min_elo:
                return
            base, inc = (int(x) for x in t.get("TimeControl", "-").split("+"))
        except ValueError:
            return
        if base + 40 * inc < 180:
            return
        white_score = {"1-0": 2, "1/2-1/2": 1, "0-1": 0}[t["Result"]]
        board = chess.Board()
        index, w, d, l = state["index"], state["w"], state["d"], state["l"]
        played = 0
        for token in COMMENT.sub(" ", movetext).split():   # comments ({ [%clk ...] }) out first
            if token[-1] == "." or token[0] == "$" or token in ("1-0", "0-1", "1/2-1/2", "*"):
                continue
            if played == plies:
                break
            try:
                move = board.parse_san(token.rstrip("?!"))   # analysed games mark moves: f4?!, d5??
            except ValueError:
                bad += 1
                return
            key = chess.polyglot.zobrist_hash(board) << 16 | polyglot_move(board, move)
            score = white_score if board.turn == chess.WHITE else 2 - white_score
            i = index.get(key)
            if i is None:
                i = index[key] = len(w)
                w.append(0); d.append(0); l.append(0)
            if score == 2: w[i] += 1
            elif score == 1: d[i] += 1
            else: l[i] += 1
            board.push(move)
            played += 1
        kept += 1
        if len(index) > PRUNE_AT:
            prune()

    steady = Steady(src)
    source = Limited(steady, opts["max-bytes"]) if opts["max-bytes"] else steady
    tags, movetext, cut = [], [], False
    with zstd.open(source, "rb") as packed:
        text = io.TextIOWrapper(packed, encoding="utf-8", newline="\n")
        try:
            for line in text:
                if line.startswith("["):
                    if movetext:
                        games += 1
                        finish(tags, " ".join(movetext))
                        tags, movetext = [], []
                        if games % 1_000_000 == 0:
                            print(f"{games:,} games, {kept:,} kept, {len(state['w']):,} position-moves, {time.time() - start:.0f}s",
                                  file=sys.stderr, flush=True)
                    name, _, value = line.strip()[1:-1].partition(" ")
                    tags.append((name, value.strip('"')))
                elif line.strip() and tags:
                    movetext.append(line.strip())
            if movetext:
                games += 1
                finish(tags, " ".join(movetext))
        except (EOFError, zstd.ZstdError) as e:
            print(f"stopped at the cut ({e}); the game it was in dropped", file=sys.stderr)
            cut = True

    # The whole file read: check it against Lichess's checksum, so a copy damaged in a download or on a disk
    # can't quietly become counts.
    sha256, checked = None, False
    if not cut and not opts["max-bytes"]:
        while steady.read(1 << 20):   # anything the decompressor didn't need to see
            pass
        sha256, expected = steady.sha256.hexdigest(), expected_sha256(src)
        if expected is None:
            print(f"no {os.path.basename(src)}.sha256 beside it, so not checked (SHA-256 {sha256})", file=sys.stderr)
        elif sha256 != expected:
            raise SystemExit(f"STOPPING: {src} doesn't match Lichess's SHA-256 ({sha256}, should be {expected}); "
                             f"it's damaged, so no counts written: download it again")
        else:
            checked = True
            print(f"SHA-256 matches Lichess's ({sha256})", file=sys.stderr)
    steady.close()
    if steady.drops:
        print(f"the file dropped out {steady.drops} times and was picked up again each time", file=sys.stderr)

    index, w, d, l = state["index"], state["w"], state["d"], state["l"]
    header = json.dumps({"source": os.path.basename(src), "games": games, "kept": kept, "min_elo": min_elo, "plies": plies,
                         "cut": cut, "pruned": state["pruned"], "records": len(index),
                         "sha256": sha256, "sha256_checked": checked}).encode()
    temp = dst + ".tmp"
    with open(temp, "wb") as out:
        out.write(MAGIC + struct.pack(">I", len(header)) + header)
        for key in sorted(index):
            i = index[key]
            out.write(RECORD.pack(key >> 16, key & 0xFFFF, w[i], d[i], l[i]))
    os.replace(temp, dst)
    print(f"{games:,} games read, {kept:,} kept (both {min_elo}+, rated, blitz or slower), {bad} with an unreadable move; "
          f"{len(index):,} position-moves saved ({state['pruned']:,} seen once dropped along the way) to {dst}; "
          f"{time.time() - start:.0f}s")


def read_counts(path):
    """A counts file's header, and an iterator over its records (sorted by position, then move)."""
    f = open(path, "rb")
    if f.read(len(MAGIC)) != MAGIC:
        raise SystemExit(f"{path}: not a counts file")
    (length,) = struct.unpack(">I", f.read(4))
    header = json.loads(f.read(length))

    def records():
        with f:
            while chunk := f.read(RECORD.size * 4096):
                yield from RECORD.iter_unpack(chunk)
    return header, records()


def verify(args):
    """Check a month against Lichess's SHA-256 without counting it (a month counted before it was kept: August
    and September 2026). With --counts, a month whose count already checked this same hash passes at once."""
    opts, args = options(args, {"counts": (None, str)})
    src = args[0]
    expected = expected_sha256(src)
    if expected is None:
        raise SystemExit(f"no {os.path.basename(src)}.sha256 beside {src}: nothing to check it against")
    if opts["counts"] and os.path.exists(opts["counts"]):
        with open(opts["counts"], "rb") as f:
            if f.read(len(MAGIC)) == MAGIC:
                (length,) = struct.unpack(">I", f.read(4))
                header = json.loads(f.read(length))
                if header.get("sha256_checked") and header.get("sha256") == expected:
                    print(f"{src}: checked when it was counted (SHA-256 {expected})")
                    return
    steady, start, report = Steady(src), time.time(), 5 * 10**9
    while steady.read(1 << 20):
        if steady.offset >= report:
            print(f"{steady.offset / 1e9:.0f} of {steady.size / 1e9:.0f} GB, {time.time() - start:.0f}s", file=sys.stderr, flush=True)
            report += 5 * 10**9
    steady.close()
    sha256 = steady.sha256.hexdigest()
    if sha256 != expected:
        raise SystemExit(f"DAMAGED: {src} doesn't match Lichess's SHA-256 ({sha256}, should be {expected}): download it again")
    print(f"{src}: SHA-256 matches Lichess's ({sha256}), {steady.size:,} bytes in {time.time() - start:.0f}s"
          + (f", the share dropped out {steady.drops} times" if steady.drops else ""))


def book(args):
    opts, args = options(args, {"min-games": (10, int), "min-score": (0.40, float)})
    import glob
    dst = args[0]
    sources = sorted(path for pattern in args[1:] for path in (glob.glob(pattern) if "*" in pattern else [pattern]))
    if not sources:
        raise SystemExit("no counts files")
    start = time.time()
    headers, streams = [], []
    for path in sources:
        header, records = read_counts(path)
        headers.append(header)
        streams.append(records)
        print(f"{path}: {header['source']}, {header['kept']:,} games kept of {header['games']:,}, "
              f"{header['records']:,} position-moves (both {header['min_elo']}+, {header['plies']} plies)")
    entries = positions = merged = 0
    temp = dst + ".tmp"
    with open(temp, "wb") as out:
        def flush(position, moves):
            nonlocal entries, positions
            chosen = [(move, 2 * w + d) for move, w, d, l in moves
                      if w + d + l >= opts["min-games"] and (w + d / 2) / (w + d + l) >= opts["min-score"]]
            if not chosen:
                return
            top = max(weight for _, weight in chosen)
            scale = min(1.0, 65535 / top) if top else 1.0
            for move, weight in sorted(chosen, key=lambda m: -m[1]):
                out.write(struct.pack(">QHHI", position, move, max(1, int(weight * scale)), 0))
                entries += 1
            positions += 1

        current, moves = None, []   # moves of the current position: [move, w, d, l], summed across files
        for position, move, w, d, l in heapq.merge(*streams):
            merged += 1
            if position != current:
                if current is not None:
                    flush(current, moves)
                current, moves = position, []
            if moves and moves[-1][0] == move:
                moves[-1][1] += w; moves[-1][2] += d; moves[-1][3] += l
            else:
                moves.append([move, w, d, l])
        if current is not None:
            flush(current, moves)
    for attempt in range(20):   # a running bot may have the old book open for a moment
        try:
            os.replace(temp, dst)
            break
        except PermissionError:
            time.sleep(0.5)
    else:
        raise SystemExit(f"couldn't replace {dst} (in use); the new book is {temp}")
    print(f"book: {entries:,} moves in {positions:,} positions from {sum(h['kept'] for h in headers):,} games "
          f"({len(sources)} counts files, {merged:,} records) -> {dst}; {time.time() - start:.0f}s")


if __name__ == "__main__":
    command, rest = (sys.argv[1], sys.argv[2:]) if len(sys.argv) > 1 else ("", [])
    if command == "count":
        count(rest)
    elif command == "book":
        book(rest)
    elif command == "verify":
        verify(rest)
    else:
        print(__doc__)
        sys.exit(1)
