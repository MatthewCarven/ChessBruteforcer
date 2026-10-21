"""P6 (PONDER.md): the ponder-miss lab across builds, and its summary.

run <old exe> <new exe> <out dir> [first last]
    experiment.py misses with the ponder on 8 threads, positions first-last (default 0-7), three arms rotated position
    by position so that the machine's changes of speed fall on all of them: old (Hash 256), new (Hash 256, what the
    bot gets) and new171 (Hash 171). P6 sizes Hash in megabytes, so the old build's 256 was 11.2 M slots, which is the
    new build's 171: that arm is the replacement rule alone. Writes <out dir>/<arm>.jsonl. In a worktree, set
    PONDER_SCOPE_NET to the main checkout's bot/eval/h1024-3m.nnue. About 20 min for 8 positions, 9 cores.
summary <dir>
    summarise.py's misses table for each arm, then each new arm against old, position by position, where both
    searched B to the same depth D (each arm sets its own D: the depth a search from scratch reaches in 3 s).
"""
import json
import math
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ARMS = ("old", "new", "new171")


def run(old_exe, new_exe, out, first=0, last=7):
    arms = [("old", old_exe, 256), ("new", new_exe, 256), ("new171", new_exe, 171)]
    os.makedirs(out, exist_ok=True)
    start = time.time()
    for n in range(first, last + 1):
        k = n % len(arms)
        for name, exe, hash_mb in arms[k:] + arms[:k]:
            env = dict(os.environ, PONDER_SCOPE_ENGINE=exe, PONDER_SCOPE_HASH=str(hash_mb))
            t = time.time()
            subprocess.run([sys.executable, os.path.join(HERE, "experiment.py"), "misses", str(n), str(n),
                            os.path.join(out, f"{name}.jsonl"), "8"], env=env, check=True, stdout=subprocess.DEVNULL)
            print(f"position {n} {name}: {time.time() - t:.0f} s (total {(time.time() - start) / 60:.1f} min)", flush=True)


def geo(xs):
    return math.exp(sum(math.log(x) for x in xs) / len(xs)) if xs else float("nan")


def summary(lab):
    for arm in ARMS:
        print(f"### {arm}\n", flush=True)
        subprocess.run([sys.executable, os.path.join(HERE, "summarise.py"), os.devnull, os.path.join(lab, f"{arm}.jsonl")], check=True)
        print()
    rows = {a: {r["n"]: r for r in (json.loads(line) for line in open(os.path.join(lab, f"{a}.jsonl"), encoding="utf-8")
                                    if line.strip())} for a in ARMS}
    nodes = lambda search: search["infos"][-1]["nodes"]
    cases = [("no ponder", lambda r: r["noponder"])] + [
        (f"after a {f} x soft miss", (lambda f: lambda r: r[f"miss{f}"]["after"])(f)) for f in ("0.75", "1.5", "3.0")]
    print("D per position (old, new, new171):", {n: tuple(rows[a][n]["D"] for a in ARMS) for n in sorted(rows["old"])})
    for arm in ("new", "new171"):
        same = [n for n in sorted(rows["old"]) if n in rows[arm] and rows[arm][n]["D"] == rows["old"][n]["D"]]
        print(f"\n{arm} against old, the {len(same)} positions where both searched to the same D ({same}):\n")
        print("| search at B to depth D | nodes, new/old (geometric mean) | min | max | ms, new/old (geometric mean) |")
        print("|---|---:|---:|---:|---:|")
        for name, pick in cases:
            ratio = [nodes(pick(rows[arm][n])) / nodes(pick(rows["old"][n])) for n in same]
            ms = [pick(rows[arm][n])["wall_ms"] / pick(rows["old"][n])["wall_ms"] for n in same]
            print(f"| {name} | {geo(ratio):.2f} | {min(ratio):.2f} | {max(ratio):.2f} | {geo(ms):.2f} |")


if __name__ == "__main__":
    if len(sys.argv) >= 5 and sys.argv[1] == "run":
        run(sys.argv[2], sys.argv[3], sys.argv[4], *map(int, sys.argv[5:7]))
    elif len(sys.argv) == 3 and sys.argv[1] == "summary":
        summary(sys.argv[2])
    else:
        print(__doc__)
        sys.exit(1)
