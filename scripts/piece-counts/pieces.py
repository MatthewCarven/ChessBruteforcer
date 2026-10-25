"""How many of the bot's games get down to 16 pieces, how long they go on from there, and the pieces left at move 50.

    python -I scripts\\piece-counts\\pieces.py [games folder] [--games]

Matthew's questions, 2026-10-25: "how many games end up with only 16 pieces on the board and the lengths of play
from that point forward", then "the average amount of pieces left from the 50 move mark".

Reads every .pgn in bot\\games (the bot's own games, one a file; default the checkout's bot\\games). A piece is
anything on the board, kings and pawns included: 32 at the start. Every capture takes exactly one off (en passant
too; a promotion swaps one for another), so the count after a ply is 32 less the captures so far, read from the
moves' "x", and a game that ever has 16 or fewer passes through exactly 16 at its 16th capture. "Moves after" counts
from that ply to the game's last, in moves (a move is White's and Black's: two plies). Move 50 is the position after
Black's 50th move (ply 100). Standard games from the usual start only. --games lists every game that got to 16.
"""
import math
import os
import re
import statistics
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ME = "matthewcarven"
SPEEDS = ("bullet", "blitz", "rapid", "classical", "correspondence")
TAG = re.compile(r'^\[(\w+) "(.*)"\]\s*$', re.M)
SAN = re.compile(r"^([NBRQK]?[a-h]?[1-8]?x?[a-h][1-8](=[NBRQ])?|O-O(-O)?)[+#]?[!?]*$")
AFTER_BANDS = ((0, 9), (10, 19), (20, 29), (30, 39), (40, 59), (60, 999))    # moves after 16 pieces
LEFT_BANDS = ((2, 4), (5, 8), (9, 12), (13, 16), (17, 32))                    # pieces left at move 50


def read_text(path):
    data = open(path, "rb").read()
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return data.decode("cp1252")


def moves_of(text):
    """The game's moves in SAN, the comments and our PVs (which run past the game's last move) taken out."""
    tags = list(TAG.finditer(text))
    body = text[tags[-1].end():] if tags else text
    body = re.sub(r"\{[^}]*\}", " ", body)
    while "(" in body:
        body, n = re.subn(r"\([^()]*\)", " ", body)
        if not n:
            break
    body = re.sub(r"\d+\.(\.\.)?", " ", body)
    return [t for t in body.split() if t not in ("1-0", "0-1", "1/2-1/2", "*") and not t.startswith("$")]


def read(path):
    text = read_text(path)
    tags = dict(TAG.findall(text))
    if ME == tags.get("White", "").lower():
        colour = "white"
    elif ME == tags.get("Black", "").lower():
        colour = "black"
    else:
        return None, "not the bot's"
    if tags.get("Variant", "Standard") != "Standard" or "FEN" in tags:
        return None, "not standard from the start"
    score = {"1-0": 1.0, "0-1": 0.0, "1/2-1/2": 0.5}.get(tags.get("Result"))
    if score is None:
        return None, "no result"
    sans = moves_of(text)
    if not sans:
        return None, "no moves"
    bad = [s for s in sans if not SAN.match(s)]
    if bad:
        return None, f"unreadable moves ({bad[0]})"
    if colour == "black":
        score = 1.0 - score
    left = []                        # pieces on the board after each ply
    n = 32
    for s in sans:
        n -= "x" in s
        left.append(n)
    sixteen = left.index(16) + 1 if 16 in left else None      # the ply that left 16
    before = [32] + left[:-1]                                   # pieces on the board as each ply was chosen
    ours = [before[i] for i in range(0 if colour == "white" else 1, len(sans), 2)]
    event = tags.get("Event", "")
    return {
        "id": tags.get("GameId") or tags.get("Site", "").rsplit("/", 1)[-1],
        "date": tags.get("UTCDate", "").replace(".", "-"),
        "opponent": tags.get("Black" if colour == "white" else "White", ""),
        "colour": colour,
        "speed": next((s for s in SPEEDS if s in event), "other"),
        "result": {1.0: "won", 0.5: "drawn", 0.0: "lost"}[score],
        "termination": tags.get("Termination", ""),
        "plies": len(sans),
        "sixteen": sixteen,
        "after": None if sixteen is None else (len(sans) - sixteen) / 2,
        "at50": left[99] if len(sans) >= 100 else None,
        "end": left[-1],
        "ours": len(ours),
        "ours16": sum(n <= 16 for n in ours),
    }, None


def mean_range(xs):
    """'mean [lo, hi]': the mean and its 95% range (normal, from the spread of xs)."""
    if not xs:
        return "-"
    m = statistics.mean(xs)
    if len(xs) < 2:
        return f"{m:.1f}"
    half = 1.96 * statistics.stdev(xs) / math.sqrt(len(xs))
    return f"{m:.1f} [{m - half:.1f}, {m + half:.1f}]"


def span(xs, fmt="{:g}"):
    return f"{fmt.format(min(xs))}-{fmt.format(max(xs))}" if xs else "-"


def wdl(gs):
    return "+{} ={} -{}".format(*(sum(g["result"] == r for g in gs) for r in ("won", "drawn", "lost")))


def sixteen_rows(title, groups, games):
    print(f"{title:<12} {'games':>6} {'got to 16':>10} {'%':>5} {'at move':>8} {'moves after: mean [95%]':>25} "
          f"{'median':>7} {'range':>9}")
    for label, gs in groups + [("total", games)]:
        if label == "total":
            print("-" * 89)
        hit = [g for g in gs if g["sixteen"]]
        after = [g["after"] for g in hit]
        at = [(g["sixteen"] + 1) // 2 for g in hit]
        print(f"{label:<12} {len(gs):>6} {len(hit):>10} {100 * len(hit) / len(gs) if gs else 0:>5.0f} "
              f"{statistics.mean(at) if at else 0:>8.1f} {mean_range(after):>25} "
              f"{statistics.median(after) if after else 0:>7.1f} {span(after):>9}")
    print()


def fifty_rows(title, groups, games):
    print(f"{title:<12} {'games':>6} {'to move 50':>11} {'%':>5} {'pieces at 50: mean [95%]':>26} {'median':>7} "
          f"{'range':>6} {'at the end':>11}")
    for label, gs in groups + [("total", games)]:
        if label == "total":
            print("-" * 91)
        hit = [g for g in gs if g["at50"] is not None]
        at50 = [g["at50"] for g in hit]
        end = [g["end"] for g in hit]
        print(f"{label:<12} {len(gs):>6} {len(hit):>11} {100 * len(hit) / len(gs) if gs else 0:>5.0f} "
              f"{mean_range(at50):>26} {statistics.median(at50) if at50 else 0:>7g} {span(at50):>6} "
              f"{statistics.mean(end) if end else 0:>11.1f}")
    print()


def main(args):
    rest = [a for a in args if a != "--games"]
    folder = rest[0] if rest else os.path.join(HERE, "..", "..", "bot", "games")
    games, skipped = [], {}
    for name in sorted(os.listdir(folder)):
        if not name.endswith(".pgn"):
            continue
        g, why = read(os.path.join(folder, name))
        if g:
            games.append(g)
        else:
            skipped[why] = skipped.get(why, 0) + 1
    if not games:
        sys.exit(f"no games in {folder}")
    games.sort(key=lambda g: (g["date"], g["id"]))
    by_result = [(r, [g for g in games if g["result"] == r]) for r in ("won", "drawn", "lost")]
    by_speed = [(s, [g for g in games if g["speed"] == s]) for s in SPEEDS + ("other",)]
    by_speed = [(s, gs) for s, gs in by_speed if gs]
    by_colour = [(c, [g for g in games if g["colour"] == c]) for c in ("white", "black")]

    print(f"{len(games)} of the bot's games, {games[0]['date']} to {games[-1]['date']}"
          + (" (left out: " + ", ".join(f"{n} {why}" for why, n in sorted(skipped.items())) + ")" if skipped else ""))
    print("A piece is anything on the board, kings and pawns included: 32 at the start.\n")

    hit = [g for g in games if g["sixteen"]]
    after = [g["after"] for g in hit]
    print(f"## Down to 16 pieces: {len(hit)} of {len(games)} games ({100 * len(hit) / len(games):.0f}%); "
          f"from there they went on {statistics.mean(after):.1f} moves on average (median {statistics.median(after):g})\n")
    print("'at move' is the move number the 16th piece came off (mean); 'moves after' runs from there to the game's end.\n")
    sixteen_rows("result", by_result, games)
    sixteen_rows("speed", by_speed, games)
    sixteen_rows("colour", by_colour, games)

    print("How long the games that got to 16 went on from there:\n")
    print(f"{'moves after':<12} {'games':>6} {'%':>5} {'won/drawn/lost':>16}")
    for lo, hi in AFTER_BANDS:
        gs = [g for g in hit if lo <= g["after"] < hi + 1]
        label = f"{lo}-{hi}" if hi < 999 else f"{lo}+"
        print(f"{label:<12} {len(gs):>6} {100 * len(gs) / len(hit):>5.0f} {wdl(gs):>16}")
    print("-" * 42)
    print(f"{'total':<12} {len(hit):>6} {100:>5} {wdl(hit):>16}")
    print(f"{'mean':<12} {mean_range(after)} moves after; median {statistics.median(after):g}\n")

    fifty = [g for g in games if g["at50"] is not None]
    at50 = [g["at50"] for g in fifty]
    print(f"## At move 50: {len(fifty)} of {len(games)} games got there ({100 * len(fifty) / len(games):.0f}%), "
          f"with {statistics.mean(at50):.1f} pieces left on average (median {statistics.median(at50):g})\n")
    print("'at the end' is the same games' pieces when they finished (mean).\n")
    fifty_rows("result", by_result, games)
    fifty_rows("speed", by_speed, games)
    fifty_rows("colour", by_colour, games)

    print("Pieces left at move 50:\n")
    print(f"{'pieces':<12} {'games':>6} {'%':>5} {'won/drawn/lost':>16}")
    for lo, hi in LEFT_BANDS:
        gs = [g for g in fifty if lo <= g["at50"] <= hi]
        print(f"{lo}-{hi:<10} {len(gs):>6} {100 * len(gs) / len(fifty):>5.0f} {wdl(gs):>16}")
    print("-" * 42)
    print(f"{'total':<12} {len(fifty):>6} {100:>5} {wdl(fifty):>16}")
    print(f"{'mean':<12} {mean_range(at50)} pieces; median {statistics.median(at50):g}\n")

    ours, ours16 = sum(g["ours"] for g in games), sum(g["ours16"] for g in games)
    print(f"## Our moves chosen with 16 or fewer pieces on the board: {ours16:,} of {ours:,} ({100 * ours16 / ours:.0f}%)\n")
    print("The share of the bot's decisions a net for 16 pieces or fewer would make (its searches look further on,")
    print("where there are fewer still). 'a game' is the mean over the games that got to 16.\n")
    for title, groups in (("result", by_result), ("speed", by_speed), ("colour", by_colour)):
        print(f"{title:<12} {'games':>6} {'our moves':>10} {'at 16 or fewer':>15} {'%':>5} {'a game':>7}")
        for label, gs in groups + [("total", games)]:
            if label == "total":
                print("-" * 60)
            n, n16 = sum(g["ours"] for g in gs), sum(g["ours16"] for g in gs)
            per = [g["ours16"] for g in gs if g["sixteen"]]
            print(f"{label:<12} {len(gs):>6} {n:>10,} {n16:>15,} {100 * n16 / n if n else 0:>5.0f} "
                  f"{statistics.mean(per) if per else 0:>7.1f}")
        print()

    if "--games" in args:
        print("## Every game that got to 16 pieces\n")
        print(f"{'date':<10} {'game':<8} {'opponent':<22} {'colour':<6} {'speed':<9} {'result':<6} {'at move':>7} "
              f"{'after':>6} {'at 50':>5} {'end':>4}  termination")
        for g in hit:
            print(f"{g['date']:<10} {g['id']:<8} {g['opponent'][:22]:<22} {g['colour']:<6} {g['speed']:<9} "
                  f"{g['result']:<6} {(g['sixteen'] + 1) // 2:>7} {g['after']:>6g} "
                  f"{'-' if g['at50'] is None else g['at50']:>5} {g['end']:>4}  {g['termination']}")
        print("-" * 104)
        print(f"total {len(hit)} games; moves after, mean {statistics.mean(after):.1f}, median "
              f"{statistics.median(after):g}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
