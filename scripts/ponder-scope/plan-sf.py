"""Stockfish's view of one game's evaluation path, beside the bot's own (plan.py's JSON).

  run <plan.json> <out.jsonl> [--depth 18]   every position of the game, Stockfish 16 in WSL on one thread at nice 19
                                             (resumes: positions already in <out.jsonl> are skipped)
  summary <plan.json> <out.jsonl>            the tables

usage: bot\\lichess-bot\\.venv\\Scripts\\python.exe -I scripts/ponder-scope/plan-sf.py run|summary ...
Scores are White's point of view, centipawns; a mate is +-(100000 - its distance).
"""
import json
import os
import sys

import chess
import chess.engine

STOCKFISH = ["wsl.exe", "-d", "Ubuntu-24.04", "--", "nice", "-n", "19", "/usr/games/stockfish"]
BELOW_NORMAL = 0x00004000
MATE = 100000


def option(name, default, kind=str):
    return kind(sys.argv[sys.argv.index(name) + 1]) if name in sys.argv else default


def white_cp(score):
    return score.white().score(mate_score=MATE)


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def read_jsonl(path):
    if not os.path.exists(path):
        return {}
    with open(path, encoding="utf-8") as f:
        return {r["ply"]: r for r in (json.loads(line) for line in f if line.strip())}


def run(plan_path, out_path):
    plan = load(plan_path)
    depth = option("--depth", 18, int)
    done = read_jsonl(out_path)
    sf = chess.engine.SimpleEngine.popen_uci(STOCKFISH, creationflags=BELOW_NORMAL)
    sf.configure({"Threads": 1, "Hash": 64})
    board = chess.Board()
    try:
        with open(out_path, "a", encoding="utf-8", newline="\n") as out:
            for ply in range(len(plan["moves"]) + 1):
                if ply not in done and not board.is_game_over():
                    info = sf.analyse(board, chess.engine.Limit(depth=depth), game=plan["game"])
                    pv = info.get("pv") or []
                    out.write(json.dumps({"ply": ply, "fen": board.fen(), "cp": white_cp(info["score"]),
                                          "best": pv[0].uci() if pv else None, "depth": info.get("depth")}) + "\n")
                    out.flush()
                    print(f"ply {ply}: {white_cp(info['score'])}", flush=True)
                if ply < len(plan["moves"]):
                    board.push_uci(plan["moves"][ply])
    finally:
        sf.quit()


def clamp(cp):
    return max(-1000, min(1000, cp))


def summary(plan_path, out_path):
    sys.stdout.reconfigure(encoding="utf-8")
    plan = load(plan_path)
    sf = read_jsonl(out_path)
    rows = plan["rows"]
    print("| move | src | ours (White's view) | Stockfish before our move | after it | our move cost (Stockfish) | SF's move |")
    print("|---:|---|---:|---:|---:|---:|---|")
    costs = []
    for r in rows:
        n = r["ply"]
        if n not in sf or n + 1 not in sf:
            continue
        before, after = sf[n]["cp"], sf[n + 1]["cp"]
        ours = -r["cp"] if r["cp"] is not None else None        # the bot is Black here: its score is Black's view
        cost = clamp(after) - clamp(before)                      # positive: good for White, i.e. it cost us
        costs.append((cost, r, before, after))
        print(f"| {r['move_no']} | {r['source']} | {ours} | {before} | {after} | {cost:+d} | "
              f"{'=' if sf[n]['best'] == r['move'] else sf[n]['best']} |")
    print("\nour five costliest moves by Stockfish:")
    for cost, r, before, after in sorted(costs, key=lambda c: -c[0])[:5]:
        print(f"  {r['move_no']}...{r['san']} ({r['source']}): Stockfish {before} -> {after} ({cost:+d}); ours "
              f"{-r.get('prev_cp', 0) if r.get('prev_cp') is not None else '-'} -> {-r['cp']}; best {sf[r['ply']]['best']}")
    same = sum(1 for c in costs if sf[c[1]["ply"]]["best"] == c[1]["move"])
    print(f"\nour move = Stockfish's: {same} of {len(costs)}")

    # In the balance (our score -3.00 or better before the move, as plan.py has it): did the moves that kept the plan
    # cost more than the ones made afresh?  And how far was our score from Stockfish's?
    balance = [c for c in costs if c[1].get("prev_cp") is not None and c[1]["prev_cp"] >= -300]
    print("\n| in the balance | moves | Stockfish's cost of our moves, total (cp) | mean | moves costing 50+ | "
          "our score less Stockfish's, before our move (mean, cp, Black's view) |")
    print("|---|---:|---:|---:|---:|---:|")
    for name, group in (("after a hit", [c for c in balance if c[1]["source"] == "hit"]),
                        ("after a miss", [c for c in balance if c[1]["source"] == "go"]), ("all", balance)):
        if not group:
            continue
        gap = [c[1]["cp"] - (-clamp(c[2])) for c in group]   # ours (Black's view) less Stockfish's (Black's view)
        total = sum(c[0] for c in group)
        print(f"| {name} | {len(group)} | {total:+d} | {total / len(group):+.1f} | {sum(1 for c in group if c[0] >= 50)} | "
              f"{sum(gap) / len(gap):+.1f} |")

    # Where each engine thought the game turned: the first of our moves from which the score stayed -1.00 or worse
    # for us (Black's view), ours against Stockfish's.
    def turn(scores):
        for i in range(len(scores)):
            if all(s <= -100 for s in scores[i:]):
                return i
        return None
    seq = [(c[1], -clamp(c[2])) for c in costs]
    ours, theirs = turn([r["cp"] for r, _ in seq]), turn([s for _, s in seq])
    print(f"\nfrom here on -1.00 or worse for us: ours from move {seq[ours][0]['move_no'] if ours is not None else '-'}, "
          f"Stockfish's from move {seq[theirs][0]['move_no'] if theirs is not None else '-'}")
    print("\nour score / Stockfish's before our move (Black's view), every 3rd move:")
    print(" ".join(f"{r['move_no']}:{r['cp']}/{s}" for r, s in seq[::3]))


if __name__ == "__main__":
    {"run": run, "summary": summary}[sys.argv[1]](sys.argv[2], sys.argv[3])
