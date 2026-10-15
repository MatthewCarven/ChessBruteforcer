"""A test for index-month.py's frame joins: a small month in Lichess's layout (pzstd frames, each after a
skippable note), made from real games and cut on purpose through game starts. Every cut point in and around
an [Event line, cuts mid-movetext, a frame with no game start, and a frame whose only game start is an
[Event line cut at its end. Then `rows` and `check` on it, and every game fetched by number, its plies and
its fingerprint compared with a straight split of the text.

    python -I scripts/test-index-month.py [games/lichess_db_standard_rated_2013-01.pgn.zst]

July 2026 lost 13 games this way (13 of 6,177 frame ends fell inside an [Event line) while `check` passed:
`get` joined the frames the same way `rows` did. Only Lichess's own count showed it.
"""
import hashlib, importlib.util, os, shutil, struct, subprocess, sys, tempfile
from compression import zstd

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(HERE, "index-month.py")
NOTE = struct.Struct("<III")


def load_index_month():
    spec = importlib.util.spec_from_file_location("index_month", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def cut_points(text, starts):
    cuts, g = [], 40
    for j in range(9):                      # 0: at the "[", 1-6: inside "[Event", 7: after "[Event ", 8: in its value
        cuts.append(starts[g] + j)
        g += 37
    for _ in range(4):                      # mid-movetext
        cuts.append((starts[g] + starts[g + 1]) // 2)
        g += 41
    moves_at = text.index(b"\n\n", starts[g]) + 2
    cuts += [moves_at + 20, moves_at + 60]  # a frame inside one game's movetext: no game starts in it
    g += 29
    cuts += [starts[g] - 40, starts[g] + 3]  # a frame whose only game start is cut at its end ("[Ev" | "ent ...")
    g += 23
    for j in (2, 5):                        # two more inside "[Event", close together
        cuts.append(starts[g] + j)
        g += 3
    return sorted(set(cuts))


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "..", "games", "lichess_db_standard_rated_2013-01.pgn.zst")
    im = load_index_month()
    with zstd.open(src, "rb") as f:
        text = f.read(6_000_000)
    starts = [m.start() for m in im.GAME_START.finditer(text)]
    text, starts = text[:starts[-1]], starts[:-1]            # whole games only
    cuts = cut_points(text, starts)
    games = [text[a:b] for a, b in zip(starts, starts[1:] + [len(text)])]

    work = tempfile.mkdtemp(prefix="index-month-test-")
    try:
        month = os.path.join(work, "lichess_db_standard_rated_2013-01.pgn.zst")
        with open(month, "wb") as f:
            for a, b in zip([0] + cuts, cuts + [len(text)]):
                packed = zstd.compress(text[a:b], 3)
                f.write(NOTE.pack(0x184D2A50, 4, len(packed)))
                f.write(packed)
        with open(month, "rb") as f:
            sha = hashlib.sha256(f.read()).hexdigest()
        with open(month + ".sha256", "w", encoding="ascii", newline="\n") as f:
            f.write(f"{sha}  {os.path.basename(month)}\n")
        folder = os.path.join(work, "index")
        for step in (["rows", month, folder, "--workers", "2", "--hash-all", "1", "--lines", "16"],
                     ["check", month, folder, "--sample", "0"]):
            run = subprocess.run([sys.executable, "-I", SCRIPT] + step, capture_output=True, text=True)
            if run.returncode:
                print(run.stdout + run.stderr)
                raise SystemExit(f"{step[0]} failed")

        index = im.Index(month, folder)
        plies, fp = index.column("plies"), index.column("fingerprint")
        wrong = []
        for g, game in enumerate(games[:index.note["games"]]):
            moves = im.sans(game.partition(b"\n\n")[2])
            if index.game(g) != game or int(plies[g]) != len(moves) or int(fp[g]) != im.fingerprint(moves):
                wrong.append(g)
        index.file.close()
        print(f"{len(cuts) + 1} frames, {len(games):,} games; indexed {index.note['games']:,}; "
              f"{len(games) - len(wrong):,} fetched by number with the right plies and fingerprint")
        if index.note["games"] != len(games) or wrong:
            raise SystemExit(f"FAILED" + (f"; first wrong game {wrong[0]:,}" if wrong else ""))
        print("passed")
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
