"""The repetition rule in the lab: the bot's own false draws (TURNS.md, "The false draw") searched again under both rules.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/repetition-lab.py <engine .exe> <out .jsonl> [--depth 10]
        [--cpu 14] [--games games/turns/games.json] [--eval bot/eval/h1024-3m.nnue] [--summary]

The moves are those scripts/turning-points.py calls false draws (on branch claude/goofy-ellis-ded337): one of our
moves in a game we didn't win that scored exactly 0.00, where the move itself or the line logged with it comes back
to a position from the game so far. Each position is searched as the bot saw it (the game's moves from the start, so
the engine knows the history) to a fixed depth, on one thread pinned to one CPU at below-normal priority, from an
empty table, once with RepetitionRule Twofold (the old rule) and once with Threefold (the new one). A fixed depth
doesn't depend on the machine's load, so this can run beside a match without changing its answers.

Per move: the old and new score and move, the line's length, and whether the bot's own logged move repeated. The
summary: how many of the old rule's 0.00 the new one still scores 0.00, how many moves change, and where the new
scores lie.
"""
import ctypes, json, os, sys
import chess, chess.engine

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def main_checkout():
    """The folder that holds .git (the data lives there, not in a worktree)."""
    common = os.path.join(REPO, ".git")
    if os.path.isfile(common):   # a worktree: .git is a file naming <main>/.git/worktrees/<name>
        with open(common, encoding="utf-8") as f:
            gitdir = f.read().split(":", 1)[1].strip()
        return os.path.dirname(os.path.dirname(os.path.dirname(gitdir)))
    return REPO


MAIN = main_checkout()


def option(args, name, default):
    if name in args:
        i = args.index(name)
        value = args[i + 1]
        del args[i:i + 2]
        return value
    return default


def false_draw(m):
    """As turning-points.py: 'move' if our 0.00 move repeats a position of the game so far, 'line' if its logged line
    does, else False."""
    if m["score"] != ["cp", 0]:
        return False
    board = chess.Board()
    seen = {board._transposition_key()}
    for mv in m["moves"]:
        board.push_uci(mv)
        seen.add(board._transposition_key())
    line = [m["move"]] + (m["pv"][1:] if m["pv"] and m["pv"][0] == m["move"] else [])
    for i, mv in enumerate(line):
        try:
            board.push_uci(mv)
        except ValueError:
            break
        if board._transposition_key() in seen:
            return "move" if i == 0 else "line"
    return False


kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
kernel32.OpenProcess.restype = ctypes.c_void_p
kernel32.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
kernel32.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
kernel32.SetProcessAffinityMask.argtypes = [ctypes.c_void_p, ctypes.c_size_t]
kernel32.CloseHandle.argtypes = [ctypes.c_void_p]


def pin(engine, cpu):
    handle = kernel32.OpenProcess(0x0200 | 0x1000, False, engine.transport.get_pid())
    try:
        kernel32.SetProcessAffinityMask(handle, 1 << cpu)
        kernel32.SetPriorityClass(handle, 0x4000)   # below normal
    finally:
        kernel32.CloseHandle(handle)


def cp(score):
    s = score.pov(score.turn)
    return s.score(mate_score=30000)


def stockfish_scores():
    """Stockfish 16's score (ours, centipawns) before each of our moves, where turning-points.py has one."""
    path = os.path.join(MAIN, "games", "turns", "curves.jsonl")
    if not os.path.exists(path):
        return {}
    return {r["key"]: r["sf"] for r in map(json.loads, open(path, encoding="utf-8"))}


BANDS = [("0.00", lambda c: c == 0), ("+0.01 or more", lambda c: c > 0), ("-0.01 to -0.99", lambda c: -100 < c < 0),
         ("-1.00 to -2.99", lambda c: -300 < c <= -100), ("-3.00 or worse", lambda c: c <= -300)]


def table(title, groups):
    """One row a group of moves: how many, the old rule's 0.00s, the new rule's, moves changed, lines, scores."""
    print(f"\n{title}")
    print("| moves | n | old rule 0.00 | new rule 0.00 | new move | line past one move: old / new | "
          + " | ".join(f"new {name}" for name, _ in BANDS[1:]) + " |")
    print("|---|---:|---:|---:|---:|---:|" + "---:|" * (len(BANDS) - 1))
    for name, rows in groups:
        if not rows:
            continue
        cells = [len(rows), sum(r["old_cp"] == 0 for r in rows), sum(r["new_cp"] == 0 for r in rows),
                 sum(r["new_move"] != r["old_move"] for r in rows),
                 f"{sum(r['old_pv'] > 1 for r in rows)} / {sum(r['new_pv'] > 1 for r in rows)}"]
        cells += [sum(test(r["new_cp"]) for r in rows) for _, test in BANDS[1:]]
        print(f"| {name} | " + " | ".join(str(c) for c in cells) + " |")


def summary(path):
    rows = [json.loads(line) for line in open(path, encoding="utf-8") if line.strip()]
    sf = stockfish_scores()
    for r in rows:
        r["sf"] = sf.get(f"{r['game']}:{r['ply']}")
    depth = rows[0]["depth"] if rows else "?"
    print(f"{len(rows)} moves the old rule may have called a false draw, from the bot's games not won (10-14 to 10-20), "
          f"searched again at depth {depth}, one thread, the network h1024-3m")
    lost = [r for r in rows if r["result"] == 0.0]
    drawn = [r for r in rows if r["result"] == 0.5]
    table("By the game's result, and by what repeated (the move itself, or a later move of its logged line):", [
        ("lost: the move repeated", [r for r in lost if r["kind"] == "move"]),
        ("lost: the line repeated", [r for r in lost if r["kind"] == "line"]),
        ("drawn: the move repeated", [r for r in drawn if r["kind"] == "move"]),
        ("drawn: the line repeated", [r for r in drawn if r["kind"] == "line"])])
    known = [r for r in rows if r["sf"] is not None]
    table(f"By Stockfish's score before the move (known for {len(known)} of the {len(rows)}; ours, as the bot's side):", [
        ("Stockfish -1.50 or worse", [r for r in known if r["sf"] <= -150]),
        ("Stockfish -1.49 to +1.49", [r for r in known if -150 < r["sf"] < 150]),
        ("Stockfish +1.50 or better", [r for r in known if r["sf"] >= 150])])


def main():
    args = sys.argv[1:]
    if "--summary" in args:
        args.remove("--summary")
        summary(args[0])
        return
    depth = int(option(args, "--depth", "10"))
    cpu = int(option(args, "--cpu", "14"))
    games_path = option(args, "--games", os.path.join(MAIN, "games", "turns", "games.json"))
    net = option(args, "--eval", os.path.join(MAIN, "bot", "eval", "h1024-3m.nnue"))
    exe, out = args
    games = json.load(open(games_path, encoding="utf-8"))
    todo = []
    for g in games:
        if g["result"] == 1.0:
            continue
        for m in g["moves"]:
            kind = false_draw(m)
            if kind:
                todo.append((g, m, kind))
    print(f"{len(todo)} false draws in {sum(g['result'] != 1.0 for g in games)} games not won", flush=True)
    done = set()
    if os.path.exists(out):
        done = {(r["game"], r["ply"]) for r in map(json.loads, open(out, encoding="utf-8")) }
    engines = {}
    for rule in ("Twofold", "Threefold"):
        engine = chess.engine.SimpleEngine.popen_uci(exe)
        pin(engine, cpu)
        engine.configure({"EvalFile": net, "Hash": 64, "Threads": 1, "RepetitionRule": rule})
        engines[rule] = engine
    try:
        for g, m, kind in todo:
            if (g["id"], m["ply"]) in done:
                continue
            board = chess.Board()
            for mv in m["moves"]:
                board.push_uci(mv)
            row = {"game": g["id"], "ply": m["ply"], "kind": kind, "played": m["move"], "logged_depth": m["depth"],
                   "depth": depth, "result": g["result"]}
            for rule, key in (("Twofold", "old"), ("Threefold", "new")):
                info = engines[rule].analyse(board, chess.engine.Limit(depth=depth), game=object())
                pv = info.get("pv", [])
                row[f"{key}_cp"] = cp(info["score"])
                row[f"{key}_move"] = pv[0].uci() if pv else None
                row[f"{key}_pv"] = len(pv)
            with open(out, "a", encoding="utf-8", newline="\n") as f:
                f.write(json.dumps(row) + "\n")
            print(f"{g['id']} ply {m['ply']} ({kind}): old {row['old_cp']} {row['old_move']} ({row['old_pv']}), "
                  f"new {row['new_cp']} {row['new_move']} ({row['new_pv']})", flush=True)
    finally:
        for engine in engines.values():
            engine.quit()
    summary(out)


if __name__ == "__main__":
    main()
