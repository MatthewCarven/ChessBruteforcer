"""Check the 5-piece pawn tables against Lichess: mate distances (Gaviota) and the 50-move rule (Syzygy).

    python scripts/pawn-tables-check.py <cli bin dir> <tables dir> <five-piece.log> <results .tsv>

Per table: the longest mate and the longest DTZ position for each side to move,
taken from the build's log (tables/five-piece.log, written by build-five-piece.ps1
-Pawns), and three random legal positions.  Each is probed with our `probe`
(mate distance, and the result under the rule) and with the Lichess tablebase
API, and both are compared: our mate in N plies against Lichess's dtm, and our
result and distance under the rule against Syzygy's category and dtz (cursed
wins and blessed losses are draws under the rule).  Lichess reports a mated
position as dtz -1 where we store 0, and may round dtz by one ply (then
precise_dtz is null): both allowed for, as in syzygy-check.py.
"""
import json, os, random, re, subprocess, sys, time, urllib.request

BIN, TABLES, LOG, RESULTS = sys.argv[1:5]
CLI = os.path.join(BIN, "ChessBruteforcer.Cli.dll")
ENV = dict(os.environ, CHESS_TABLES=TABLES)
random.seed(20261009)

# The longest positions, from the pawn run's log (the last solve of each table counts).
longest = {}
step = table = None
lines = open(LOG, encoding="utf-8").read().splitlines()
lines = lines[next(i for i, l in enumerate(lines) if "(with pawns)" in l):]
for line in lines:
    m = re.match(r"\S+ \S+  (solve|dtz) (\w+): starting", line)
    if m:
        step, table = m.groups()
        longest.setdefault(table, {})[step] = []
        continue
    m = re.search(r"longest: .*e\.g\. (.+)$", line)
    if m and table and step:
        longest[table][step].append(m.group(1).strip())
names = [t for t in longest if len(longest[t].get("solve", [])) == 2 and len(longest[t].get("dtz", [])) == 2]

def ours(fen):
    out = subprocess.run(["dotnet", CLI, "probe", fen], capture_output=True, text=True, env=ENV).stdout
    mate = re.search(r"(?:White|Black) to move: (.*)", out)
    rule = re.search(r"under the 50-move rule: (.*)", out)
    if not mate or not rule:
        return None
    def parse(text):
        n = re.search(r"\((\d+) plies\)", text) or re.search(r"in (\d+) plies", text)
        if text.startswith("win"):
            return "win", int(n.group(1))
        if text.startswith("loss"):
            return "loss", -(int(n.group(1)) if n else 0)
        return "draw", 0
    return parse(mate.group(1)), parse(rule.group(1))

def lichess(fen):
    url = "https://tablebase.lichess.ovh/standard?fen=" + fen.replace(" ", "_")
    for attempt in range(6):
        try:
            with urllib.request.urlopen(url, timeout=20) as r:
                return json.load(r)
        except Exception:
            time.sleep(3 + 5 * attempt)
    raise RuntimeError("no answer for " + fen)

def random_fen(material):
    white, black = material[1:].split("vK")
    pieces = ["K", "k"] + list(white) + [c.lower() for c in black]
    board = {}
    for letter in pieces:
        squares = [s for s in range(64) if s not in board and (letter.upper() != "P" or 8 <= s < 56)]
        board[random.choice(squares)] = letter
    rows = []
    for rank in range(7, -1, -1):
        row, empty = "", 0
        for file in range(8):
            p = board.get(rank * 8 + file)
            if p:
                row += (str(empty) if empty else "") + p
                empty = 0
            else:
                empty += 1
        rows.append(row + (str(empty) if empty else ""))
    return "/".join(rows) + " " + random.choice("wb") + " - - 0 1"

samples = []
for t in names:
    samples += [(t, "longest mate", f) for f in longest[t]["solve"]]
    samples += [(t, "longest dtz", f) for f in longest[t]["dtz"]]
    got = 0
    while got < 3:
        fen = random_fen(t)
        if ours(fen) is not None:
            samples.append((t, "random", fen))
            got += 1

rows, bad_mate, bad_rule, no_dtm = [], 0, 0, 0
for t, kind, fen in samples:
    mine = ours(fen)
    d = lichess(fen)
    time.sleep(0.4)
    (mate_kind, mate_plies), (rule_kind, rule_plies) = mine
    dtm = d.get("dtm")
    if dtm is None and mate_kind != "draw":
        mate_ok = None   # Lichess gave no mate distance here
        no_dtm += 1
    else:
        mate_ok = (mate_kind == "draw" and not dtm) or (mate_kind != "draw" and dtm == mate_plies)
    theirs = {"win": "win", "loss": "loss", "draw": "draw", "cursed-win": "draw", "blessed-loss": "draw"}.get(d["category"], d["category"])
    exact = d.get("precise_dtz") if d.get("precise_dtz") is not None else d.get("dtz")
    rule_ok = rule_kind == theirs and (theirs == "draw" or rule_plies == exact
                                        or ((rule_kind, rule_plies) == ("loss", 0) and exact == -1)
                                        or (d.get("precise_dtz") is None and abs(rule_plies - d["dtz"]) <= 1))
    bad_mate += mate_ok is False
    bad_rule += not rule_ok
    rows.append((t, kind, fen, mine, d["category"], d.get("dtz"), d.get("precise_dtz"), dtm, mate_ok, rule_ok))
    flag = "ok " if mate_ok is not False and rule_ok else "BAD"
    print(f"{flag} {t:7} {kind:12} {fen:42} ours mate {mate_kind} {mate_plies}, rule {rule_kind} {rule_plies}  "
          f"lichess {d['category']} dtz {d.get('dtz')} precise {d.get('precise_dtz')} dtm {dtm}", flush=True)

print(f"\n{len(rows)} positions in {len(names)} tables: mate distances {len(rows) - bad_mate - no_dtm} agree, "
      f"{bad_mate} disagree, {no_dtm} without a Lichess dtm; 50-move rule {len(rows) - bad_rule} agree, {bad_rule} disagree")
with open(RESULTS, "w") as f:
    f.write("table\tkind\tfen\tours mate\tours rule\tlichess category\tdtz\tprecise dtz\tdtm\tmate agrees\trule agrees\n")
    for t, kind, fen, mine, cat, dtz, precise, dtm, mate_ok, rule_ok in rows:
        f.write(f"{t}\t{kind}\t{fen}\t{mine[0][0]} {mine[0][1]}\t{mine[1][0]} {mine[1][1]}\t{cat}\t{dtz}\t{precise}\t{dtm}\t"
                f"{'-' if mate_ok is None else 'yes' if mate_ok else 'NO'}\t{'yes' if rule_ok else 'NO'}\n")
