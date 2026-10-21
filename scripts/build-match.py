"""This checkout's engine against an earlier commit's: an SPRT match of the new build against the old, as the
evaluation matches were played (MATCHES.md): 10+0.1, the bot's network on both sides, one thread and Hash 64 each,
no tables in the engines (ours adjudicate), the 300 openings of games\\tune\\openings-300.txt in colour-reversed pairs,
5 games at a time, SPRT [0, 10], at most 2,000 games. No pondering. (P4's measure, 2026-10-21: a change to the
search itself, so both sides are builds, not options.)

    python scripts/build-match.py <tag> <old commit> [--games 2000] [--concurrency 5] [--tc 10+0.1] [--net h1024-3m]
        [--fixed]

The old commit's src is taken with `git archive` and built in %TEMP%\\cb-build-match\\old-src; this checkout is built
here; both builds and the match tool (this checkout's) are copied to %TEMP%\\cb-build-match, so the repo stays
buildable while it plays. Below-normal priority, which the match tool and engines inherit. Before it starts it waits
while any other ChessBruteforcer.Engine is running (another match, or the bot) or commit headroom is under 2 GB.
--fixed plays every game, with no SPRT to stop it.

Logs: games\\builds\\<tag>.log and .pgn in the main checkout. Results are from the new build's point of view.
"""
import ctypes, datetime, hashlib, io, os, shutil, subprocess, sys, tarfile, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from importlib import import_module  # noqa: E402

depth_match = import_module("depth-match")
REPO, MAIN, git, free_commit_gb, other_engines, options = (depth_match.REPO, depth_match.MAIN, depth_match.git,
                                                           depth_match.free_commit_gb, depth_match.other_engines,
                                                           depth_match.options)
COPY = os.path.join(os.environ["TEMP"], "cb-build-match")
OUT = os.path.join(MAIN, "games", "builds")
BUILD = ["-c", "Release", "--nologo", "-v", "q", "--disable-build-servers", "-p:UseSharedCompilation=false"]


def build(old):
    for project in ("Engine", "Match"):
        subprocess.run(["dotnet", "build", os.path.join(REPO, "src", "ChessBruteforcer." + project), *BUILD], check=True)
    if os.path.exists(COPY):
        shutil.rmtree(COPY)
    source = os.path.join(COPY, "old-src")
    archive = subprocess.run(["git", "archive", "--format=tar", old, "src"], cwd=REPO, capture_output=True,
                             check=True).stdout
    with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
        tar.extractall(source, filter="data")
    subprocess.run(["dotnet", "build", os.path.join(source, "src", "ChessBruteforcer.Engine"), *BUILD], check=True)
    shutil.copytree(os.path.join(REPO, "src", "ChessBruteforcer.Engine", "bin", "Release", "net8.0"), os.path.join(COPY, "new"))
    shutil.copytree(os.path.join(source, "src", "ChessBruteforcer.Engine", "bin", "Release", "net8.0"), os.path.join(COPY, "old"))
    shutil.copytree(os.path.join(REPO, "src", "ChessBruteforcer.Match", "bin", "Release", "net8.0"), os.path.join(COPY, "match"))


def main():
    settings, rest = options(sys.argv[1:], {"games": 2000, "concurrency": 5, "tc": "10+0.1", "net": "h1024-3m"})
    fixed = "--fixed" in rest
    rest = [a for a in rest if a != "--fixed"]
    if len(rest) != 2:
        print(__doc__)
        return 1
    tag, old = rest
    old = git("rev-parse", "--short", old)
    new = git("rev-parse", "--short", "HEAD")
    dirty = git("status", "--porcelain", "--untracked-files=no", "src")
    ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)   # below normal
    os.makedirs(OUT, exist_ok=True)
    log, pgn = os.path.join(OUT, tag + ".log"), os.path.join(OUT, tag + ".pgn")
    if os.path.exists(pgn):
        print(f"{pgn} is there already: rename it (and its .log) to play {tag} again.")
        return 1
    while other_engines() or free_commit_gb() < 2:
        print(f"{datetime.datetime.now():%H:%M:%S} waiting ({len(other_engines())} other engines, "
              f"{free_commit_gb():.1f} GB commit free)", flush=True)
        time.sleep(60)
    build(old)
    net = os.path.join(COPY, settings["net"] + ".nnue")
    shutil.copyfile(os.path.join(MAIN, "bot", "eval", settings["net"] + ".nnue"), net)
    with open(net, "rb") as f:
        net_hash = hashlib.sha256(f.read()).hexdigest()[:12]
    sprt = [] if fixed else ["--sprt", "0", "10"]
    with open(log, "a", encoding="utf-8") as out:
        out.write(f"{datetime.datetime.now():%Y-%m-%d %H:%M:%S}: new {new}{' (with uncommitted src changes)' if dirty else ''}"
                  f" v old {old}, {settings['games']} games at {settings['tc']}, {settings['concurrency']} at a time, "
                  f"{'fixed' if fixed else 'SPRT [0, 10]'}, net {settings['net']} (sha256 {net_hash}), from {REPO}\n")
        out.flush()
        print(f"{datetime.datetime.now():%H:%M:%S} {tag}: started (progress in {log})", flush=True)
        sides = []
        for name in ("new", "old"):
            sides += ["--engine", f"name={name}", f"cmd={os.path.join(COPY, name, 'ChessBruteforcer.Engine.exe')}",
                      f"option.EvalFile={net}", "option.Threads=1", "option.Hash=64"]
        code = subprocess.run([os.path.join(COPY, "match", "ChessBruteforcer.Match.exe"), *sides,
                               "--games", str(settings["games"]), "--tc", settings["tc"],
                               "--concurrency", str(settings["concurrency"]), "--tables", os.path.join(MAIN, "tables"),
                               *sprt, "--openings", os.path.join(MAIN, "games", "tune", "openings-300.txt"),
                               "--pgn", pgn], stdout=out, stderr=subprocess.STDOUT).returncode
        out.write(f"{datetime.datetime.now():%Y-%m-%d %H:%M:%S}: finished, exit {code}\n")
    print(f"{datetime.datetime.now():%H:%M:%S} {tag}: finished, exit {code}", flush=True)
    return code


if __name__ == "__main__":
    sys.exit(main())
