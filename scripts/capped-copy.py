r"""Copy files to the desktop share at a capped rate (Matthew's rule: all share traffic <= ~8 MB/s in total).

    python scripts\capped-copy.py <dest dir> <log> <rate bytes/s> <src file or dir>...
    e.g. python scripts\capped-copy.py \\id10terror-desktop\chessbruteforcer\nnue\2026-09 copy.log 7500000 games\nnue\2026-09

Each file goes to <dest>\<name>.part, flushed to the wire every chunk (so the SMB client can't save it up and burst),
paced so the running average stays at or under the rate, then renamed. A file already at the destination with the
same size is skipped (its hash is still checked against the desktop's own later). Writes <log> (progress every 30 s)
and <log>.sha256 (sha256 of each source file as read, `<hash> *<name>`), to check against the desktop's own hashes
of the copies (Get-FileHash over SSH, read on the desktop's disk, so no second pass over the link).

At 7,500,000 B/s the laptop's Wi-Fi carried 7.32 MB/s while copying (NNUE.md, session 2b): the wheel's 2.0 GB took
280 s. A busy disk on the far side slows it (each chunk waits for its flush), never speeds it up.
"""
import hashlib
import os
import pathlib
import sys
import time

CHUNK = 1 << 20


def main(argv):
    dest, log_path, rate = pathlib.Path(argv[0]), pathlib.Path(argv[1]), float(argv[2])
    files = []
    for a in argv[3:]:
        p = pathlib.Path(a)
        files += sorted(q for q in p.iterdir() if q.is_file()) if p.is_dir() else [p]
    total = sum(f.stat().st_size for f in files)
    log = open(log_path, "a", encoding="utf-8", buffering=1)
    sums = open(str(log_path) + ".sha256", "a", encoding="utf-8", buffering=1)

    def say(text):
        log.write(f"{time.strftime('%H:%M:%S')} {text}\n")

    dest.mkdir(parents=True, exist_ok=True)
    say(f"start: {len(files)} files, {total:,} bytes to {dest} at <= {rate:,.0f} B/s")
    start, done, last = time.time(), 0, time.time()
    sent_start = start
    sent = 0
    for f in files:
        size = f.stat().st_size
        target = dest / f.name
        h = hashlib.sha256()
        if target.exists() and target.stat().st_size == size:
            with open(f, "rb") as src:
                while b := src.read(CHUNK * 8):
                    h.update(b)
            sums.write(f"{h.hexdigest()} *{f.name}\n")
            done += size
            say(f"skip {f.name} (there, {size:,} bytes)")
            continue
        part = dest / (f.name + ".part")
        with open(f, "rb") as src, open(part, "wb", buffering=0) as out:
            while b := src.read(CHUNK):
                h.update(b)
                out.write(b)
                os.fsync(out.fileno())
                sent += len(b)
                done += len(b)
                ahead = sent_start + sent / rate - time.time()
                if ahead > 0:
                    time.sleep(ahead)
                now = time.time()
                if now - last >= 30:
                    last = now
                    say(f"  {done:,} of {total:,} bytes ({done / total:.1%}), {sent / (now - sent_start) / 1e6:.2f} MB/s "
                        f"average, {f.name}")
        os.replace(part, target)
        sums.write(f"{h.hexdigest()} *{f.name}\n")
        say(f"done {f.name}: {size:,} bytes")
    dt = time.time() - start
    say(f"finished: {total:,} bytes in {dt:,.0f} s ({sent / max(time.time() - sent_start, 1e-9) / 1e6:.2f} MB/s sent)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
