"""How often the bot's forecast comes true: in each saved game (bot/games/*.pgn), after each of our moves the
PGN holds the engine's principal variation as a side line starting with that same move. Its second move is the
reply it expected; compare with the reply actually played, against a blind guess (1 / legal moves), by opponent
(bot or human) and by stage of the game; and how many plies in a row of the forecast came true.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts\\forecast-hits.py bot\\games

(The bot's venv has python-chess.) First run 2026-10-15: 241 games, 11,795 forecasts, the reply right 51.0%
against 6.6% for a blind guess; every game against another bot.
"""
import glob, os, re, sys
from collections import defaultdict
import chess

TOKEN = re.compile(r"\{[^}]*\}|\(|\)|\$\d+|\d+\.(?:\.\.)?|1-0|0-1|1/2-1/2|\*|[^\s(){}]+")
EVAL = re.compile(r"\[%eval ([^,\]]+),(\d+)\]")
OURS = "matthewcarven"


def read(path):
    data = open(path, "rb").read().replace(b"\r\n", b"\n")   # lichess-bot writes CRLF on Windows
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return data.decode("cp1252")


def parse(text):
    """Tags, and the main line as a list of (san, [variations]) where each variation is (its sans, its depth)."""
    head, _, body = text.partition("\n\n")
    tags = dict(re.findall(r'^\[(\w+) "([^"]*)"\]', head, re.M))
    main, stack = [], []          # stack of (list being built, depth comment) for open variations
    current = main
    for tok in TOKEN.findall(body):
        if tok == "(":
            var = {"sans": [], "depth": None}
            stack.append((current, var))
            current = var["sans"]
            continue
        if tok == ")":
            parent, var = stack.pop()
            if parent is main and main:
                main[-1][1].append(var)
            current = parent
            continue
        if tok.startswith("{"):
            m = EVAL.search(tok)
            if m and stack:
                stack[-1][1]["depth"] = int(m.group(2))
            continue
        if re.fullmatch(r"\d+\.(\.\.)?|\$\d+|1-0|0-1|1/2-1/2|\*", tok):
            continue
        if current is main:
            main.append((tok, []))
        else:
            current.append(tok)
    return tags, main


def phase(ply):
    move = ply // 2 + 1
    return "moves 1-10" if move <= 10 else "moves 11-20" if move <= 20 else "moves 21-40" if move <= 40 else "moves 41+"


def main():
    folder = sys.argv[1]
    stats = defaultdict(lambda: [0, 0, 0.0, 0])      # key -> forecasts, reply hits, sum of 1/legal, forced replies
    run_lengths = defaultdict(int)                   # plies of the forecast that came true after our move
    first_ok = first_all = games = 0
    depths = []
    for path in sorted(glob.glob(os.path.join(folder, "*.pgn"))):
        tags, line = parse(read(path))
        if OURS not in (tags.get("White"), tags.get("Black")) or tags.get("Variant", "Standard") != "Standard":
            continue
        we_white = tags.get("White") == OURS
        opp_title = tags.get("BlackTitle") if we_white else tags.get("WhiteTitle")
        opp = "bot" if opp_title == "BOT" else "human"
        board = chess.Board(tags["FEN"]) if "FEN" in tags else chess.Board()
        games += 1
        for i, (san, variations) in enumerate(line):
            ours = board.turn == (chess.WHITE if we_white else chess.BLACK)
            pv = next((v for v in variations if v["sans"] and v["sans"][0] == san), None)
            if ours and variations:
                first_all += 1
                first_ok += pv is not None
            try:
                board.push_san(san)
            except ValueError:
                print(f"{os.path.basename(path)}: can't play {san} at ply {i}", file=sys.stderr)
                break
            if not ours or pv is None or len(pv["sans"]) < 2 or i + 1 >= len(line):
                continue
            legal = board.legal_moves.count()
            hit = pv["sans"][1] == line[i + 1][0]
            if pv["depth"]:
                depths.append(pv["depth"])
            for key in ("all", opp, phase(i + 1)):
                s = stats[key]
                s[0] += 1
                s[1] += hit
                s[2] += 1 / legal
                s[3] += legal == 1
            k = 0                                    # consecutive plies after our move that came true
            while 1 + k < len(pv["sans"]) and i + 1 + k < len(line) and pv["sans"][1 + k] == line[i + 1 + k][0]:
                k += 1
            run_lengths[min(k, 6)] += 1
    print(f"{games} games; {first_ok:,} of {first_all:,} of our searched moves carry their forecast "
          f"(the forecast's first move is the move played)")
    if depths:
        depths.sort()
        print(f"depth of the forecasts: median {depths[len(depths) // 2]}, 10% / 90% {depths[len(depths) // 10]} / "
              f"{depths[9 * len(depths) // 10]}")
    print(f"\n{'':14} {'forecasts':>10} {'reply right':>12} {'blind guess':>12} {'only one move':>14}")
    for key in ("all", "bot", "human", "moves 1-10", "moves 11-20", "moves 21-40", "moves 41+"):
        if key in stats:
            n, h, chance, forced = stats[key]
            print(f"{key:14} {n:>10,} {100 * h / n:>11.1f}% {100 * chance / n:>11.1f}% {100 * forced / n:>13.1f}%")
    total = sum(run_lengths.values())
    print("\nhow far the forecast came true after our move (plies in a row, theirs first):")
    at_least = total
    for k in range(1, 7):
        at_least -= run_lengths[k - 1]
        print(f"  at least {k} pl{'y' if k == 1 else 'ies'}: {100 * at_least / total:5.1f}%")


main()
