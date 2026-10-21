"""The table for scripts/depth-match.py's pairs: one row per log in games\\depth (the main checkout's), smoke runs
left out unless --smoke.

    python scripts/depth-summary.py [--smoke]

Per pair: games, wins / losses / draws for the stronger side, its score and Elo with the match tool's 95% interval,
the same Elo from colour-reversed game pairs (pentanomial: each opening's two games counted together, which is the
fairer interval when an opening favours one colour), the mean depth each side reached and its speed, and the minutes
the pair took.
"""
import datetime, math, os, re, subprocess, sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
common = subprocess.run(["git", "rev-parse", "--git-common-dir"], cwd=REPO, capture_output=True, text=True,
                        check=True).stdout.strip()
OUT = os.path.join(os.path.abspath(os.path.join(REPO, common, "..")), "games", "depth")


def elo(score):
    score = min(max(score, 1e-6), 1 - 1e-6)
    return -400 * math.log10(1 / score - 1)


def pentanomial(pair_scores):
    """Elo and 95% interval from per-pair scores (0, 0.5, 1, 1.5, 2 over two games)."""
    n = len(pair_scores)
    mean = sum(pair_scores) / n / 2
    var = sum((s / 2 - mean) ** 2 for s in pair_scores) / n
    half = 1.96 * math.sqrt(var / n)
    return elo(mean), elo(mean - half), elo(mean + half)


def read(path):
    text = open(path, encoding="utf-8", errors="replace").read()
    first = re.search(r"^(\S+ \S+): (\S+) \((\S+)\) v (\S+) \((\S+)\)", text, re.M)
    final = re.search(r"^final: .*?: \+(\d+) -(\d+) =(\d+)\s+score (\S+)%\s+Elo (\S+) \[(\S+), (\S+)\]", text, re.M)
    if not first or not final:
        return None
    name_a = first.group(2)
    searches = re.findall(r"^searches: (\S+) depth (\S+), (\S+) knps \((\d+) moves\); (\S+) depth (\S+), (\S+) knps",
                          text, re.M)
    finished = re.search(r"^(\S+ \S+): finished", text, re.M)
    minutes = None
    if finished:
        start = datetime.datetime.strptime(first.group(1), "%Y-%m-%d %H:%M:%S")
        end = datetime.datetime.strptime(finished.group(1), "%Y-%m-%d %H:%M:%S")
        minutes = (end - start).total_seconds() / 60
    # Each game's score for the stronger side, by game number; games 2k-1 and 2k share an opening.
    scores = {}
    for number, white, result in re.findall(r"^game\s+(\d+): (\S+) vs \S+\s+(\S+)", text, re.M):
        a_white = white == name_a
        scores[int(number)] = {"1-0": 1 if a_white else 0, "0-1": 0 if a_white else 1}.get(result, 0.5)
    pairs = [scores[g] + scores[g + 1] for g in range(1, max(scores, default=0), 2) if g in scores and g + 1 in scores]
    w, l, d = (int(final.group(i)) for i in (1, 2, 3))
    row = {"pair": f"{name_a} v {first.group(4)}", "games": w + l + d, "wld": f"+{w} -{l} ={d}",
           "score": final.group(4), "elo": f"{final.group(5)} [{final.group(6)}, {final.group(7)}]",
           "minutes": f"{minutes:.0f}" if minutes else "running"}
    if len(pairs) >= 10:
        e, lo, hi = pentanomial(pairs)
        row["pairs"] = f"{e:+.0f} [{lo:+.0f}, {hi:+.0f}]"
    if searches:
        s = searches[-1]
        row["depths"] = f"{s[1]} v {s[5]}"
        row["knps"] = f"{s[2]} v {s[6]}"
    return row


def main():
    smoke = "--smoke" in sys.argv
    rows = []
    for name in sorted(os.listdir(OUT), key=lambda n: os.path.getmtime(os.path.join(OUT, n))):
        if name.endswith(".log") and (smoke or "smoke" not in name):
            row = read(os.path.join(OUT, name))
            if row:
                rows.append(row)
    columns = ["pair", "games", "wld", "score", "elo", "pairs", "depths", "knps", "minutes"]
    heads = ["pair", "games", "+W -L =D", "score %", "Elo [95%]", "Elo by opening pairs [95%]", "mean depth",
             "knps", "minutes"]
    print("| " + " | ".join(heads) + " |")
    print("| " + " | ".join("---" for _ in heads) + " |")
    for row in rows:
        print("| " + " | ".join(str(row.get(c, "")) for c in columns) + " |")


if __name__ == "__main__":
    main()
