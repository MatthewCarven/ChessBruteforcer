"""Does depth win games? Our engine against itself with more search on one side: a fixed depth against one ply less,
or a longer clock against a shorter one (time odds). Matthew's question, 2026-10-21.

    python scripts/depth-match.py 9v8 13v12/300 20+0.2v10+0.1 [--games 600] [--concurrency 7] [--clock-concurrency 5]
        [--net h1024-3m]

Each pair is "<stronger>v<weaker>[/games]": a number is a fixed depth (`go depth N`, no clock), anything with a + is a
clock (base+increment, seconds). Results are from the stronger side's point of view. A fixed depth with one thread
plays the same game whatever the machine is doing, so depth pairs run --concurrency at a time; pairs on the clock
run --clock-concurrency at a time, as the earlier matches did. Pairs run one after another; a pair
whose .pgn is there already is skipped, so a stopped series picks up at the next pair (rename a pair's .pgn and .log
to play it again).

Both sides are the same build (this checkout's, built here and copied to %TEMP%\\cb-depth so the repo stays
buildable) with the bot's network (bot\\eval\\<net>.nnue in the main checkout), one thread and Hash 64 each, no tables
in the engines (ours adjudicate), the 300 openings of games\\tune\\openings-300.txt in colour-reversed pairs. Below-
normal priority, which the match tool and engines inherit. Before each pair it waits while any other
ChessBruteforcer.Engine is running (another match, or the bot) or commit headroom is under 2 GB.

Logs: games\\depth\\<tag>.log and .pgn in the main checkout (tag d9-d8, or tc20+0.2-tc10+0.1).
"""
import ctypes, datetime, hashlib, os, shutil, subprocess, sys, time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
COPY = os.path.join(os.environ["TEMP"], "cb-depth")


def git(*args):
    return subprocess.run(["git", *args], cwd=REPO, capture_output=True, text=True, check=True).stdout.strip()


MAIN = os.path.abspath(os.path.join(REPO, git("rev-parse", "--git-common-dir"), ".."))
OUT = os.path.join(MAIN, "games", "depth")


class MemoryStatus(ctypes.Structure):
    _fields_ = [("dwLength", ctypes.c_uint32), ("dwMemoryLoad", ctypes.c_uint32),
                ("ullTotalPhys", ctypes.c_uint64), ("ullAvailPhys", ctypes.c_uint64),
                ("ullTotalPageFile", ctypes.c_uint64), ("ullAvailPageFile", ctypes.c_uint64),
                ("ullTotalVirtual", ctypes.c_uint64), ("ullAvailVirtual", ctypes.c_uint64),
                ("ullAvailExtendedVirtual", ctypes.c_uint64)]


def free_commit_gb():
    status = MemoryStatus()
    status.dwLength = ctypes.sizeof(MemoryStatus)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status))
    return status.ullAvailPageFile / 2**30


def other_engines():
    listing = subprocess.run(["tasklist", "/FI", "IMAGENAME eq ChessBruteforcer.Engine.exe", "/FO", "CSV", "/NH"],
                             capture_output=True, text=True).stdout
    return [line for line in listing.splitlines() if line.startswith('"ChessBruteforcer.Engine')]


def side(text):
    """(name, time control) for one side of a pair: "9" is depth 9, "20+0.2" a clock."""
    if "+" in text:
        return "tc" + text, text
    return "d" + str(int(text)), "depth=" + text


def options(args, spec):
    values = {}
    for name, default in spec.items():
        values[name] = default
        if "--" + name in args:
            i = args.index("--" + name)
            values[name] = type(default)(args[i + 1])
            del args[i:i + 2]
    return values, args


def build():
    for project in ("Engine", "Match"):
        subprocess.run(["dotnet", "build", os.path.join("src", "ChessBruteforcer." + project), "-c", "Release",
                        "--nologo", "-v", "q", "--disable-build-servers", "-p:UseSharedCompilation=false"],
                       cwd=REPO, check=True)
    if os.path.exists(COPY):
        shutil.rmtree(COPY)
    for project in ("Engine", "Match"):
        shutil.copytree(os.path.join(REPO, "src", "ChessBruteforcer." + project, "bin", "Release", "net8.0"),
                        os.path.join(COPY, project.lower()))


def main():
    settings, pairs = options(sys.argv[1:], {"games": 600, "concurrency": 7, "clock-concurrency": 5,
                                             "net": "h1024-3m"})
    if not pairs:
        print(__doc__)
        return 1
    pairs = [(*(side(s) for s in pair.split("/")[0].split("v")),
              int(pair.split("/")[1]) if "/" in pair else settings["games"]) for pair in pairs]
    ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)   # below normal
    os.makedirs(OUT, exist_ok=True)
    build()
    net = os.path.join(COPY, settings["net"] + ".nnue")
    shutil.copyfile(os.path.join(MAIN, "bot", "eval", settings["net"] + ".nnue"), net)
    with open(net, "rb") as f:
        net_hash = hashlib.sha256(f.read()).hexdigest()[:12]
    engine = os.path.join(COPY, "engine", "ChessBruteforcer.Engine.exe")
    match = os.path.join(COPY, "match", "ChessBruteforcer.Match.exe")

    for (name_a, tc_a), (name_b, tc_b), games in pairs:
        tag = f"{name_a}-{name_b}"
        concurrency = settings["concurrency" if tc_a.startswith("depth") else "clock-concurrency"]
        log, pgn = os.path.join(OUT, tag + ".log"), os.path.join(OUT, tag + ".pgn")
        if os.path.exists(pgn):
            print(f"{tag}: {pgn} is there already, skipped", flush=True)
            continue
        while other_engines() or free_commit_gb() < 2:
            print(f"{datetime.datetime.now():%H:%M:%S} {tag}: waiting ({len(other_engines())} other engines, "
                  f"{free_commit_gb():.1f} GB commit free)", flush=True)
            time.sleep(60)
        with open(log, "a", encoding="utf-8") as out:
            out.write(f"{datetime.datetime.now():%Y-%m-%d %H:%M:%S}: {name_a} ({tc_a}) v {name_b} ({tc_b}), "
                      f"{games} games, {concurrency} at a time, net {settings['net']} "
                      f"(sha256 {net_hash}), commit {git('rev-parse', '--short', 'HEAD')}, from {REPO}\n")
            out.flush()
            print(f"{datetime.datetime.now():%H:%M:%S} {tag}: started (progress in {log})", flush=True)
            sides = []
            for name, tc in ((name_a, tc_a), (name_b, tc_b)):
                sides += ["--engine", f"name={name}", f"cmd={engine}", f"option.EvalFile={net}",
                          "option.Threads=1", "option.Hash=64", f"tc={tc}"]
            code = subprocess.run([match, *sides, "--games", str(games), "--concurrency", str(concurrency),
                                   "--tables", os.path.join(MAIN, "tables"),
                                   "--openings", os.path.join(MAIN, "games", "tune", "openings-300.txt"),
                                   "--pgn", pgn], stdout=out, stderr=subprocess.STDOUT).returncode
            out.write(f"{datetime.datetime.now():%Y-%m-%d %H:%M:%S}: finished, exit {code}\n")
        print(f"{datetime.datetime.now():%H:%M:%S} {tag}: finished, exit {code}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
