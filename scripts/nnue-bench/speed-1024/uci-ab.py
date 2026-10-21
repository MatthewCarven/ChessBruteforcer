"""8 threads the way the bot runs the engine (UCI: EvalFile, Threads, Hash), each arm in turn, one engine
at a time (memory), below-normal priority: nodes a second from the engine's own last info line of a
fixed-time search, on bench's six positions.

python uci-ab.py <net .nnue> <rounds> <movetime ms> <name>=<engine dll>[|ENV=VALUE] ...
"""
import os
import statistics
import subprocess
import sys
import time

BELOW_NORMAL = 0x4000
FENS = [
    "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
    "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
    "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
    "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
    "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
    "r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
]


def parse_arm(text):
    name, rest = text.split("=", 1)
    dll, *envs = rest.split("|")
    return name, dll, dict(e.split("=", 1) for e in envs)


class Engine:
    def __init__(self, dll, env_extra, net):
        env = os.environ.copy()
        env.update(env_extra)
        self.p = subprocess.Popen(["dotnet", dll], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True,
                                  encoding="utf-8", errors="replace", env=env, creationflags=BELOW_NORMAL)
        self.send("uci")
        self.wait("uciok")
        for option in (f"EvalFile value {net}", "Threads value 8", "Hash value 256"):
            self.send(f"setoption name {option}")
        self.send("isready")
        self.wait("readyok")

    def send(self, line):
        self.p.stdin.write(line + "\n")
        self.p.stdin.flush()

    def wait(self, token):
        lines = []
        while True:
            line = self.p.stdout.readline()
            if not line:
                raise RuntimeError(f"engine ended before {token}: {lines[-5:]}")
            lines.append(line.rstrip())
            if line.startswith(token):
                return lines

    def search(self, fen, movetime):
        self.send("ucinewgame")
        self.send(f"position fen {fen}")
        self.send("isready")
        self.wait("readyok")
        self.send(f"go movetime {movetime}")
        lines = self.wait("bestmove")
        info = [l for l in lines if l.startswith("info depth") and " nps " in l][-1].split()
        return int(info[info.index("depth") + 1]), int(info[info.index("nodes") + 1]), int(info[info.index("nps") + 1])

    def quit(self):
        self.send("quit")
        self.p.wait(timeout=30)


def main():
    net, rounds, movetime = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
    arms = [parse_arm(a) for a in sys.argv[4:]]
    nps = {name: [] for name, _, _ in arms}
    depths = {name: [] for name, _, _ in arms}
    for round_ in range(rounds + 1):   # round 0 warms up and is dropped
        for k in range(len(arms)):
            name, dll, env = arms[(k + round_) % len(arms)]
            engine = Engine(dll, env, net)
            totals = []
            for fen in FENS:
                depth, nodes, speed = engine.search(fen, movetime)
                totals.append(speed)
                if round_ > 0:
                    depths[name].append(depth)
            engine.quit()
            mean = statistics.mean(totals)
            if round_ > 0:
                nps[name].append(mean)
            print(f"{time.strftime('%H:%M:%S')}  round {round_}  {name:<10} {mean / 1000:8.0f} knps (mean of six)"
                  + ("  warm-up" if round_ == 0 else ""), flush=True)
    base = statistics.median(nps[arms[0][0]])
    print(f"\n8 threads, hash 256 MB, movetime {movetime} ms, {rounds} rounds; medians of each round's mean over six positions")
    for name, _, _ in arms:
        median = statistics.median(nps[name])
        print(f"{name:<10} {median / 1000:8.0f} knps  {100 * median / base - 100:+6.1f}% vs {arms[0][0]}   "
              f"mean depth {statistics.mean(depths[name]):.2f}   every round: {' '.join(f'{v / 1000:.0f}' for v in nps[name])}")


if __name__ == "__main__":
    main()
