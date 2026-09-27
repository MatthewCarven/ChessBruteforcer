"""Compare our DTZ (50-move rule) with Syzygy's, via the Lichess tablebase API.

    python scripts/syzygy-check.py <cli bin dir> <tables dir> <results .tsv>

Per table: the longest DTZ position for each side to move (from `dtz`), three
random legal positions, and a few en passant positions in KPvKP.  Needs the
DTM and DTZ tables in <tables dir> (scripts/dtz-tables.sh makes them).
Lichess reports a checkmated position as dtz -1; we store it as 0 ("checkmated
now"), so that one convention is allowed for.
"""
import json, os, random, re, subprocess, sys, time, urllib.request

BIN, TABLES, RESULTS = sys.argv[1], sys.argv[2], sys.argv[3]
CLI = os.path.join(BIN, "ChessBruteforcer.Cli.dll")
ENV = dict(os.environ, CHESS_TABLES=TABLES)
ALL = "KvK KQvK KRvK KBvK KNvK KPvK KQvKQ KQvKR KQvKB KQvKN KQvKP KRvKR KRvKB KRvKN KRvKP KBvKB KBvKN KBvKP KNvKN KNvKP KPvKP KQQvK KQRvK KQBvK KQNvK KQPvK KRRvK KRBvK KRNvK KRPvK KBBvK KBNvK KBPvK KNNvK KNPvK KPPvK".split()
random.seed(20260928)

def cli(*args):
    return subprocess.run(["dotnet", CLI, *args], capture_output=True, text=True, env=ENV).stdout

def ours(fen):
    out = cli("probe", fen)
    m = re.search(r"under the 50-move rule: (.*)", out)
    if not m:
        return None
    text = m.group(1)
    n = re.search(r"in (\d+) plies", text)
    if text.startswith("win"):
        return "win", int(n.group(1))
    if text.startswith("loss"):
        return "loss", -(int(n.group(1)) if n else 0)
    return "draw", 0

def syzygy(fen):
    url = "https://tablebase.lichess.ovh/standard?fen=" + fen.replace(" ", "_")
    for attempt in range(5):
        try:
            with urllib.request.urlopen(url, timeout=20) as r:
                d = json.load(r)
            return d["category"], d.get("dtz"), d.get("precise_dtz")
        except Exception as e:
            time.sleep(2 + 3 * attempt)
    raise RuntimeError("no answer for " + fen)

def random_fen(material):
    white, black = material[1:].split("vK")
    pieces = [("K", True), ("k", True)] + [(c, True) for c in white] + [(c.lower(), True) for c in black]
    while True:
        board = {}
        ok = True
        for letter, _ in pieces:
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
for m in ALL:
    out = cli("dtz", m)
    samples += [(m, "longest", f) for f in re.findall(r"e\.g\. (\S+ [wb] \S+ \S+ \d+ \d+)", out)]
    got = 0
    while got < 3:
        fen = random_fen(m)
        if ours(fen) is not None:
            samples.append((m, "random", fen))
            got += 1
# En passant: white has just played a double push next to a black pawn.
samples += [("KPvKP", "en passant", f) for f in [
    "8/8/8/8/3pP3/8/8/K6k b - e3 0 1",
    "8/8/8/8/4Pp2/8/8/k6K b - e3 0 1",
    "4k3/8/8/8/2Pp4/8/8/4K3 b - c3 0 1",
    "8/8/8/5k2/3pP3/8/5K2/8 b - e3 0 1",
]]

rows, bad = [], 0
for m, kind, fen in samples:
    mine = ours(fen)
    cat, dtz, precise = syzygy(fen)
    time.sleep(0.4)
    theirs_kind = {"win": "win", "loss": "loss", "draw": "draw", "cursed-win": "draw", "blessed-loss": "draw"}.get(cat, cat)
    exact = precise if precise is not None else dtz
    same_kind = mine is not None and mine[0] == theirs_kind
    # Syzygy may store DTZ rounded (then precise_dtz is null): allow one ply either way there.
    mated = mine == ("loss", 0) and exact == -1   # the checkmate convention (see the top)
    same_dtz = mine is not None and (theirs_kind == "draw" or mine[1] == exact or mated
                                     or (precise is None and abs(mine[1] - dtz) <= 1))
    ok = same_kind and same_dtz
    bad += not ok
    rows.append((m, kind, fen, mine, cat, dtz, precise, ok))
    print(f"{'ok ' if ok else 'BAD'} {m:6} {kind:10} {fen:40} ours {mine}  syzygy {cat} dtz {dtz} precise {precise}", flush=True)
print(f"\n{len(rows)} positions, {len(rows) - bad} agree, {bad} disagree")
with open(RESULTS, "w") as f:
    f.write("table\tkind\tfen\tours\tsyzygy category\tsyzygy dtz\tprecise dtz\tagree\n")
    for m, kind, fen, mine, cat, dtz, precise, ok in rows:
        ours_text = "-" if mine is None else f"{mine[0]} {mine[1]}"
        f.write(f"{m}\t{kind}\t{fen}\t{ours_text}\t{cat}\t{dtz}\t{precise}\t{'yes' if ok else 'NO'}\n")
