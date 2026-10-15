"""Judge games' first 6-piece positions with Lichess's tablebase (Syzygy, up to 7 pieces), as `game takeover`
judges 5-piece ones with ours: what perfect play from there gives the side to move, under the 50-move rule at
the game's own count, against how the game actually ended.

    python -I scripts/six-piece-check.py <positions .tsv> <results .tsv>

The positions come from `game reach ... --sample N <tsv>` (FEN, the result for the side to move, how it ended,
plies left, rating band, speed, link). Each is asked of https://tablebase.lichess.ovh/standard, gently (one at
a time, ~0.4 s apart, retrying a busy server). A win counts only if the DTZ fits what the 50-move count has
left (cursed wins and blessed losses are draws), as our TryProbeWithClock does. Results are written as they
come (a rerun skips positions already judged), and the summary covers the whole results file.
"""
import csv, json, os, sys, time, urllib.parse, urllib.request
from collections import Counter, defaultdict

src, dst = sys.argv[1], sys.argv[2]
rows = list(csv.DictReader(open(src, encoding="utf-8"), delimiter="\t"))
done = {}
if os.path.exists(dst):
    for r in csv.DictReader(open(dst, encoding="utf-8"), delimiter="\t"):
        done[r["fen"]] = r
fields = list(rows[0].keys()) + ["category", "dtz", "verdict"]


def ask(fen):
    url = "https://tablebase.lichess.ovh/standard?fen=" + urllib.parse.quote(fen)
    for attempt in range(6):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "ChessBruteforcer research (Matthew Carven)"})
            with urllib.request.urlopen(request, timeout=20) as response:
                return json.load(response)
        except Exception as e:
            time.sleep(2 + 5 * attempt)
    raise SystemExit(f"tablebase not answering for {fen}")


def verdict(answer, fen):
    """win / draw / loss for the side to move by the 50-move rule at the FEN's count (or 'unknown')."""
    category = answer.get("category")
    dtz = answer.get("precise_dtz") or answer.get("dtz")
    clock = int(fen.split()[4])
    if category in ("draw", "cursed-win", "blessed-loss"):
        return "draw"
    if category in ("win", "maybe-win", "loss", "maybe-loss") and dtz is not None:
        if clock + abs(dtz) > 100:
            return "draw"   # the count runs out first
        return "win" if category.endswith("win") else "loss"
    return "unknown"


new = [r for r in rows if r["fen"] not in done]
with open(dst, "a", encoding="utf-8", newline="") as out:
    writer = csv.DictWriter(out, fieldnames=fields, delimiter="\t")
    if not done:
        writer.writeheader()
    for i, r in enumerate(new):
        answer = ask(r["fen"])
        r = dict(r, category=answer.get("category"), dtz=answer.get("precise_dtz") or answer.get("dtz"),
                 verdict=verdict(answer, r["fen"]))
        writer.writerow(r)
        out.flush()
        done[r["fen"]] = r
        if (i + 1) % 50 == 0:
            print(f"{i + 1} of {len(new)} asked", file=sys.stderr, flush=True)
        time.sleep(0.4)

judged = [r for r in done.values() if r["verdict"] != "unknown"]
print(f"{len(done)} positions, {len(judged)} judged ({len(done) - len(judged)} unknown)")
print("The table's verdict (50-move rule, the game's count) for the side to move, against the game's result:")
for v in ("win", "draw", "loss"):
    group = [r for r in judged if r["verdict"] == v]
    c = Counter(r["result for the side to move"] for r in group)
    n = len(group) or 1
    print(f"  {v:5s} {len(group):5d}   won {100 * c['win'] / n:5.1f}%  drawn {100 * c['draw'] / n:5.1f}%  lost {100 * c['loss'] / n:5.1f}%")
otherwise = [r for r in judged if r["verdict"] != r["result for the side to move"]]
print(f"  ended otherwise than the table says: {len(otherwise)} of {len(judged)} ({100 * len(otherwise) / max(1, len(judged)):.1f}%)")
on_time = sum(r["termination"] == "Time forfeit" for r in otherwise)
print(f"  of those, on time: {on_time} ({100 * on_time / max(1, len(otherwise)):.0f}%)")
by = defaultdict(list)
for r in judged:
    by[r["speed"]].append(r)
print("  by speed: " + ", ".join(f"{s} {100 * sum(x['verdict'] != x['result for the side to move'] for x in g) / len(g):.0f}% of {len(g)}"
                                 for s, g in sorted(by.items(), key=lambda kv: -len(kv[1]))))
