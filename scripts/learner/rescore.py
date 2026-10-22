"""Re-score a sample of the book learner's positions (bot/books/ours.jsonl) with one evaluation at a fixed depth.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/learner/rescore.py <bot folder> <engine exe> <eval file>
        <arm name> <out.csv> [--n 200] [--depth 14] [--seed 1] [--learnt-with eval-sf1] [--part 0 --parts 1]

--n 100000 takes every position. --learnt-with keeps only the rows that evaluation learnt (rows from before
2026-10-22 have no "eval" and count as eval-sf1). --part i --parts k takes every k-th of the sample from the i-th,
so k processes (each with its own out.csv) share one run.

For each sampled position: the engine's best move and score at --depth; if the best move isn't the learnt move,
the learnt move's score too (the position after it, searched one ply less, negated), so loss = best - learnt.
The engine has no searchmoves, hence the second search. One thread, Hash 64, a fresh game (ucinewgame) per search,
below-normal priority. Appends to out.csv and skips positions already in it, so it can be stopped and run again.
Run it from a copy of the engine build (not bot\\lichess-bot\\engines), one arm per process and file: the bot's net
(bot\\eval\\h1024-3m.nnue), and the learner's own eval-sf1.txt as the noise floor (how often the same evaluation
re-searched at a fixed depth disagrees with its own 45 s answer). summary.py reads the files.
"""
import csv, ctypes, json, os, random, sys, time
import chess, chess.engine

bot, exe, evalfile, arm, out = sys.argv[1:6]


def option(name, default, kind=int):
    return kind(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default


N, DEPTH, SEED = option("--n", 200), option("--depth", 14), option("--seed", 1)
PART, PARTS, LEARNT_WITH = option("--part", 0), option("--parts", 1), option("--learnt-with", None, str)

k32 = ctypes.windll.kernel32
k32.GetCurrentProcess.restype = ctypes.c_void_p
k32.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
k32.SetPriorityClass(k32.GetCurrentProcess(), 0x4000)   # below normal; the engine inherits it

rows = {}
for line in open(os.path.join(bot, "books", "ours.jsonl"), encoding="utf-8"):
    if line.strip():
        r = json.loads(line)
        rows[r["key"]] = r
keys = sorted(rows)
random.Random(SEED).shuffle(keys)
sample = [k for k in keys[:N] if LEARNT_WITH is None or rows[k].get("eval", "eval-sf1") == LEARNT_WITH][PART::PARTS]

done = set()
if os.path.exists(out):
    done = {int(r["key"]) for r in csv.DictReader(open(out, encoding="utf-8"))}
fields = ["arm", "key", "fen", "learnt", "why", "learnt_depth", "learnt_cp", "best", "best_cp", "learnt_cp_now",
          "loss", "depth", "nodes", "seconds"]
new_file = not os.path.exists(out)
print(f"{arm}: {len(sample)} positions in this part, {len(done & set(sample))} of them done already", flush=True)
engine = chess.engine.SimpleEngine.popen_uci(exe)
engine.configure({"Hash": 64, "Threads": 1, "EvalFile": evalfile})


def cp(info, turn):
    return info["score"].pov(turn).score(mate_score=10000)


with open(out, "a", newline="", encoding="utf-8") as f:
    w = csv.DictWriter(f, fieldnames=fields)
    if new_file:
        w.writeheader()
    for i, key in enumerate(sample):
        if key in done:
            continue
        r = rows[key]
        board = chess.Board(r["fen"])
        learnt = chess.Move.from_uci(r["move"])
        t0 = time.time()
        info = engine.analyse(board, chess.engine.Limit(depth=DEPTH), game=object())
        best, best_cp = info["pv"][0], cp(info, board.turn)
        nodes = info.get("nodes", 0)
        if best == learnt:
            learnt_cp = best_cp
        else:
            after = board.copy()
            after.push(learnt)
            reply = engine.analyse(after, chess.engine.Limit(depth=DEPTH - 1), game=object())
            learnt_cp = -cp(reply, after.turn)
            nodes += reply.get("nodes", 0)
        w.writerow({"arm": arm, "key": key, "fen": r["fen"], "learnt": r["move"], "why": r["why"],
                    "learnt_depth": r.get("depth"), "learnt_cp": r["best_cp"], "best": best.uci(), "best_cp": best_cp,
                    "learnt_cp_now": learnt_cp, "loss": best_cp - learnt_cp, "depth": info.get("depth"),
                    "nodes": nodes, "seconds": round(time.time() - t0, 2)})
        f.flush()
        print(f"{arm} {i + 1}/{len(sample)} {r['move']} v {best.uci()} loss {best_cp - learnt_cp} "
              f"({time.time() - t0:.1f} s)", flush=True)
engine.quit()
