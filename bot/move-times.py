"""How long each side takes a move in the bot's games, from the clocks lichess.org writes into each PGN.

    python bot\\move-times.py [games folder] [--since YYYY-MM-DD | --since "YYYY-MM-DD HH:MM"]

--since is this machine's local time, as the bot's log and Matthew read it. The PGN's UTCDate/UTCTime are UTC, and the
laptop is on UTC+11, so its mornings carry the day before in the tags (06:59 on 2026-10-25 here is 2026.10.24 19:59);
the script converts, so `--since "2026-10-25 06:55"` means the bot's 06:55 start that morning.

Reads every .pgn in bot\\games (the bot's own games, one a file). A move's time is the mover's clock before it, plus
the increment, less the clock after it ([%clk] has whole seconds, so single moves are rough; the medians and means
over many aren't). Each side's first move is left out: lichess.org starts the clocks after both have moved.

Reported by speed (bullet, blitz, rapid, classical, from the Event line): seconds a move, ours and the opponent's, by
move number; the share of the clock used by the game's end; time losses. Then ours again, split by whether another
of the bot's games was on at the same time (its start from UTCDate/UTCTime, its end from the file's write time, which
lichess-bot sets when the game ends): the measure for playing several games at once (2026-10-25: up to three).
"""
import datetime
import os
import re
import statistics
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ME = "matthewcarven"
SPEEDS = ("bullet", "blitz", "rapid", "classical")
BANDS = ((2, 10), (11, 20), (21, 40), (41, 999))   # move numbers (a move is White's and Black's)
CLK = re.compile(r"\[%clk (\d+):(\d\d):(\d\d)\]")
TAG = re.compile(r'^\[(\w+) "(.*)"\]$', re.M)


def read(path):
    """One game: its tags, the clock after each ply (seconds), and its start and end (UTC)."""
    with open(path, encoding="utf-8", errors="replace") as f:
        text = f.read()
    tags = dict(TAG.findall(text))
    clocks = [int(h) * 3600 + int(m) * 60 + int(s) for h, m, s in CLK.findall(text)]
    start = datetime.datetime.strptime(f"{tags.get('UTCDate', '')} {tags.get('UTCTime', '')}", "%Y.%m.%d %H:%M:%S")
    start = start.replace(tzinfo=datetime.timezone.utc)
    end = datetime.datetime.fromtimestamp(os.path.getmtime(path), datetime.timezone.utc)
    return tags, clocks, start, end


def spent(clocks, inc):
    """[(ply, seconds)] for every ply from each side's second move on."""
    return [(i, clocks[i - 2] + inc - clocks[i]) for i in range(2, len(clocks))]


def local_time(text):
    """--since's local date, or date and time, as an aware datetime (compares with the games' UTC starts)."""
    for fmt in ("%Y-%m-%d %H:%M", "%Y-%m-%d"):
        try:
            return datetime.datetime.strptime(text, fmt).astimezone()   # naive -> this machine's zone
        except ValueError:
            pass
    raise SystemExit(f'--since wants YYYY-MM-DD or "YYYY-MM-DD HH:MM" (local time), not {text!r}')


def describe(xs):
    if not xs:
        return "-"
    return f"{statistics.median(xs):.1f} / {statistics.mean(xs):.1f}"


def main(args):
    folder = os.path.join(HERE, "games")
    since = None
    rest = list(args)
    if "--since" in rest:
        i = rest.index("--since")
        since = local_time(rest[i + 1])
        del rest[i:i + 2]
    if rest:
        folder = rest[0]

    games = []
    for name in sorted(os.listdir(folder)):
        if not name.endswith(".pgn"):
            continue
        tags, clocks, start, end = read(os.path.join(folder, name))
        if since and start < since:
            continue
        if ME not in (tags.get("White", "").lower(), tags.get("Black", "").lower()) or "+" not in tags.get("TimeControl", ""):
            continue
        speed = next((s for s in SPEEDS if s in tags.get("Event", "")), None)
        if speed is None or len(clocks) < 4:
            continue
        base, inc = (int(x) for x in tags["TimeControl"].split("+"))
        games.append({"tags": tags, "clocks": clocks, "start": start, "end": max(end, start), "speed": speed,
                      "base": base, "inc": inc, "us": 0 if tags["White"].lower() == ME else 1})

    # Another of the bot's games on at the same time, for any part of this one.
    by_start = sorted(games, key=lambda g: g["start"])
    for g in games:
        g["overlap"] = any(o is not g and o["start"] < g["end"] and g["start"] < o["end"] for o in by_start)

    print(f"{len(games)} games with clocks in {folder}" + (f", from {since:%Y-%m-%d %H:%M} local" if since else ""))
    print("Seconds a move, median / mean (whole-second clocks); each side's first move left out.\n")
    for speed in SPEEDS:
        sel = [g for g in games if g["speed"] == speed]
        if not sel:
            continue
        clocks_seen = sorted({f"{g['base'] // 60 if g['base'] >= 60 else g['base'] / 60:g}+{g['inc']}" for g in sel})
        print(f"## {speed}: {len(sel)} games ({', '.join(clocks_seen)})")
        print(f"  {'moves':<10} {'ours':>14} {'theirs':>14}")
        for lo, hi in BANDS + ((2, 999),):
            ours, theirs = [], []
            for g in sel:
                for ply, s in spent(g["clocks"], g["inc"]):
                    move = ply // 2 + 1
                    if lo <= move <= hi:
                        (ours if ply % 2 == g["us"] else theirs).append(s)
            label = "all" if (lo, hi) == (2, 999) else (f"{lo}-{hi}" if hi < 999 else f"{lo}+")
            print(f"  {label:<10} {describe(ours):>14} {describe(theirs):>14}   ({len(ours):,} and {len(theirs):,} moves)")
        used_us, used_them, flagged_us, flagged_them = [], [], 0, 0
        for g in sel:
            c = g["clocks"]
            moves = [len(c[g["us"]::2]), len(c[1 - g["us"]::2])]
            last = [c[g["us"]::2][-1], c[1 - g["us"]::2][-1]]
            budget = [g["base"] + g["inc"] * max(0, m - 1) for m in moves]
            used_us.append(100 * (budget[0] - last[0]) / budget[0])
            used_them.append(100 * (budget[1] - last[1]) / budget[1])
            result = g["tags"].get("Result", "")
            if g["tags"].get("Termination") == "Time forfeit" and result in ("1-0", "0-1"):
                we_lost = (result == "0-1") if g["us"] == 0 else (result == "1-0")
                flagged_us += we_lost
                flagged_them += not we_lost
        print(f"  clock used by the end, median: ours {statistics.median(used_us):.0f}%, "
              f"theirs {statistics.median(used_them):.0f}%")
        print(f"  lost on time: us {flagged_us}, them {flagged_them}")
        for label, want in (("alone", False), ("beside another game", True)):
            ours = [s for g in sel if g["overlap"] == want for ply, s in spent(g["clocks"], g["inc"]) if ply % 2 == g["us"]]
            n = sum(g["overlap"] == want for g in sel)
            print(f"  ours, {label}: {describe(ours)} ({n} games)")
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
