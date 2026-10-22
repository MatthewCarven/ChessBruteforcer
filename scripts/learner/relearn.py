"""Option (c) for the book learner (Matthew, 2026-10-22): where the bot's net thinks a move learnt with sf1 loses
--min-loss centipawns or more (50), the net's own move takes its place.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/learner/relearn.py <bot folder> <rescore.csv> [...]
        [--min-loss 50] [--eval h1024-3m] [--apply]

Reads rescore.py's files (any number; a position in several counts once) and the bot folder's books/ours.jsonl,
through the bot folder's own learn.py. A position qualifies when its loss is --min-loss or more and ours.jsonl's
latest row there still has the move that was scored (otherwise the learner has moved on, and it's skipped).
Without --apply it only lists them. With --apply:
- it refuses (exit 2) while a learn.py is running (a command line with bot\\learn.py in it), or if it can't tell
  (exit 3): the learner appends to ours.jsonl too, and two writers could interleave rows. Stop the learner first
  (close its window), and start it again afterwards (bot\\learn.cmd). The bot can stay on;
- it copies ours.jsonl and ours.bin to ours.jsonl.bak-<time> and ours.bin.bak-<time> in books\\ (copying a backup
  back undoes it);
- it appends one row per position to ours.jsonl: why "re-learnt", the re-score's best move at its depth, and the old
  move, its why and its loss in "was", "was_why" and "was_loss". The learner keeps the latest row per position, so
  nothing is deleted, and a second run adds nothing;
- it rebuilds ours.bin with the learner's own write_book (a .tmp moved over the old, tried again while lichess-bot
  has it open), and prints the rows added and the book's new size.
"""
import csv, importlib.util, json, os, re, shutil, subprocess, sys, time
import chess, chess.polyglot

LEARN_PY = re.compile(r"bot[\\/]+learn\.py\b", re.IGNORECASE)


def learner_running():
    """The processes running a learn.py, as (pid, command line), this one aside; None if the check couldn't run."""
    command = ("Get-CimInstance Win32_Process | Where-Object { $_.CommandLine } | "
               "ForEach-Object { [string]$_.ProcessId + ' ' + $_.CommandLine }")
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", command],
                             capture_output=True, text=True, timeout=120)
    except (OSError, subprocess.TimeoutExpired):
        return None
    if out.returncode or not out.stdout.strip():
        return None
    found = []
    for line in out.stdout.splitlines():
        pid, _, cmd = line.strip().partition(" ")
        if pid.isdigit() and int(pid) != os.getpid() and LEARN_PY.search(cmd) and "Win32_Process" not in cmd:
            found.append((int(pid), cmd))
    return found


def load_learn(bot):
    spec = importlib.util.spec_from_file_location("learn", os.path.join(bot, "learn.py"))
    learn = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(learn)
    return learn


def plan(learn, files, min_loss, eval_name):
    """(positions re-scored, how many lose min_loss or more, how many of those the learner moved on from, new rows)."""
    scored = {}
    for path in files:
        for r in csv.DictReader(open(path, encoding="utf-8")):
            scored[int(r["key"])] = r
    latest = learn.learnt()
    over = [r for r in scored.values() if int(r["loss"]) >= min_loss]
    picked, moved_on = [], 0
    when = time.strftime("%Y-%m-%d %H:%M")
    for r in sorted(over, key=lambda r: -int(r["loss"])):
        key = int(r["key"])
        row = latest.get(key)
        if row is None or row["move"] != r["learnt"]:
            moved_on += 1
            continue
        board = chess.Board(row["fen"])
        best = chess.Move.from_uci(r["best"])
        if chess.polyglot.zobrist_hash(board) != key or best not in board.legal_moves:
            raise SystemExit(f"position {key}: the re-score's move {r['best']} doesn't fit {row['fen']}")
        picked.append({"why": "re-learnt", "best_cp": int(r["best_cp"]), "was": row["move"], "was_why": row["why"],
                       "was_loss": int(r["loss"]), "key": key, "polyglot": learn.polyglot_move(board, best),
                       "move": best.uci(), "san": board.san(best), "fen": row["fen"], "game": row.get("game"),
                       "played": row.get("played"), "depth": int(r["depth"]), "when": when, "eval": eval_name})
    return len(scored), len(over), moved_on, picked


def apply(learn, picked):
    """Back up, append, rebuild: (the backups made, the book's size afterwards)."""
    stamp = time.strftime("%Y%m%d-%H%M%S")
    backups = []
    for path in (learn.LEARNT, learn.OURS):
        if os.path.exists(path):
            shutil.copy2(path, f"{path}.bak-{stamp}")
            backups.append(f"{path}.bak-{stamp}")
    with open(learn.LEARNT, "a", encoding="utf-8") as out:
        out.write("".join(json.dumps(p) + "\n" for p in picked))
    return backups, learn.write_book()


def main(argv, running=learner_running):
    bot = argv[0]
    files = [a for a in argv[1:] if a.lower().endswith(".csv")]

    def option(name, default, kind=int):
        return kind(argv[argv.index(name) + 1]) if name in argv else default

    min_loss, eval_name = option("--min-loss", 50), option("--eval", "h1024-3m", str)
    learn = load_learn(bot)
    n, over, moved_on, picked = plan(learn, files, min_loss, eval_name)
    print(f"{n} positions re-scored; {over} lose {min_loss} cp or more; {moved_on} of those already changed in "
          f"ours.jsonl (skipped); {len(picked)} to re-learn")
    for p in picked[:12]:
        print(f"  {p['was']} -> {p['san']} ({p['was_loss']} cp) in {p['fen']}")
    if "--apply" not in argv:
        return 0
    if not picked:
        print("nothing to apply")
        return 0
    found = running()
    if found is None:
        print("refused: couldn't check for a running learner (PowerShell's process list); nothing changed")
        return 3
    if found:
        print("refused: the learner is running (close its window first; nothing changed):")
        for pid, cmd in found:
            print(f"  pid {pid}: {cmd[-120:]}")
        return 2
    backups, size = apply(learn, picked)
    print(f"backed up: {', '.join(os.path.basename(b) for b in backups)}")
    print(f"added {len(picked)} rows to {learn.LEARNT}; ours.bin rebuilt: {size} positions")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
