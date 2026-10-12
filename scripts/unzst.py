"""Unpack a Lichess .pgn.zst into PGN; if the file was cut short (a range download), complete games only.

    python scripts/unzst.py <in .pgn.zst> <out .pgn>

A Lichess file is many Zstandard frames one after another; zstd.open reads
across them (Python 3.14's standard library), and raises at a cut, after
everything before it has been read.  Only then is the last game, cut short,
dropped: a whole file is written as it is.  (A first version trimmed every
file, and so lost the last game of 2013-06: 224,678 of 224,679.)
"""
import os, sys
from compression import zstd

src, dst = sys.argv[1], sys.argv[2]
written, cut = 0, False
with zstd.open(src, "rb") as packed, open(dst, "wb") as out:
    try:
        while chunk := packed.read(8 << 20):
            out.write(chunk)
            written += len(chunk)
    except (EOFError, zstd.ZstdError) as e:
        print("stopped at the cut:", e)
        cut = True
if cut:
    # The last game is cut short: drop everything from its [Event tag on.
    with open(dst, "rb+") as out:
        size = out.seek(0, os.SEEK_END)
        tail = 4 << 20
        start = max(0, size - tail)
        out.seek(start)
        at = out.read().rfind(b"\n[Event ")
        if at >= 0:
            out.truncate(start + at + 1)
print(f"{written / 2**30:.2f} GB unpacked, kept {os.path.getsize(dst) / 2**30:.2f} GB"
      + (" of complete games" if cut else " (the whole file)"))
