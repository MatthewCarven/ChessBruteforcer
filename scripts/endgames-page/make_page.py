"""Build the 'Endgames thrown away' page from a `game takeover --moves` output (2026-09 sample with clocks)."""
import html, re, sys

src, dst = sys.argv[1], sys.argv[2]
text = open(src, encoding="utf-8").read()

# ---- examples -------------------------------------------------------------
examples = {}
for line in text.splitlines():
    if line.startswith("EXAMPLE\t"):
        f = line.split("\t")
        x = dict(zip(["table", "kind", "change", "rating", "speed", "left", "fen", "played", "best", "before", "after",
                      "link", "puci", "buci"], f[1:]))
        examples[(x["table"], x["kind"])] = x

# The cards, in order, with a note only where the reason is mechanical and checked by hand on the position.
CHOSEN = [
    ("KQPvKQ", "piece", "Mate in one was on the board: Qc7, guarded by the king. Qe6+ let it slip, and the tables call the rest a draw."),
    ("KQvK", "stalemate", "The queen already covered every square the white king could step to. A move that doesn't give it one back, "
                          "and doesn't give check, leaves White with no legal move: stalemate, a draw."),
    ("KPPvK", "stalemate", "The same trap with two pawns: Kc2 took b1, the last square the cornered white king had (the pawn "
                           "covered a2, the king b2). No legal move, no check: stalemate."),
    ("KQvK", "hung", "Qe4+ put the queen next to the black king with nothing guarding it. Kxe4, and a lone king can't lose."),
    ("KQvK", "missed", "The black king was in check from a queen it could simply take. Kb5 walked away from it instead."),
    ("KQPvKQ", "hung", "White had mate in 4. Qxh3+ traded the queens off, and king against king and pawn here is only a draw."),
    ("KRvKR", "hung", "Rook against rook is a draw until one rook can be taken. Ra7 put White's on the black rook's rank."),
    ("KPvK", "pawn", None),
    ("KPvK", "king", None),
    ("KPvKP", "king", "One game, two moves running, a 2854 against a 2831 in bullet: White's Ke3 lost the draw..."),
    ("KPvKP", "pawn", "...and Black's a5, the very next move, gave it straight back."),
    ("KRPvKR", "piece", None),
    ("KBPvKP", "hung", None),
]

KIND_LABEL = {
    "stalemate": "Stalemate", "too slow": "Too slow for the 50-move rule", "hung": "Left it to be taken",
    "missed": "Missed a capture", "pawn": "Wrong pawn move", "king": "Wrong king step", "piece": "Wrong piece move",
}
NAMES = {"K": "king", "Q": "queen", "R": "rook", "B": "bishop", "N": "knight", "P": "pawn"}
NUMBER = {2: "two", 3: "three"}
SPEED = {"bullet": "bullet", "blitz": "blitz", "rapid": "rapid", "classical": "classical", "ultrabullet": "ultrabullet"}


def side_words(letters):
    parts = []
    for letter in "KQRBNP":
        n = letters.count(letter)
        if n == 1:
            parts.append(NAMES[letter])
        elif n > 1:
            parts.append(f"{NUMBER.get(n, n)} {NAMES[letter]}s")
    return parts[0] if len(parts) == 1 else ", ".join(parts[:-1]) + " and " + parts[-1]


def material_words(table):
    strong, weak = table.split("v")
    words = f"{side_words(strong)} against {side_words(weak)}"
    return words[0].upper() + words[1:]


GLYPH = {"k": "♚", "q": "♛", "r": "♜", "b": "♝", "n": "♞", "p": "♟"}


def board_svg(fen, played, best, label):
    rows = fen.split()[0].split("/")
    white_to_move = fen.split()[1] == "w"
    size = 40
    out = [f'<svg class="board" viewBox="0 0 {8 * size} {8 * size}" role="img" aria-label="{html.escape(label)}">']

    def xy(file, rank):   # board file 0-7 (a-h), rank 0-7 (1-8) to the top-left of its square, mover at the bottom
        col, row = (file, 7 - rank) if white_to_move else (7 - file, rank)
        return col * size, row * size

    for rank in range(8):
        for file in range(8):
            x, y = xy(file, rank)
            shade = "sq-dark" if (file + rank) % 2 == 0 else "sq-light"
            out.append(f'<rect x="{x}" y="{y}" width="{size}" height="{size}" class="{shade}"/>')
    # coordinates along the bottom and left edges, as on a printed diagram
    for file in range(8):
        x, y = xy(file, 0 if white_to_move else 7)
        out.append(f'<text x="{x + size - 3}" y="{y + size - 3}" class="coord" text-anchor="end">{"abcdefgh"[file]}</text>')
    for rank in range(8):
        x, y = xy(0 if white_to_move else 7, rank)
        out.append(f'<text x="{x + 3}" y="{y + 11}" class="coord">{rank + 1}</text>')
    for r, row in enumerate(rows):
        rank, file = 7 - r, 0
        for ch in row:
            if ch.isdigit():
                file += int(ch)
                continue
            x, y = xy(file, rank)
            colour = "white" if ch.isupper() else "black"
            out.append(f'<text x="{x + size / 2}" y="{y + size * 0.80}" class="piece {colour}" text-anchor="middle">'
                       f'{GLYPH[ch.lower()]}︎</text>')
            file += 1

    def arrow(uci, cls):
        if len(uci) < 4:
            return ""
        f1, r1, f2, r2 = "abcdefgh".index(uci[0]), int(uci[1]) - 1, "abcdefgh".index(uci[2]), int(uci[3]) - 1
        x1, y1 = xy(f1, r1)
        x2, y2 = xy(f2, r2)
        x1, y1, x2, y2 = x1 + size / 2, y1 + size / 2, x2 + size / 2, y2 + size / 2
        dx, dy = x2 - x1, y2 - y1
        length = (dx * dx + dy * dy) ** 0.5
        ux, uy = dx / length, dy / length
        hx, hy = x2 - ux * 13, y2 - uy * 13   # the head's base
        px, py = -uy * 8, ux * 8
        return (f'<g class="{cls}"><line x1="{x1:.1f}" y1="{y1:.1f}" x2="{hx:.1f}" y2="{hy:.1f}"/>'
                f'<polygon points="{x2:.1f},{y2:.1f} {hx + px:.1f},{hy + py:.1f} {hx - px:.1f},{hy - py:.1f}"/></g>')

    out.append(arrow(best, "arrow best"))
    out.append(arrow(played, "arrow played"))
    out.append("</svg>")
    return "".join(out)


def sentence_case(s):
    return s[0].upper() + s[1:] if s else s


cards = []
for table, kind, note in CHOSEN:
    x = examples.get((table, kind))
    if not x:
        print("missing example", table, kind, file=sys.stderr)
        continue
    mover = "White" if x["fen"].split()[1] == "w" else "Black"
    other = "Black" if mover == "White" else "White"
    left = int(x["left"])
    clock = f"{left} s left" if left < 120 else f"{left // 60} min {left % 60:02d} s left"
    label = f'{material_words(table)}. {mover} to move. Played {x["played"]}; the tables play {x["best"]}.'
    facts = (f'<p class="verdicts"><span class="was">{mover} {html.escape(x["before"])}.</span> '
             f'<span class="mv played">{html.escape(x["played"])}</span> and {other} now {html.escape(x["after"])}. '
             f'The tables play <span class="mv best">{html.escape(x["best"])}</span>.</p>')
    cards.append(f'''<article class="card">
  <header><p class="kind">{KIND_LABEL[kind]}</p><h3>{material_words(table)}</h3></header>
  {board_svg(x["fen"], x["puci"], x["buci"], label)}
  {facts}
  {f'<p class="note">{html.escape(note)}</p>' if note else ''}
  <p class="meta"><span>{mover} {x["rating"]}</span><span>{SPEED.get(x["speed"], x["speed"])}</span><span>{clock}</span>
  <a href="{html.escape(x["link"])}" target="_blank" rel="noopener">The game on Lichess</a></p>
</article>''')

# ---- the clock tables and the kinds -----------------------------------------
def table_rows(title_prefix):
    block = text.split(title_prefix, 1)[1].split("\n\n", 1)[0].splitlines()[2:]
    rows = []
    for line in block:
        m = re.match(r"\s+(.+?)\s{2,}([\d,]+)\s+([\d,]+)\s+([\d,]+)\s+([\d.]+)\s", line)
        if m:
            rows.append((m[1], int(m[2].replace(",", "")), int(m[3].replace(",", "")), int(m[4].replace(",", "")), float(m[5])))
    return rows


left_rows = table_rows("By the seconds left on the mover's clock")
taken_rows = table_rows("By the time taken over the move")
kinds = []
for line in text.split("What the mistakes were", 1)[1].split("\n\n", 1)[0].splitlines()[2:]:
    m = re.match(r"\s+(\S+(?: \S+)?)\s+([\d,]+) \((\d+)%\)", line)
    if m:
        kinds.append((m[1], int(m[2].replace(",", "")), int(m[3])))
all_line = re.search(r"all\s+([\d,]+)\s+[\d.]+% of the moves that could go wrong \(1 in (\d+)\)", text)
moves_line = re.search(r"Moves played: ([\d,]+) in ([\d,]+) games", text)


def column_chart(rows, title, note):
    w, h, left_pad, bottom, top = 520, 240, 34, 44, 16
    slot = (w - left_pad - 8) / len(rows)
    top_value = 6
    scale = (h - bottom - top) / top_value
    parts = [f'<svg class="chart" viewBox="0 0 {w} {h}" role="img" aria-label="{html.escape(title)}">']
    for v in range(0, top_value + 1):
        y = h - bottom - v * scale
        parts.append(f'<line x1="{left_pad}" x2="{w - 4}" y1="{y:.1f}" y2="{y:.1f}" class="grid{" base" if v == 0 else ""}"/>')
        parts.append(f'<text x="{left_pad - 6}" y="{y + 4:.1f}" class="tick" text-anchor="end">{v}</text>')
    for i, (label, moves, could, mistakes, rate) in enumerate(rows):
        cx = left_pad + slot * (i + 0.5)
        bh = rate * scale
        y = h - bottom - bh
        bw = 24
        # a column with a 4px rounded top, square at the baseline
        r = 4
        path = (f"M{cx - bw / 2:.1f},{h - bottom:.1f} V{y + r:.1f} Q{cx - bw / 2:.1f},{y:.1f} {cx - bw / 2 + r:.1f},{y:.1f} "
                f"H{cx + bw / 2 - r:.1f} Q{cx + bw / 2:.1f},{y:.1f} {cx + bw / 2:.1f},{y + r:.1f} V{h - bottom:.1f} Z")
        tip = f"{label}: {rate:.2f} mistakes per 100 ({mistakes:,} of {could:,} moves that could go wrong)"
        parts.append(f'<g class="col" tabindex="0" data-tip="{html.escape(tip)}">'
                     f'<rect x="{cx - slot / 2:.1f}" y="{top}" width="{slot:.1f}" height="{h - bottom - top}" class="hit"/>'
                     f'<path d="{path}" class="bar"/>'
                     f'<text x="{cx:.1f}" y="{y - 6:.1f}" class="value" text-anchor="middle">{rate:.1f}</text>'
                     f'<text x="{cx:.1f}" y="{h - bottom + 16:.1f}" class="cat" text-anchor="middle">{html.escape(label)}</text></g>')
    parts.append(f'<text x="{left_pad}" y="{h - 6}" class="axis-note">{html.escape(note)}</text>')
    parts.append("</svg>")
    table = "".join(f"<tr><td>{html.escape(l)}</td><td>{c:,}</td><td>{m:,}</td><td>{r:.2f}</td></tr>" for l, _, c, m, r in rows)
    return ("".join(parts) +
            f'<details class="numbers"><summary>The numbers</summary><div class="scroll"><table><thead><tr><th></th>'
            f'<th>moves that could go wrong</th><th>mistakes</th><th>per 100</th></tr></thead><tbody>{table}</tbody></table></div></details>')


KIND_PLAIN = {
    "king": "Wrong king step", "hung": "Left something to be taken", "piece": "Wrong move of another piece",
    "pawn": "Wrong pawn move", "stalemate": "Stalemated the other side", "missed": "Missed a capture",
    "too slow": "Too slow for the 50-move rule",
}
kind_bars = []
top_share = max(s for _, _, s in kinds)
for name, n, share in sorted(kinds, key=lambda k: -k[1]):
    kind_bars.append(f'<li><span class="k-name">{KIND_PLAIN.get(name, name)}</span>'
                     f'<span class="k-track"><span class="k-bar" style="width:{100 * share / top_share:.1f}%"></span></span>'
                     f'<span class="k-val">{share}%</span><span class="k-n">{n:,}</span></li>')

page = open(sys.argv[3], encoding="utf-8").read()
page = (page.replace("{{CARDS}}", "\n".join(cards))
        .replace("{{LEFT_CHART}}", column_chart(left_rows, "Mistakes per 100 moves by seconds left on the clock", "seconds left on the mover's clock"))
        .replace("{{TAKEN_CHART}}", column_chart(taken_rows, "Mistakes per 100 moves by time taken over the move", "time taken over the move"))
        .replace("{{KINDS}}", "\n".join(kind_bars))
        .replace("{{ONE_IN}}", all_line[2])
        .replace("{{MISTAKES}}", all_line[1])
        .replace("{{MOVES}}", moves_line[1])
        .replace("{{GAMES}}", moves_line[2]))
open(dst, "w", encoding="utf-8", newline="\n").write(page)
print(len(cards), "cards,", len(left_rows), "+", len(taken_rows), "bars,", len(kinds), "kinds")
