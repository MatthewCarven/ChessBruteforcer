"""records.py against C#'s own records: python scripts/nnue/test-records.py (numpy only, about a second).

test-records.bin is what C# writes for the positions and labels in test-records.txt (the C# test
NnueDataTests.ThePythonReadersFixtureIsWhatCSharpWrites holds it to that). Here records.py must read the .bin back
to exactly the .txt: every piece on its square, the side to move, the score, the mate flag, the result and the
game number. Together the two tests say the Python reader decodes C#'s records to the same pieces.
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import records  # noqa: E402

FIXTURE_TXT = os.path.join(HERE, "test-records.txt")
FIXTURE_BIN = os.path.join(HERE, "test-records.bin")
FAILURES = []


def expect(what, ok):
    print(("ok    " if ok else "FAIL  ") + what)
    if not ok:
        FAILURES.append(what)


def main():
    with open(FIXTURE_TXT, encoding="utf-8") as f:
        lines = [line.strip() for line in f if line.strip()]
    data = records.read(FIXTURE_BIN)
    expect("the record is 32 bytes", records.RECORD.itemsize == 32 and records.SIZE == 32)
    expect(f"{len(data):,} records for {len(lines):,} lines", len(data) == len(lines) == 1000)

    # One record at a time, through to_fen: the line `nnue show` prints.
    shown = [records.show_line(r) for r in data]
    wrong = [i for i, (a, b) in enumerate(zip(shown, lines)) if a != b]
    expect(f"every record reads back to its line ({len(wrong)} differ{'' if not wrong else ': first ' + str(wrong[0])})", not wrong)

    # The whole batch at once, through boards(): each square's code against the FEN's letter.
    board = records.boards(data)
    mismatches = 0
    for i, line in enumerate(lines):
        placement, side = line.split("\t")[0].split(" ")[:2]
        squares = []
        for row in placement.split("/"):
            for ch in row:
                squares.extend(["."] * int(ch) if ch.isdigit() else [ch])
        expected = np.array([squares[(7 - s // 8) * 8 + s % 8] for s in range(64)])   # FEN runs from a8, squares from a1
        got = np.array([records.LETTERS[c] for c in board[i]])
        mismatches += int((expected != got).sum()) + (records.black_to_move(data[i:i + 1])[0] != (side == "b"))
    expect(f"boards() puts every piece on its square ({mismatches} wrong)", mismatches == 0)

    fields = [line.split("\t") for line in lines]
    expect("scores", [int(f[1]) for f in fields] == data["score"].tolist())
    expect("mate flags", [f[2] == "1" for f in fields] == records.is_mate(data).tolist())
    expect("results", [int(f[3]) for f in fields] == data["result"].tolist())
    expect("game numbers", [int(f[4]) for f in fields] == data["game"].tolist())
    expect("mates are +-32000, others within +-31999",
           bool(np.all(records.is_mate(data) == (np.abs(data["score"].astype(np.int32)) == records.MATE))))
    expect("no problems found in good records", records.problems(data, quiet_rule=False) == {})
    small = int((records.occupied(data).sum(axis=1) <= 5).sum())
    expect(f"the quiet rule's check finds the {small} records of 5 pieces or fewer (random play, not `nnue data`)",
           small > 0 and records.problems(data) == {"5 pieces or fewer (the rule wants more)": small})

    # problems() must see damage: a bad code, a stray half past the last piece, a king made a queen, a stray flag.
    few = np.flatnonzero(records.occupied(data).sum(axis=1) < 32)
    broken = data[few[:4]].copy()
    broken["pieces"][0, 0] = (broken["pieces"][0, 0] & 0xF0) | 7
    broken["pieces"][1, 15] |= 0xF0
    slot = int(np.flatnonzero(records.codes(broken[2:3])[0] == 6)[0])
    broken["pieces"][2, slot // 2] ^= (6 ^ 5) << (4 * (slot % 2))
    broken["flags"][3] |= 4
    found = records.problems(broken)
    expect(f"problems() sees damage ({found})",
           all(what in found for what in ("a code that means nothing on an occupied square", "a code past the last piece",
                                          "not one white king", "flags with meaningless bits")))

    check_a_data_directory(data)
    print("all passed" if not FAILURES else f"{len(FAILURES)} FAILED")
    return 1 if FAILURES else 0


def check_a_data_directory(data):
    """check() on a small directory laid out as `nnue data` lays one out: well, then with a held-back game's record
    in a bucket, then with a count off. (Game numbers renumbered 1, 2, ... so the check's per-game table is small.)"""
    import contextlib
    import io
    import json
    import tempfile

    good = data[records.occupied(data).sum(axis=1) > 5].copy()
    good["game"] = np.arange(1, len(good) + 1) // 3 + 1   # three positions a game
    held = good["game"] % 50 == 0
    buckets = [good[~held][0::2], good[~held][1::2]]
    meta = {"bucket_files": ["bucket-00.bin", "bucket-01.bin"], "bucket_counts": [len(b) for b in buckets],
            "held_back": {"every": 50, "file": "held-back.bin", "games": len(np.unique(good["game"][held])),
                          "positions": int(held.sum())},
            "counts": {"games_read": int(good["game"].max()), "games_used": len(np.unique(good["game"])),
                       "positions": len(good), "mate": int(records.is_mate(good).sum())}}

    def run(buckets, held_records, meta):
        with tempfile.TemporaryDirectory() as directory:
            for name, part in zip(meta["bucket_files"], buckets):
                part.tofile(os.path.join(directory, name))
            held_records.tofile(os.path.join(directory, "held-back.bin"))
            with open(os.path.join(directory, "data.json"), "w", encoding="utf-8") as f:
                json.dump(meta, f)
            with contextlib.redirect_stdout(io.StringIO()):
                return records.check(directory)

    expect(f"check() passes a good directory ({len(good)} records, {meta['held_back']['games']} games held back)",
           meta["held_back"]["games"] > 0 and run(buckets, good[held], meta))
    # A whole held-back game moved into a bucket, data.json's counts made to agree: only the rule can catch it.
    first = good[held]["game"][0]
    moved, kept = good[held & (good["game"] == first)], good[held & (good["game"] != first)]
    leaked = [np.concatenate([buckets[0], moved]), buckets[1]]
    expect("check() fails a held-back game's records in a bucket",
           not run(leaked, kept, dict(meta, bucket_counts=[len(b) for b in leaked],
                                      held_back=dict(meta["held_back"], positions=len(kept), games=meta["held_back"]["games"] - 1))))
    expect("check() fails a bucket whose count isn't data.json's",
           not run(buckets, good[held], dict(meta, bucket_counts=[len(buckets[0]) + 1, len(buckets[1])])))


if __name__ == "__main__":
    sys.exit(main())
