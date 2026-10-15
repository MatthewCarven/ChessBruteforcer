"""Game pointers over a Lichess month (INDEX.md, session 1): a frame table into Lichess's own .pgn.zst and one
row per game in Parquet, so any game can be fetched by its number and the month counted without reading it.

    python -I scripts/index-month.py rows  <month .zst> <dir> [--workers 4] [--frames N] [--hash-all 1] [--lines 16]
                                                             [--part-rows 10000000]
    python -I scripts/index-month.py get   <month .zst> <dir> <game number>...
    python -I scripts/index-month.py check <month .zst> <dir> [--sample 1000]
    python -I scripts/index-month.py dups  <month .zst> <dir> [--examples 20]
    python -I scripts/index-month.py trial <dir>

rows: one read of the month, frame by frame, in file order (riding out the share dropping, and checked
against Lichess's SHA-256 when the whole file is read). Worker processes unpack and parse the frames; a game
cut by a frame's end is finished from the next frame's start. Games are numbered 0, 1, 2, ... in file order.
Writes <dir>/frames.cbf (the frame table), <dir>/parquet/part-NNN.parquet (the rows, --part-rows games a part,
zstd, written through DuckDB a part at a time as the read goes) and <dir>/games.json (the columns, their
dictionaries, the parts, the counts, how the read went). Then the count: against Lichess's, from the
<name>.count beside the month (its line of standard/counts.txt, which the download saves), MATCHES, or SHORT
BY n (or OVER BY n) and exit 1, the files written either way. Below-normal priority, so the bot's games come
first. --frames N reads only the first N frames; --hash-all 1 keeps every game's hash for `check` (small months
only). --lines N also writes each game's first N moves to <dir>/lines.u16 (N two-byte codes a game, 0 once the
game has ended; the moves in SAN by code in <dir>/lines.json), for the by-moves trial with Matthew's
Lookuptables.

get: a game's PGN by its number, through the frame table: one frame read and unpacked (two if the game runs
into the next frame).

check: fetches the games `rows` kept a hash of (one random game a frame, and every game cut by a frame's end;
every game with --hash-all) and compares them byte for byte, and their rows (every column) with the fetched
game parsed again, up to --sample of each kind (0: all). It checks `rows` against `get`, which share the frame
joins: a game lost at a join (July 2026 lost 13 before the fix) shows only in the count against Lichess's,
which `rows` prints. scripts/test-index-month.py tests the joins.

dups: games with exactly the same moves, from the fingerprints: groups, sizes, lengths, endings, and examples
confirmed by fetching two games of each group and comparing their moves.

trial: the format trial's three queries (it chose Parquet, 2026-10-15) on the rows, two ways, SQL in DuckDB and
numpy over the columns a part at a time, timed and their answers compared; and the rows' size column by column.

The game fingerprint: SHA-256 of the moves in SAN as Lichess writes them, joined by single spaces (no move
numbers, comments, annotation marks or result), the first 8 bytes read little-endian.
"""
import bisect, ctypes, datetime, hashlib, importlib.util, io, json, os, random, re, struct, sys, time
from collections import Counter, OrderedDict, deque
from compression import zstd

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
FRAME_MAGIC = b"CBFRAME1"
FRAME_HEAD = struct.Struct("<8sIQ32s")          # magic, frames, file size, file SHA-256
FRAME_ROW = struct.Struct("<QIIII")            # offset, packed size, plain size, first game, first game at
NOTE = struct.Struct("<III")                   # pzstd's skippable frame: magic, 4, the next frame's packed size
GAME_START = re.compile(rb"^\[Event ", re.M)
TAG = re.compile(rb'^\[(\w+) "([^"]*)"\]', re.M)
COMMENT = re.compile(rb"\{[^}]*\}")
RESULTS = {b"1-0": 0, b"0-1": 1, b"1/2-1/2": 2, b"*": 3}
COLUMNS = [                                     # name, numpy type
    ("plies", "<u2"), ("result", "u1"), ("termination", "u1"), ("white_elo", "<u2"), ("black_elo", "<u2"),
    ("clock_base", "<u2"), ("increment", "u1"), ("start", "<i4"), ("eco", "<u2"), ("flags", "u1"),
    ("fingerprint", "<u8"),
]
FLAG_EVALS, FLAG_CLOCKS, FLAG_WHITE_BOT, FLAG_BLACK_BOT, FLAG_TOURNAMENT = 1, 2, 4, 8, 16
SPEEDS = ["ultrabullet", "bullet", "blitz", "rapid", "classical"]   # Lichess: base + 40 x increment < 30 / 180 / 480 / 1500 s
PART_ROWS = 10_000_000                          # games a Parquet part (rows --part-rows): ~260 MB of rows in memory


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


def make_book():
    """make-book.py as a module (its name has a dash): its Steady reader and expected_sha256."""
    spec = importlib.util.spec_from_file_location("make_book", os.path.join(HERE, "make-book.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def below_normal():
    """Below-normal priority for this process; the worker processes inherit it."""
    if os.name != "nt":
        return
    kernel = ctypes.windll.kernel32
    kernel.GetCurrentProcess.restype = ctypes.c_void_p
    kernel.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    kernel.SetPriorityClass.restype = ctypes.c_int
    if not kernel.SetPriorityClass(kernel.GetCurrentProcess(), 0x4000):
        print("couldn't set below-normal priority", file=sys.stderr)


def duck(folder, threads=4):
    """DuckDB for a month's index: memory capped at 1 GB (the laptop has ~4 GB of commit headroom), spilling into
    the index folder rather than the working directory. Imported here, so the worker processes never load it."""
    import duckdb
    spill = os.path.join(os.path.abspath(folder), "duckdb-spill")
    return duckdb.connect(config={"memory_limit": "1GB", "threads": threads, "temp_directory": spill})


def quoted(path):
    """A path as an SQL string."""
    return "'" + path.replace("'", "''") + "'"


def names_sql(names):
    return ", ".join(f'"{name}"' for name in names)


def expected_count(path):
    """Lichess's game count for a month, from the <name>.count the download saved beside it (its line of
    standard/counts.txt: the name, a space, the count), or None."""
    try:
        with open(path + ".count", encoding="ascii") as f:
            text = f.read()
    except OSError:
        return None
    try:
        name, count = text.split()
        count = int(count)
    except ValueError:
        raise SystemExit(f"{path}.count isn't a line of Lichess's counts.txt (the name, a space, the count): {text[:80]!r}")
    if name != os.path.basename(path):
        raise SystemExit(f"{path}.count is the count of {name}")
    return count


def month_start(path):
    """The first day of the month in a Lichess file name (lichess_db_standard_rated_2026-07.pgn.zst), as an ordinal."""
    m = re.search(r"(\d{4})-(\d{2})", os.path.basename(path))
    if not m:
        raise SystemExit(f"{path}: no month in the name")
    return datetime.date(int(m[1]), int(m[2]), 1).toordinal()


def sans(movetext):
    """The moves of a game's movetext in SAN, without move numbers, comments, annotation marks or result."""
    out = []
    for token in COMMENT.sub(b"", movetext).split():
        if token.endswith(b".") or token in RESULTS or token.startswith(b"$"):
            continue
        out.append(token.rstrip(b"?!"))
    return out


def fingerprint(moves):
    return int.from_bytes(hashlib.sha256(b" ".join(moves)).digest()[:8], "little")


def parse_game(text, start_ordinal):
    """One game's row (numbers in COLUMNS order, termination and ECO as bytes for the dictionaries), and its
    moves in SAN."""
    head, _, movetext = text.partition(b"\n\n")
    tags = dict(TAG.findall(head))
    moves = sans(movetext)
    base, _, inc = tags.get(b"TimeControl", b"-").partition(b"+")
    flags = 0
    if b"[%eval" in movetext:
        flags |= FLAG_EVALS
    if b"[%clk" in movetext:
        flags |= FLAG_CLOCKS
    if tags.get(b"WhiteTitle") == b"BOT":
        flags |= FLAG_WHITE_BOT
    if tags.get(b"BlackTitle") == b"BOT":
        flags |= FLAG_BLACK_BOT
    if b"lichess.org/" in tags.get(b"Event", b""):
        flags |= FLAG_TOURNAMENT
    try:
        y, mo, d = (int(x) for x in tags[b"UTCDate"].split(b"."))
        hh, mm, ss = (int(x) for x in tags[b"UTCTime"].split(b":"))
        start = ((datetime.date(y, mo, d).toordinal() - start_ordinal) * 24 + hh) * 3600 + mm * 60 + ss
    except (KeyError, ValueError):
        start = -1
    elo = lambda name: int(tags[name]) if tags.get(name, b"?").isdigit() else 0
    return ((len(moves), RESULTS.get(tags.get(b"Result", b"*"), 3), elo(b"WhiteElo"), elo(b"BlackElo"),
             int(base) if base.isdigit() else 0, min(int(inc), 255) if inc.isdigit() else 0, start, flags,
             fingerprint(moves)),
            tags.get(b"Termination", b"?"), tags.get(b"ECO", b"?"), moves)


def split_games(text):
    """text cut at every game start after its first byte: what comes before the first such start, then each game."""
    starts = [0] + [m.start() for m in GAME_START.finditer(text, 1)]
    return [text[a:b] for a, b in zip(starts, starts[1:] + [len(text)])]


NUMERIC = ["plies", "result", "white_elo", "black_elo", "clock_base", "increment", "start", "flags", "fingerprint"]


def as_arrays(rows, terms, ecos, lines, keep):
    """Rows from parse_game as numpy columns (NUMERIC order), the termination and ECO values each as
    (distinct values, a code per game into them), and the first `keep` moves of each game as (distinct
    moves, a [games, keep] array of codes, 1 up, 0 where the game had ended): light to send between
    processes."""
    types = dict(COLUMNS)
    numeric = [np.array(values, dtype=types[name]) for name, values in zip(NUMERIC, zip(*rows))] if rows else \
              [np.zeros(0, dtype=types[name]) for name in NUMERIC]

    def coded(values):
        distinct = {}
        codes = np.array([distinct.setdefault(v, len(distinct)) for v in values], dtype=np.uint16)
        return list(distinct), codes

    moves = None
    if keep:
        distinct = {}
        codes = np.zeros((len(lines), keep), dtype=np.uint16)
        for g, line in enumerate(lines):
            codes[g, :len(line)] = [distinct.setdefault(m, len(distinct) + 1) for m in line]
        moves = (list(distinct), codes)
    return numeric, coded(terms), coded(ecos), moves


def parse_frame(task):
    """In a worker: unpack a frame and parse every game that starts and ends in it. Returns the text before
    its first game (the end of a game begun earlier), the last game (to be finished from the next frame), the
    rows, and the hashes of the games picked for `check`."""
    number, packed, start_ordinal, hash_all, keep = task
    text = zstd.decompress(packed)
    starts = [m.start() for m in GAME_START.finditer(text)]
    if not starts:
        return dict(frame=number, plain=len(text), first_at=-1, head=text, tail=None, games=0, hashes=[])
    rows, terms, ecos, lines = [], [], [], []
    for a, b in zip(starts, starts[1:]):
        row, term, eco, moves = parse_game(text[a:b], start_ordinal)
        rows.append(row)
        terms.append(term)
        ecos.append(eco)
        lines.append(moves[:keep])
    if hash_all:
        picked = range(len(rows))
    else:
        picked = [random.Random(number).randrange(len(rows))] if rows else []
    hashes = [(i, hashlib.sha256(text[starts[i]:starts[i + 1]]).hexdigest()) for i in picked]
    return dict(frame=number, plain=len(text), first_at=starts[0], head=text[:starts[0]], tail=text[starts[-1]:],
                games=len(rows), columns=as_arrays(rows, terms, ecos, lines, keep), hashes=hashes)


def frames_of(raw, size, limit):
    """(number, offset, packed bytes) for each frame, following pzstd's notes, reading the file in order."""
    pos = number = 0
    while pos < size and number < limit:
        note = raw.read(NOTE.size)
        magic, length, packed = NOTE.unpack(note) if len(note) == NOTE.size else (0, 0, 0)
        if not (0x184D2A50 <= magic <= 0x184D2A5F and length == 4):
            raise SystemExit(f"no pzstd note at byte {pos:,} (frame {number:,}): this file isn't cut the way the "
                             f"index expects")
        data = raw.read(packed)
        if len(data) != packed:
            raise SystemExit(f"frame {number:,} at byte {pos + NOTE.size:,} is cut short")
        yield number, pos + NOTE.size, data
        pos += NOTE.size + packed
        number += 1


class RowWriter:
    """The rows as they come, written to Parquet through DuckDB a part at a time (part_rows games each, the last
    fewer), into <folder>/parquet.new until install() puts them in <folder>/parquet. Only the part being gathered
    is in memory (~26 bytes a game). The opening moves (--lines) go to their own raw file every million games."""

    def __init__(self, folder, part_rows, lines_path=None):
        self.folder, self.part_rows = folder, part_rows
        self.new = os.path.join(folder, "parquet.new")
        if os.path.isdir(self.new):                     # left by a read that stopped part-way
            for old in os.listdir(self.new):
                os.remove(os.path.join(self.new, old))
        os.makedirs(self.new, exist_ok=True)
        self.batches = {name: [] for name, _ in COLUMNS}
        self.pending = self.written = 0
        self.parts, self.write_seconds, self.con = [], 0.0, None
        self.term_codes, self.eco_codes = {}, {}
        self.lines = open(lines_path, "wb") if lines_path else None
        self.line_parts, self.line_pending, self.move_codes = [], 0, {}

    def add(self, columns):
        """One batch from as_arrays: its numeric columns, its terminations and ECO codes re-coded into the
        month's dictionaries, and its opening moves likewise (when kept)."""
        numeric, terms, ecos, moves = columns
        types = dict(COLUMNS)
        for name, values in zip(NUMERIC, numeric):
            self.batches[name].append(values)
        for name, (distinct, codes), table in (("termination", terms, self.term_codes), ("eco", ecos, self.eco_codes)):
            to_month = np.array([table.setdefault(v, len(table)) for v in distinct], dtype=types[name])
            self.batches[name].append(to_month[codes] if len(codes) else np.zeros(0, dtype=types[name]))
        n = len(numeric[0])
        if self.lines is not None:
            distinct, codes = moves
            to_month = np.array([0] + [self.move_codes.setdefault(m, len(self.move_codes) + 1) for m in distinct])
            if len(self.move_codes) > 65535:
                raise SystemExit("more than 65,535 different moves in SAN: they don't fit two bytes")
            self.line_parts.append(to_month.astype("<u2")[codes])
            self.line_pending += n
            if self.line_pending >= 1_000_000:
                self.flush_lines()
        self.pending += n
        if self.pending >= self.part_rows:
            self.write_parts()

    def flush_lines(self):
        if self.line_parts:
            np.concatenate(self.line_parts).tofile(self.lines)
            self.line_parts.clear()
        self.line_pending = 0

    def write_parts(self, last=False):
        """Each full part waiting as a Parquet file; with last, what's left too (an empty part if there are no
        games at all, so a month always has one)."""
        types = dict(COLUMNS)
        while self.pending >= self.part_rows or (last and (self.pending or not self.parts)):
            take, chunk = min(self.part_rows, self.pending), {}
            for name, _ in COLUMNS:                     # a column at a time, so the batches go as they're joined
                waiting = self.batches[name]
                whole = waiting[0] if len(waiting) == 1 else np.concatenate(waiting) if waiting else np.zeros(0, types[name])
                chunk[name] = whole[:take]
                self.batches[name] = [whole[take:]] if len(whole) > take else []
            if self.con is None:                        # one thread: a 10 M-row part then costs +16 MB and 1.5 s
                self.con = duck(self.folder, threads=1) # to write, against +140-180 MB and 0.75 s on four (2026-10-15)
            path = os.path.join(self.new, f"part-{len(self.parts):03}.parquet")
            t = time.perf_counter()
            self.con.register("chunk", chunk)
            self.con.execute(f"COPY (SELECT {names_sql(chunk)} FROM chunk) TO {quoted(path)} (FORMAT parquet, COMPRESSION zstd)")
            self.con.unregister("chunk")
            self.write_seconds += time.perf_counter() - t
            self.parts.append(dict(file=os.path.basename(path), first=self.written, rows=take, bytes=os.path.getsize(path)))
            self.written += take
            self.pending -= take
            del chunk

    def finish(self):
        """The last part written and the lines file closed; returns {column: packed bytes} over all the parts,
        from the Parquet files' own metadata."""
        self.write_parts(last=True)
        if self.lines is not None:
            self.flush_lines()
            self.lines.close()
        files = "[" + ", ".join(quoted(os.path.join(self.new, p["file"])) for p in self.parts) + "]"
        sizes = dict(self.con.execute(f"SELECT path_in_schema, sum(total_compressed_size)::BIGINT FROM "
                                      f"parquet_metadata({files}) GROUP BY path_in_schema").fetchall())
        self.con.close()
        return sizes

    def install(self):
        """The new parts in <folder>/parquet, in place of an earlier run's (or the format trial's)."""
        final = os.path.join(self.folder, "parquet")
        os.makedirs(final, exist_ok=True)
        for old in os.listdir(final):
            if old.endswith(".parquet"):
                os.remove(os.path.join(final, old))
        for part in self.parts:
            os.replace(os.path.join(self.new, part["file"]), os.path.join(final, part["file"]))
        os.rmdir(self.new)

    def discard(self):
        if self.con is not None:
            self.con.close()
        if self.lines is not None:
            self.lines.close()
        for part in os.listdir(self.new):
            os.remove(os.path.join(self.new, part))
        os.rmdir(self.new)


def rows_command(args):
    o, args = options(args, {"workers": (4, int), "frames": (10**9, int), "hash-all": (0, int), "lines": (0, int),
                             "part-rows": (PART_ROWS, int)})
    path, folder = args
    keep = o["lines"]
    lichess_count = expected_count(path)                  # read first: a bad .count stops before the hour's read
    os.makedirs(folder, exist_ok=True)
    below_normal()
    import multiprocessing
    mb = make_book()
    start_ordinal = month_start(path)
    size = os.path.getsize(path)
    steady = mb.Steady(path)
    raw = io.BufferedReader(steady, 8 << 20)
    columns = RowWriter(folder, o["part-rows"], os.path.join(folder, "lines.u16") if keep else None)
    frame_rows, samples = [], {"interior": [], "boundary": []}
    tail, tail_frame, games = None, -1, 0
    stray = 0
    t0 = time.perf_counter()
    last_report = t0

    def finish_tail(tail_text):
        nonlocal games
        row, term, eco, moves = parse_game(tail_text, start_ordinal)
        samples["boundary"].append((games, hashlib.sha256(tail_text).hexdigest()))
        columns.add(as_arrays([row], [term], [eco], [moves[:keep]], keep))
        games += 1

    def carry_on(more):
        """The game still open at a frame's end, carried on with the next frame's text. A game whose [Event line
        the frame's end cut shows up only here (neither frame holds the whole line; July 2026 had 13 in 6,177
        frames), so every game before it is finished."""
        nonlocal tail
        *done, tail = split_games(tail + more)
        for text in done:
            finish_tail(text)

    def take(result, offset, packed_size):
        """Fold one frame's result in, in file order. A frame's "first game" is the number of the first game
        starting in it or after it: every game begun before it, including one still running into it."""
        nonlocal tail, tail_frame, games, stray
        if tail is not None:
            carry_on(result["head"])
        elif result["head"].strip():
            stray += 1                                    # text before the file's first game: shouldn't happen
        if result["first_at"] < 0:                        # no game starts here: all of it continues the last one
            frame_rows.append([offset, packed_size, result["plain"], games + (tail is not None), 0])
            return
        if tail is not None:
            finish_tail(tail)
            tail = None
        frame_rows.append([offset, packed_size, result["plain"], games, result["first_at"]])
        base = games
        columns.add(result["columns"])
        games += result["games"]
        samples["interior"].extend((base + i, h) for i, h in result["hashes"])
        tail, tail_frame = result["tail"], result["frame"]

    with multiprocessing.Pool(o["workers"]) as pool:
        in_flight = deque()
        for number, offset, packed in frames_of(raw, size, o["frames"]):
            in_flight.append((offset, len(packed), pool.apply_async(parse_frame, ((number, packed, start_ordinal, o["hash-all"], keep),))))
            while len(in_flight) > 2 * o["workers"] or (in_flight and in_flight[0][2].ready()):
                off, n, job = in_flight.popleft()
                take(job.get(), off, n)
            now = time.perf_counter()
            if now - last_report > 60:
                last_report = now
                print(f"  {number + 1:,} frames, {steady.offset / 1e9:.2f} of {size / 1e9:.2f} GB, {games:,} games "
                      f"({now - t0:.0f}s)", flush=True)
        while in_flight:
            off, n, job = in_flight.popleft()
            take(job.get(), off, n)
    if tail is not None:
        finish_tail(tail)
    read_seconds = time.perf_counter() - t0
    whole = bool(steady.offset >= size and frame_rows and frame_rows[-1][0] + frame_rows[-1][1] == size)
    sha = steady.sha256.hexdigest() if whole else None
    expected = mb.expected_sha256(path)
    raw.close()
    if whole and expected and sha != expected:
        columns.discard()
        raise SystemExit(f"{path}: SHA-256 {sha} isn't Lichess's {expected}; nothing written")

    sizes = columns.finish()
    if columns.written != games:
        raise SystemExit(f"wrote {columns.written:,} rows for {games:,} games")
    note_path = os.path.join(folder, "games.json")
    if os.path.exists(note_path):                         # an earlier run's: until the new one is written, the
        os.remove(note_path)                              # folder isn't an index anyone can read half-replaced
    with open(os.path.join(folder, "frames.cbf"), "wb") as f:
        f.write(FRAME_HEAD.pack(FRAME_MAGIC, len(frame_rows), size, bytes.fromhex(sha) if sha else bytes(32)))
        for row in frame_rows:
            f.write(FRAME_ROW.pack(*row))
    columns.install()
    parquet_bytes = sum(p["bytes"] for p in columns.parts)
    if not whole:
        counted, count_text = None, "read only part of the file (--frames), so not checked"
    elif lichess_count is None:
        counted, count_text = None, f"no {os.path.basename(path)}.count beside the month (the download saves it), so not checked"
    else:
        counted = games == lichess_count
        count_text = (f"{games:,} games, Lichess's counts.txt {lichess_count:,}: " +
                      ("MATCHES" if counted else f"SHORT BY {lichess_count - games:,}" if games < lichess_count
                       else f"OVER BY {games - lichess_count:,}"))
    note = dict(
        month=os.path.basename(path), file_size=size, sha256=sha, sha256_matches=(sha == expected) if whole and expected else None,
        whole_file=whole, frames=len(frame_rows), games=games, lichess_count=lichess_count, count_matches=counted,
        read_seconds=round(read_seconds, 1), write_seconds=round(columns.write_seconds, 2), share_drops=steady.drops,
        stray_text_before_first_game=stray, format="parquet", part_rows=o["part-rows"], parts=columns.parts,
        parquet_bytes=parquet_bytes,
        columns={name: dict(type=t, raw=games * np.dtype(t).itemsize, packed=int(sizes.get(name, 0))) for name, t in COLUMNS},
        terminations=[t.decode() for t in sorted(columns.term_codes, key=columns.term_codes.get)],
        ecos=[e.decode() for e in sorted(columns.eco_codes, key=columns.eco_codes.get)],
        samples=samples, hash_all=bool(o["hash-all"]),
    )
    with open(note_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(note, f)
    if keep:
        with open(os.path.join(folder, "lines.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(dict(plies=keep, games=games, moves=[""] + [m.decode() for m in
                      sorted(columns.move_codes, key=columns.move_codes.get)]), f)
        print(f"lines.u16: the first {keep} moves of each game, {len(columns.move_codes):,} different moves in SAN, "
              f"{os.path.getsize(os.path.join(folder, 'lines.u16')) / 1e6:.1f} MB")
    print(f"{path}: {len(frame_rows):,} frames, {games:,} games in {read_seconds:.0f}s "
          f"({steady.offset / 1e6 / read_seconds:.1f} MB/s); rows {parquet_bytes / 1e6:.1f} MB of Parquet in "
          f"{len(columns.parts)} part(s) ({parquet_bytes / max(games, 1):.2f} bytes a game), written in "
          f"{columns.write_seconds:.1f}s")
    if whole:
        print(f"SHA-256: {'matches Lichess' if expected and sha == expected else 'no .sha256 to check against' if not expected else 'MISMATCH'}")
    else:
        print(f"read {steady.offset:,} of {size:,} bytes (--frames): no SHA-256 check")
    print(f"count: {count_text}")
    if stray:
        print(f"warning: {stray} frame(s) began with text before any game")
    if os.path.isdir(os.path.join(folder, "games")):
        print(f"note: {os.path.join(folder, 'games')} holds plain .zst columns from before the rows went to Parquet; "
              f"nothing reads them now (scripts/clean-up.ps1 offers them)")
    if counted is False:
        raise SystemExit(1)


CHUNK = 10_000_000                              # rows at a time, when a month's columns are worked in pieces


def read_note(folder):
    """A month's games.json, refusing one from before the rows went to Parquet, or one whose parts don't match."""
    path = os.path.join(folder, "games.json")
    if not os.path.exists(path):
        raise SystemExit(f"{path} isn't there: run rows (an index whose rows stopped part-way has none)")
    with open(path, encoding="utf-8") as f:
        note = json.load(f)
    if note.get("format") != "parquet":
        raise SystemExit(f"{folder} was indexed before the rows went to Parquet (plain .zst columns): run rows again")
    for part in note["parts"]:
        p = os.path.join(folder, "parquet", part["file"])
        if not os.path.exists(p) or os.path.getsize(p) != part["bytes"]:
            raise SystemExit(f"{p} isn't the part rows wrote ({part['bytes']:,} bytes)")
    return note


def part_paths(folder, note):
    return [os.path.join(folder, "parquet", part["file"]) for part in note["parts"]]


def column_pieces(folder, note, names):
    """(first game, {name: array}) for each Parquet part in order: the named columns, a part at a time."""
    types = dict(COLUMNS)
    con = duck(folder)
    try:
        for part, path in zip(note["parts"], part_paths(folder, note)):
            got = con.execute(f"SELECT {names_sql(names)} FROM read_parquet({quoted(path)})").fetchnumpy()
            piece = {name: np.asarray(got[name], dtype=types[name]) for name in names}
            del got
            if any(len(a) != part["rows"] for a in piece.values()):
                raise SystemExit(f"{path} gave {len(next(iter(piece.values()))):,} rows, not {part['rows']:,}")
            yield part["first"], piece
    finally:
        con.close()


def load_column(folder, note, name):
    """One column whole, filled a part at a time: the array and one part's piece in memory."""
    out = np.empty(note["games"], dict(COLUMNS)[name])
    for first, piece in column_pieces(folder, note, [name]):
        out[first:first + len(piece[name])] = piece[name]
    return out


def rows_of(folder, note, numbers):
    """Every column of the games numbered `numbers` (distinct, ascending), from the parts that hold them."""
    numbers = np.asarray(numbers, np.int64)
    out = {name: np.zeros(len(numbers), t) for name, t in COLUMNS}
    con = duck(folder)
    try:
        for part, path in zip(note["parts"], part_paths(folder, note)):
            lo, hi = np.searchsorted(numbers, [part["first"], part["first"] + part["rows"]])
            if lo == hi:
                continue
            wanted = numbers[lo:hi] - part["first"]
            con.register("wanted", {"r": wanted})
            got = con.execute(f"SELECT file_row_number, {names_sql(name for name, _ in COLUMNS)} FROM read_parquet("
                              f"{quoted(path)}, file_row_number = true) JOIN wanted ON file_row_number = r "
                              f"ORDER BY file_row_number").fetchnumpy()
            con.unregister("wanted")
            if not np.array_equal(got["file_row_number"], wanted):
                raise SystemExit(f"{path}: games {numbers[lo]:,} to {numbers[hi - 1]:,} aren't all there")
            for name, _ in COLUMNS:
                out[name][lo:hi] = got[name]
    finally:
        con.close()
    return out


def duplicate_groups(fp):
    """The fingerprints that 2 or more games share (in order) and how many games share each, and how many
    distinct fingerprints there are. One sorted copy of the column, then a few bytes a game."""
    s = np.sort(fp)
    eq = s[1:] == s[:-1]
    distinct = len(s) - int(eq.sum())
    edges = np.diff(np.concatenate((np.zeros(1, np.int8), eq.view(np.int8), np.zeros(1, np.int8))))
    del eq
    starts, ends = np.flatnonzero(edges == 1), np.flatnonzero(edges == -1)
    del edges
    values = s[starts]
    del s
    return values, ends - starts + 1, distinct


def in_groups(fp, values):
    """Which of the fingerprints `fp` are among the sorted `values`, a piece at a time."""
    out = np.zeros(len(fp), bool)
    if len(values):
        for start in range(0, len(fp), CHUNK):
            part = fp[start:start + CHUNK]
            i = np.minimum(np.searchsorted(values, part), len(values) - 1)
            out[start:start + CHUNK] = values[i] == part
    return out


class Index:
    """A month's frame table and rows, and games fetched by number."""

    def __init__(self, path, folder):
        self.path, self.folder = path, folder
        with open(os.path.join(folder, "frames.cbf"), "rb") as f:
            magic, count, size, sha = FRAME_HEAD.unpack(f.read(FRAME_HEAD.size))
            if magic != FRAME_MAGIC:
                raise SystemExit(f"{folder}/frames.cbf isn't a frame table")
            self.frames = [FRAME_ROW.unpack(f.read(FRAME_ROW.size)) for _ in range(count)]
        if os.path.getsize(path) != size:
            raise SystemExit(f"{path} is {os.path.getsize(path):,} bytes; the frame table was made from one of {size:,}")
        self.note = read_note(folder)
        if self.note["frames"] != count:
            raise SystemExit(f"{folder}: games.json has {self.note['frames']:,} frames, frames.cbf {count:,}")
        self.first = [row[3] for row in self.frames]
        self.cache = OrderedDict()                      # frame number -> (text, game starts)
        self.file = open(path, "rb")
        self.frame_reads = 0

    def column(self, name):
        return load_column(self.folder, self.note, name)

    def rows(self, numbers):
        return rows_of(self.folder, self.note, numbers)

    def frame(self, number):
        if number in self.cache:
            self.cache.move_to_end(number)
            return self.cache[number]
        offset, packed, plain, _, _ = self.frames[number]
        self.file.seek(offset)
        text = zstd.decompress(self.file.read(packed))
        self.frame_reads += 1
        if len(text) != plain:
            raise SystemExit(f"frame {number:,} unpacked to {len(text):,} bytes, not {plain:,}")
        entry = (text, [m.start() for m in GAME_START.finditer(text)])
        self.cache[number] = entry
        if len(self.cache) > 4:
            self.cache.popitem(last=False)
        return entry

    def game(self, number):
        """The full PGN text of game `number`, from its [Event line to the next game's."""
        if not 0 <= number < self.note["games"]:
            raise SystemExit(f"game {number:,}: the month has {self.note['games']:,} (0 to {self.note['games'] - 1:,})")
        f = bisect.bisect_right(self.first, number) - 1
        text, starts = self.frame(f)
        k = number - self.first[f]
        if k + 1 < len(starts):
            return text[starts[k]:starts[k + 1]]
        # The frame's last game, or one after it whose [Event line the frame's end cuts (neither frame holds the
        # whole line): from the last game start a frame shows (an earlier frame's, if this one shows none) to the
        # next one, split at every game start in between.
        while not starts:
            f -= 1
            text, starts = self.frame(f)
        last = self.first[f] + len(starts) - 1
        out = [text[starts[-1]:]]
        while f + 1 < len(self.frames):              # it runs into the next frame(s)
            f += 1
            text, starts = self.frame(f)
            if starts:
                out.append(text[:starts[0]])
                break
            out.append(text)
        games = split_games(b"".join(out))
        if number - last >= len(games):
            raise SystemExit(f"game {number:,}: the frame table and the file disagree")
        return games[number - last]


def get_command(args):
    path, folder, *numbers = args
    index = Index(path, folder)
    for n in numbers:
        sys.stdout.buffer.write(index.game(int(n)))
    sys.stdout.buffer.flush()


def check_command(args):
    o, args = options(args, {"sample": (1000, int)})
    path, folder = args
    index = Index(path, folder)
    t = time.perf_counter()
    bad = 0
    lines = None
    if os.path.exists(os.path.join(folder, "lines.json")):
        with open(os.path.join(folder, "lines.json"), encoding="utf-8") as f:
            lines = json.load(f)
        table = np.fromfile(os.path.join(folder, "lines.u16"), dtype="<u2").reshape(-1, lines["plies"])
    start_ordinal = month_start(path)
    terminations, ecos = index.note["terminations"], index.note["ecos"]
    for kind in ("interior", "boundary"):
        kept = index.note["samples"][kind]
        chosen = kept if o["sample"] <= 0 or o["sample"] >= len(kept) else random.Random(1).sample(kept, o["sample"])
        chosen = sorted(set(map(tuple, chosen)))
        stored = index.rows([n for n, _ in chosen])
        wrong, wrong_lines, wrong_rows = [], [], []
        for i, (n, h) in enumerate(chosen):
            text = index.game(n)
            if hashlib.sha256(text).hexdigest() != h:
                wrong.append(n)
            row, term, eco, moves = parse_game(text, start_ordinal)
            if (any(int(stored[name][i]) != value for name, value in zip(NUMERIC, row))
                    or terminations[stored["termination"][i]] != term.decode()
                    or ecos[stored["eco"][i]] != eco.decode()):
                wrong_rows.append(n)
            if lines is not None:
                saved = [lines["moves"][c] for c in table[n] if c]
                if saved != [m.decode() for m in moves[:lines["plies"]]]:
                    wrong_lines.append(n)
        bad += len(wrong) + len(wrong_lines) + len(wrong_rows)
        print(f"{kind}: {len(chosen) - len(wrong):,} of {len(chosen):,} games fetched by number match the read"
              + (f"; wrong: {wrong[:10]}" if wrong else ""))
        print(f"{kind}: {len(chosen) - len(wrong_rows):,} of {len(chosen):,} have their row right (all {len(COLUMNS)} "
              f"columns, against the game fetched and parsed again)" + (f"; wrong: {wrong_rows[:10]}" if wrong_rows else ""))
        if lines is not None:
            print(f"{kind}: {len(chosen) - len(wrong_lines):,} of {len(chosen):,} have their first {lines['plies']} moves "
                  f"saved right" + (f"; wrong: {wrong_lines[:10]}" if wrong_lines else ""))
    print(f"{index.frame_reads:,} frame reads, {time.perf_counter() - t:.0f}s")
    if bad:
        raise SystemExit(1)


def dups_command(args):
    o, args = options(args, {"examples": (20, int)})
    path, folder = args
    index = Index(path, folder)
    fp = index.column("fingerprint")
    n = len(fp)
    values, sizes, distinct = duplicate_groups(fp)
    dup_games = in_groups(fp, values)
    groups = len(values)
    print(f"{n:,} games, {distinct:,} distinct move sequences")
    print(f"games sharing their exact moves with another: {dup_games.sum():,} ({100 * dup_games.mean():.2f}%), "
          f"in {groups:,} groups; beyond the first of each group: {dup_games.sum() - groups:,} "
          f"({100 * (dup_games.sum() - groups) / n:.2f}%)")
    print("\ngroup size    groups      games")
    for lo, hi in ((2, 2), (3, 9), (10, 99), (100, 999), (1000, 10**12)):
        m = (sizes >= lo) & (sizes <= hi)
        print(f"{f'{lo}-{hi}' if hi < 10**12 else f'{lo}+':>10} {int(m.sum()):>9,} {int(sizes[m].sum()):>10,}")
    plies = index.column("plies")
    print("\nplies     all games   duplicated   share duplicated")
    for lo, hi in ((0, 0), (1, 4), (5, 10), (11, 20), (21, 40), (41, 80), (81, 100000)):
        m = (plies >= lo) & (plies <= hi)
        k = int((m & dup_games).sum())
        print(f"{f'{lo}-{hi}' if hi < 100000 else f'{lo}+':>7} {int(m.sum()):>11,} {k:>12,}   {100 * k / max(int(m.sum()), 1):6.2f}%")
    longest = int(plies[dup_games].max()) if dup_games.any() else 0
    del plies
    print(f"the longest duplicated game: {longest} plies")
    term = index.column("termination")
    names = index.note["terminations"]
    print("\nhow duplicated games ended: " + ", ".join(
        f"{names[t]} {100 * c / max(int(dup_games.sum()), 1):.1f}%"
        for t, c in zip(*np.unique(term[dup_games], return_counts=True))))
    del term
    biggest = np.argsort(-sizes, kind="stable")[:o["examples"]]
    print(f"\nthe {len(biggest)} biggest groups (moves confirmed by fetching two games of each):")
    for g in biggest:
        a, b = (int(x) for x in np.flatnonzero(fp == values[g])[:2])
        ma, mb = (sans(index.game(x).partition(b"\n\n")[2]) for x in (a, b))
        same = "same moves" if ma == mb else "DIFFERENT MOVES"
        line = " ".join(m.decode() for m in ma[:16]) + (" ..." if len(ma) > 16 else "")
        print(f"  {int(sizes[g]):>7,} games, {len(ma)} plies ({same}; e.g. game {a:,}): {line or '(no moves)'}")


def trial_command(args):
    (folder,) = args
    note = read_note(folder)
    games = note["games"]
    parquet_size = note["parquet_bytes"]

    def speed_of(base, inc):
        return np.digitize(base.astype(np.int64) + 40 * inc.astype(np.int64), [30, 180, 480, 1500])

    def numpy_queries():
        q1, q2 = np.zeros(20, np.int64), 0
        for _, c in column_pieces(folder, note, ["clock_base", "increment", "result", "plies", "white_elo", "black_elo"]):
            q1 += np.bincount(speed_of(c["clock_base"], c["increment"]) * 4 + c["result"], minlength=20)
            q2 += int(((c["plies"] <= 20) & (c["white_elo"] >= 2000) & (c["black_elo"] >= 2000)).sum())
        _, counts, _ = duplicate_groups(load_column(folder, note, "fingerprint"))
        sizes, groups = np.unique(counts, return_counts=True)
        return q1.reshape(5, 4), q2, {int(s): int(g) for s, g in zip(sizes, groups)}

    def sql_queries():
        # DuckDB gets the memory the fingerprints' group-by would otherwise take (~0.7 GB of keys for July) capped
        # at 1 GB, spilling into the index folder.
        con = duck(folder)
        p = "read_parquet([" + ", ".join(quoted(x) for x in part_paths(folder, note)) + "])"
        est = "(clock_base::INTEGER + 40 * increment::INTEGER)"
        speed = f"CASE WHEN {est} < 30 THEN 0 WHEN {est} < 180 THEN 1 WHEN {est} < 480 THEN 2 WHEN {est} < 1500 THEN 3 ELSE 4 END"
        q1 = np.zeros((5, 4), np.int64)
        for s, r, k in con.execute(f"SELECT {speed} AS s, result, count(*) FROM {p} GROUP BY s, result").fetchall():
            q1[s, r] = k
        q2 = con.execute(f"SELECT count(*) FROM {p} WHERE plies <= 20 AND white_elo >= 2000 AND black_elo >= 2000").fetchone()[0]
        q3 = dict(con.execute(f"SELECT n, count(*) FROM (SELECT count(*) AS n FROM {p} GROUP BY fingerprint) "
                              f"WHERE n > 1 GROUP BY n").fetchall())
        con.close()
        return q1, q2, q3

    def timed(fn, reps=3):
        best, answer = None, None
        for _ in range(reps):
            t = time.perf_counter()
            answer = fn()
            s = time.perf_counter() - t
            best = s if best is None else min(best, s)
        return best, answer

    pt, pa = timed(numpy_queries)
    qt, qa = timed(sql_queries)
    same = np.array_equal(pa[0], qa[0]) and pa[1] == qa[1] and pa[2] == qa[2]
    print(f"{games:,} games; rows {parquet_size / 1e6:.1f} MB of Parquet in {len(note['parts'])} part(s), "
          f"{parquet_size / max(games, 1):.2f} bytes a game, written in {note['write_seconds']:.2f}s during the read")
    packed = sum(c["packed"] for c in note["columns"].values())
    print(f"\n{'column':12} {'raw MB':>9} {'packed MB':>10} {'bytes a game':>13} {'share':>7}")
    for name, c in note["columns"].items():
        print(f"{name:12} {c['raw'] / 1e6:>9.1f} {c['packed'] / 1e6:>10.1f} {c['packed'] / max(games, 1):>13.2f} "
              f"{100 * c['packed'] / max(packed, 1):>6.1f}%")
    print(f"{'all':12} {sum(c['raw'] for c in note['columns'].values()) / 1e6:>9.1f} {packed / 1e6:>10.1f} "
          f"{packed / max(games, 1):>13.2f}   (the rest of the files, {(parquet_size - packed) / 1e6:.2f} MB, is Parquet's own)")
    print(f"\nthree queries (best of 3): SQL in DuckDB {qt:.2f}s, numpy over the columns {pt:.2f}s (each reading what it "
          f"needs from disk)")
    print(f"answers agree: {'yes' if same else 'NO'}")
    print("\ngames by speed and result (white won / black won / drawn / unfinished):")
    for i, name in enumerate(SPEEDS):
        row = pa[0][i]
        print(f"  {name:12} {int(row.sum()):>12,}   " + " / ".join(f"{100 * x / max(int(row.sum()), 1):.1f}%" for x in row))
    print(f"20 plies or fewer, both players 2000+: {pa[1]:,}")
    print("duplicate groups by size: " + ", ".join(f"{s}: {g:,}" for s, g in sorted(pa[2].items())[:8]))
    if not same:
        raise SystemExit(1)


if __name__ == "__main__":
    commands = {"rows": rows_command, "get": get_command, "check": check_command, "dups": dups_command,
                "trial": trial_command}
    if len(sys.argv) < 2 or sys.argv[1] not in commands:
        raise SystemExit(__doc__)
    commands[sys.argv[1]](sys.argv[2:])
