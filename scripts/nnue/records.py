"""The network's training records (NNUE.md, "Training"), as `nnue data` writes them. numpy only.

    python scripts/nnue/records.py show <records .bin> [count]   the first records, as `nnue show` prints them
    python scripts/nnue/records.py check <data dir>              every file against data.json, every board sane

A record is 32 bytes, little-endian, each field on its own alignment (src/ChessBruteforcer.Core/Records/NnueRecord.cs):

    offset  size  field
         0     8  occupancy: bit s set when square s holds a piece (a1 = 0, b1 = 1, ... h8 = 63)
         8    16  the pieces, 4 bits each, in square order: the i-th occupied square's code is in byte 8 + i // 2,
                  low half for even i, high half for odd i; unused halves are 0
        24     1  flags: bit 0 black to move, bit 1 the score is a mate
        25     1  the game's result for white: 0 lost, 1 drawn, 2 won
        26     2  white's score, int16, centipawns; a mate is +-32000 (and flagged), any other score is held to +-31999
        28     4  the game's number (uint32), counting every game in the source files

A piece's code is colour * 8 + type: white pawn 1, knight 2, bishop 3, rook 4, queen 5, king 6; black 9 to 14.
Castling rights, en passant and the move clocks are not stored.

A data directory holds data.json (written last: no data.json, no finished run), the training buckets it lists
(bucket_files), and held-back.bin: every position of the games whose number is 0 mod 50.
"""
import json
import os
import sys

import numpy as np

SIZE = 32
MATE = 32000
BLACK_TO_MOVE = 1
MATE_FLAG = 2
RECORD = np.dtype({"names": ["occupancy", "pieces", "flags", "result", "score", "game"],
                   "formats": ["<u8", ("u1", 16), "u1", "u1", "<i2", "<u4"],
                   "offsets": [0, 8, 24, 25, 26, 28], "itemsize": SIZE})
LETTERS = ".PNBRQK..pnbrqk."   # by code; '.' where a code means nothing
CHUNK = 1 << 18                # records decoded at a time (boards() holds ~600 bytes a record while it works)


def load_meta(directory):
    """data.json of a finished run (FileNotFoundError if the run never finished)."""
    with open(os.path.join(directory, "data.json"), encoding="utf-8") as f:
        return json.load(f)


def training_files(directory, meta=None):
    meta = meta or load_meta(directory)
    return [os.path.join(directory, name) for name in meta["bucket_files"]]


def held_back_file(directory, meta=None):
    meta = meta or load_meta(directory)
    return os.path.join(directory, meta["held_back"]["file"])


def count(path):
    size = os.path.getsize(path)
    if size % SIZE:
        raise ValueError(f"{path} is {size:,} bytes, not a whole number of {SIZE}-byte records")
    return size // SIZE


def read(path, start=0, n=None):
    """Records start to start + n of a file (all from start if n is None), as a structured array."""
    return np.fromfile(path, dtype=RECORD, count=-1 if n is None else n, offset=start * SIZE)


def black_to_move(records):
    return (records["flags"] & BLACK_TO_MOVE) != 0


def is_mate(records):
    return (records["flags"] & MATE_FLAG) != 0


def codes(records):
    """The 32 piece codes of each record, in square order, (n, 32) uint8; 0 past the last piece."""
    packed = records["pieces"]
    out = np.empty((len(records), 32), np.uint8)
    out[:, 0::2] = packed & 15
    out[:, 1::2] = packed >> 4
    return out


def occupied(records):
    """(n, 64) uint8, 1 where a square holds a piece (column = square, a1 = 0)."""
    occupancy = np.ascontiguousarray(records["occupancy"]).astype("<u8", copy=False)
    return np.unpackbits(occupancy.view(np.uint8).reshape(-1, 8), axis=1, bitorder="little")


def boards(records):
    """(n, 64) uint8: each square's piece code, 0 for empty (column = square, a1 = 0)."""
    out = np.zeros((len(records), 64), np.uint8)
    for at in range(0, len(records), CHUNK):
        part = records[at:at + CHUNK]
        bits = occupied(part)
        rank = np.cumsum(bits, axis=1, dtype=np.int8) - 1   # each occupied square's place among them
        rows, squares = np.nonzero(bits)
        out[at + rows, squares] = codes(part)[rows, rank[rows, squares]]
    return out


def to_fen(record):
    """One record's position: placement and side to move, then "- - 0 1" (the rest isn't stored)."""
    board = boards(np.asarray(record, dtype=RECORD).reshape(1))[0]
    ranks = []
    for rank in range(7, -1, -1):
        text, empty = "", 0
        for square in range(rank * 8, rank * 8 + 8):
            code = int(board[square])
            if code == 0:
                empty += 1
                continue
            if LETTERS[code] == ".":
                raise ValueError(f"{code} is not a piece code")
            if empty:
                text += str(empty)
            text, empty = text + LETTERS[code], 0
        ranks.append(text + (str(empty) if empty else ""))
    return "/".join(ranks) + (" b" if int(record["flags"]) & BLACK_TO_MOVE else " w") + " - - 0 1"


def show_line(record):
    """The line `nnue show` prints for a record."""
    return (f"{to_fen(record)}\t{int(record['score'])}\t{1 if int(record['flags']) & MATE_FLAG else 0}"
            f"\t{int(record['result'])}\t{int(record['game'])}")


def problems(records, quiet_rule=True):
    """What's wrong with any of these records, as {what: how many}; empty if nothing. quiet_rule: hold them to
    `nnue data`'s rule too (more than 5 pieces), not only to the format."""
    found = {}

    def note(what, bad):
        if (k := int(np.count_nonzero(bad))):
            found[what] = found.get(what, 0) + k

    c = codes(records)
    pieces = occupied(records).sum(axis=1, dtype=np.int16)
    slot = np.arange(32)
    used = slot[None, :] < pieces[:, None]
    note("more than 32 pieces", pieces > 32)
    if quiet_rule:
        note("5 pieces or fewer (the rule wants more)", pieces <= 5)
    note("a code that means nothing on an occupied square", used & ~np.isin(c, [1, 2, 3, 4, 5, 6, 9, 10, 11, 12, 13, 14]))
    note("a code past the last piece", ~used & (c != 0))
    b = boards(records)
    note("not one white king", (b == 6).sum(axis=1) != 1)
    note("not one black king", (b == 14).sum(axis=1) != 1)
    note("a pawn on the first or last rank", ((b[:, :8] & 7) == 1).any(axis=1) | ((b[:, 56:] & 7) == 1).any(axis=1))
    note("flags with meaningless bits", (records["flags"] & ~np.uint8(3)) != 0)
    note("a result other than 0, 1, 2", records["result"] > 2)
    mate, score = is_mate(records), records["score"].astype(np.int32)
    note("a mate flag without +-32000, or +-32000 without the flag", mate != (np.abs(score) == MATE))
    return found


def check(directory):
    """Every file of a run against data.json, every record looked over. Returns True if all is well."""
    meta = load_meta(directory)
    ok = True

    def fail(text):
        nonlocal ok
        ok = False
        print("  FAIL", text)

    c = meta["counts"]
    every = meta["held_back"]["every"]
    seen = np.zeros(c["games_read"] + 1, np.uint8)   # by game number: 1 in a bucket, 2 held back
    total, mates = 0, 0
    files = [(path, n, False) for path, n in zip(training_files(directory, meta), meta["bucket_counts"])]
    files.append((held_back_file(directory, meta), meta["held_back"]["positions"], True))
    for path, expected, held in files:
        n = count(path)
        if n != expected:
            fail(f"{os.path.basename(path)}: {n:,} records, data.json says {expected:,}")
        for at in range(0, n, 1 << 22):
            part = read(path, at, 1 << 22)
            for what, k in problems(part).items():
                fail(f"{os.path.basename(path)}: {k:,} records with {what}")
            games = part["game"].astype(np.int64)
            if (bad := int(np.count_nonzero(games > c["games_read"]))):
                fail(f"{os.path.basename(path)}: {bad:,} records from games past the {c['games_read']:,} read")
                games = games[games <= c["games_read"]]
            if held and (bad := int(np.count_nonzero(games % every != 0))):
                fail(f"{bad:,} held-back records from games not numbered 0 mod {every}")
            if not held and (bad := int(np.count_nonzero(games % every == 0))):
                fail(f"{os.path.basename(path)}: {bad:,} training records from held-back games")
            seen[games] |= 2 if held else 1
            total += len(part)
            mates += int(np.count_nonzero(is_mate(part)))
        print(f"  {os.path.basename(path)}: {n:,} records", flush=True)
    training, held_back, both = (int(np.count_nonzero(seen == k)) for k in (1, 2, 3))
    for name, mine, theirs in (("positions", total, c["positions"]), ("mates", mates, c["mate"]),
                               ("held-back games", held_back + both, meta["held_back"]["games"]),
                               ("games used", training + held_back + both, c["games_used"])):
        if mine != theirs:
            fail(f"{name}: {mine:,} counted, data.json says {theirs:,}")
    if both:
        fail(f"{both:,} games in both the buckets and {meta['held_back']['file']}")
    print(f"{'all well' if ok else 'PROBLEMS'}: {total:,} records, {mates:,} mates, "
          f"{training:,} training games, {held_back:,} held back")
    return ok


def main(args):
    if len(args) >= 2 and args[0] == "show":
        for record in read(args[1], 0, int(args[2]) if len(args) > 2 else 10):
            print(show_line(record))
        return 0
    if len(args) == 2 and args[0] == "check":
        return 0 if check(args[1]) else 1
    print(__doc__)
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
