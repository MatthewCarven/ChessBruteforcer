"""The bot's search speed from its log's "Speed: 12.4Mnps" lines, by time window (NNUE.md, decision D: train beside
the bot, and stop if its speed drops more than ~10%).

    python scripts/nnue/bot_speed.py <bot log> [--date 2026-10-17] [--min-nodes 20] [label=]HH:MM-HH:MM ...

For each window: how many moves logged a speed, and their median, mean and quartiles in millions of nodes a second;
then the windows with the same label pooled (on=00:55-01:05 off=01:06-01:15 on=...). --date picks the day (the log
is named for the day the bot started, and runs on past midnight; default: the date of its last line).
--min-nodes N keeps only searches of at least N million nodes (the "Nodes:" line just before each Speed line):
short searches run slower, and how many there are depends on the game, not on what else the laptop is doing.
Only the Nodes and Speed lines are read, nothing else from the log. A speed depends on the position too, so compare
windows that alternate (on, off, on, off) rather than one of each.
"""
import re
import statistics
import sys

LINE = re.compile(r"^(\d{4}-\d\d-\d\d) (\d\d):(\d\d):(\d\d),\d+ .*INFO (Nodes|Speed): ([\d.]+)([KM]?)(?:nps)?\s*$")
UNIT = {"": 1e-6, "K": 1e-3, "M": 1.0}


def speeds(path):
    """(date, seconds since midnight, million nodes a second, million nodes of that search) for each move logged."""
    out, nodes = [], None
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            if "Nodes:" not in line and "Speed:" not in line:
                continue
            m = LINE.match(line)
            if not m:
                continue
            day, h, mi, s, what, v, unit = m.groups()
            value = float(v) * UNIT[unit]
            if what == "Nodes":
                nodes = value
            else:
                out.append((day, int(h) * 3600 + int(mi) * 60 + int(s), value, nodes if nodes is not None else 0.0))
                nodes = None
    return out


def seconds(hhmm):
    h, m = hhmm.split(":")
    return int(h) * 3600 + int(m) * 60


def describe(v):
    if len(v) < 4:
        return f"{len(v)} moves"
    q = statistics.quantiles(v, n=4)
    return (f"{len(v):4} moves, median {statistics.median(v):5.2f} M/s, mean {statistics.fmean(v):5.2f}, "
            f"quartiles {q[0]:.2f} .. {q[2]:.2f}")


def option(args, name, default):
    if name in args:
        i = args.index(name)
        return args[:i] + args[i + 2:], args[i + 1]
    return args, default


def main(args):
    args, min_nodes = option(args, "--min-nodes", "0")
    args, day = option(args, "--date", None)
    if len(args) < 2:
        print(__doc__)
        return 1
    moves = speeds(args[0])
    day = day or (moves[-1][0] if moves else "")
    pooled = {}
    for window in args[1:]:
        label, _, span = window.rpartition("=")
        a, b = (seconds(x) for x in span.split("-"))
        v = sorted(s for d, t, s, n in moves if d == day and a <= t < b and n >= float(min_nodes))
        print(f"{window}: {describe(v)}")
        if label:
            pooled.setdefault(label, []).extend(v)
    for label, v in pooled.items():
        print(f"{label}, pooled: {describe(sorted(v))}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
