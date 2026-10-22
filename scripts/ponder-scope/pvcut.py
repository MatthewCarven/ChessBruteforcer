"""How often the line the engine prints (its PV) is cut short: lichess-bot's DEBUG logs, every search that played a move.

A PV node that takes a deep-enough table entry returns without the line under it, so a depth-15 search can print a
one-move line (PONDER.md, "Does the bot keep a plan?", option (a)).  For each played move: the last finished depth's
line length against its depth; and for every info line from depth 8 on.

usage: python scripts/ponder-scope/pvcut.py <DEBUG log> [<log> ...] [--split HH:MM:SS]   (plain Python)
--split: report the searches before and after that time of day apart (e.g. a restart onto a new build).
"""
import re
import statistics as st
import sys

LINE = re.compile(r"^(\d{4}-\d\d-\d\d) (\d\d:\d\d:\d\d),\d{3} chess\.engine .*?<UciProtocol \(pid=(\d+)\)>: (<<|>>) (.*)$")
INFO = re.compile(r"^info depth (\d+) score \S+ -?\d+( lowerbound)? .*? pv ?(.*)$")


def parse(paths):
    played, lines = [], []
    current = {}
    for path in paths:
        with open(path, encoding="utf-8", errors="replace") as f:
            for raw in f:
                m = LINE.match(raw.rstrip("\n"))
                if not m:
                    continue
                clock, pid, way, text = m.group(2), m.group(3), m.group(4), m.group(5)
                s = current.get(pid)
                if way == "<<":
                    if text.startswith("go"):
                        current[pid] = {"kind": "ponder" if text.startswith("go ponder") else "go", "infos": [],
                                        "hit": False, "stopped": False, "clock": clock}
                    elif text == "ponderhit" and s:
                        s["hit"] = True
                    elif text == "stop" and s:
                        s["stopped"] = True
                elif s is not None:
                    mi = INFO.match(text)
                    if mi:
                        s["infos"].append((int(mi.group(1)), bool(mi.group(2)), len(mi.group(3).split())))
                    elif text.startswith("bestmove"):
                        current[pid] = None
                        if s["kind"] == "go" or s["hit"]:
                            full = [i for i in s["infos"] if not i[1]]
                            if full:
                                played.append({"kind": "hit" if s["kind"] == "ponder" else "go", "clock": s["clock"],
                                               "depth": full[-1][0], "len": full[-1][2]})
                        lines.extend({"kind": s["kind"], "clock": s["clock"], "depth": d, "len": n}
                                     for d, partial, n in s["infos"] if not partial and d >= 8)
    return played, lines


def table(name, group):
    if not group:
        return
    lens = [g["len"] for g in group]
    short = sum(1 for g in group if g["len"] <= 3)
    half = sum(1 for g in group if g["len"] < g["depth"] / 2)
    one = sum(1 for g in group if g["len"] == 1)
    print(f"| {name} | {len(group)} | {st.median(g['depth'] for g in group):.0f} | {st.median(lens):.0f} | "
          f"{one} ({one / len(group):.0%}) | {short} ({short / len(group):.0%}) | {half} ({half / len(group):.0%}) |")


def main():
    args = sys.argv[1:]
    split = None
    if "--split" in args:
        split = args[args.index("--split") + 1]
        args = [a for a in args if a not in ("--split", split)]
    played, lines = parse(args)
    tables = [g for g in played if g["depth"] == 1]   # an endgame-table move: depth 1, its one move by design
    played = [g for g in played if g["depth"] > 1]
    print(f"(left out: {len(tables)} moves from the endgame tables, reported as depth 1)\n")
    parts =[("all", lambda g: True)] if split is None else [
        (f"before {split}", lambda g: g["clock"] < split), (f"from {split}", lambda g: g["clock"] >= split)]
    print("| moves played, by the search that played them | moves | depth (median) | PV plies (median) | "
          "1 ply | 1-3 plies | under half the depth |")
    print("|---|---:|---:|---:|---:|---:|---:|")
    for label, keep in parts:
        table(f"{label}: all", [g for g in played if keep(g)])
        table(f"{label}: plain go", [g for g in played if keep(g) and g["kind"] == "go"])
        table(f"{label}: ponder hit", [g for g in played if keep(g) and g["kind"] == "hit"])
    print("\n| every finished depth 8+ (info lines) | lines | depth (median) | PV plies (median) | 1 ply | 1-3 plies | "
          "under half the depth |")
    print("|---|---:|---:|---:|---:|---:|---:|")
    for label, keep in parts:
        table(f"{label}: go", [g for g in lines if keep(g) and g["kind"] == "go"])
        table(f"{label}: go ponder", [g for g in lines if keep(g) and g["kind"] == "ponder"])


if __name__ == "__main__":
    main()
