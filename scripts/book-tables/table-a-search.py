r"""BT1 (a)'s leaf searches (BOOK-TABLES.md): every leaf of the 5-ply table searched to a fixed depth by our engine,
on the desktop. Plain Python, no packages: FENs in, best move and score out.

    python -u table-a-search.py <engine exe> <net .nnue> <leaves .tsv> <out dir> <depth> <engines> [<limit>]

<leaves .tsv> is table-a.py's: one leaf a line, `<polyglot key hex>\t<fen>`, in a shuffled order, so the first leaves
done are a fair sample of the rest. Each engine is one process, one thread, Hash 64, no endgame tables, at below-normal
priority, and every search starts from nothing (ucinewgame clears the table and the history), so a leaf's answer is
the same whichever engine, machine or restart gives it. Engine n appends its answers to <out dir>\results-<n>.tsv:

    key  depth  move  score (cp 23 / mate -3)  nodes  ms

A restart reads every results file in <out dir> and skips the keys done at this depth, so the run can be stopped at
any time (with any number of engines) and started again. With <limit>, only the first <limit> leaves of the file (a
sizing sample; a later run into the same folder keeps them). Progress, every 5 minutes, to stdout: leaves done, the
rate over the last 30 minutes, the time left at that rate, the engines' nodes a second, and the CPU's % Processor
Performance (100 = its base clock; a drop with the engines all busy is the machine slowing itself down).
"""
import collections
import ctypes
import glob
import os
import queue
import subprocess
import sys
import threading
import time

BELOW_NORMAL = 0x4000
REPORT_S = 300


def say(text):
    print(f"{time.strftime('%Y-%m-%d %H:%M:%S')} {text}", flush=True)


class Engine:
    def __init__(self, exe, net):
        self.exe, self.net = exe, net
        self.start()

    def start(self):
        self.p = subprocess.Popen([self.exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                  text=True, bufsize=1, cwd=os.path.dirname(self.exe), creationflags=BELOW_NORMAL)
        self.q = queue.Queue()
        threading.Thread(target=self._read, daemon=True).start()
        self.send("uci")
        self.wait("uciok", 120)
        for name, value in (("Threads", 1), ("Hash", 64), ("EvalFile", self.net)):
            self.send(f"setoption name {name} value {value}")
        self.send("isready")
        self.wait("readyok", 120)

    def _read(self):
        for line in self.p.stdout:
            self.q.put(line.rstrip("\n"))
        self.q.put(None)

    def send(self, text):
        self.p.stdin.write(text + "\n")
        self.p.stdin.flush()

    def wait(self, prefix, timeout):
        lines, end = [], time.monotonic() + timeout
        while True:
            line = self.q.get(timeout=max(0.01, end - time.monotonic()))
            if line is None:
                raise RuntimeError("the engine exited")
            lines.append(line)
            if line.startswith(prefix):
                return lines

    def search(self, fen, depth):
        """(move, score, nodes, ms) from the last complete depth's info line and the bestmove. (A forced mate fully
        seen ends the search before <depth>: its line is the last.)"""
        self.send("ucinewgame")
        self.send(f"position fen {fen}")
        self.send("isready")
        self.wait("readyok", 120)
        self.send(f"go depth {depth}")
        lines = self.wait("bestmove", 7200)
        move = lines[-1].split()[1]
        score, nodes, ms = None, 0, 0
        for line in lines:
            w = line.split()
            if w[:1] == ["info"] and "score" in w and "lowerbound" not in w and "upperbound" not in w:
                i = w.index("score")
                score = f"{w[i + 1]} {w[i + 2]}"
                nodes = int(w[w.index("nodes") + 1])
                ms = int(w[w.index("time") + 1])
        if score is None or move == "0000":
            raise RuntimeError(f"no score or no move for {fen}: {lines[-3:]}")
        return move, score, nodes, ms

    def quit(self):
        try:
            self.send("quit")
            self.p.wait(timeout=10)
        except Exception:
            self.p.kill()


def performance():
    """The CPU's % Processor Performance (all processors), or None."""
    try:
        out = subprocess.run(["typeperf", "-sc", "1", r"\Processor Information(_Total)\% Processor Performance"],
                             capture_output=True, text=True, timeout=30).stdout
        rows = [line for line in out.splitlines() if line.startswith('"')]   # the header, then the sample
        return float(rows[1].split(",")[1].strip('"'))
    except Exception:
        return None


def done_keys(out, depth):
    done = set()
    for path in glob.glob(os.path.join(out, "results-*.tsv")):
        with open(path, encoding="utf-8") as f:
            for line in f:
                w = line.rstrip("\n").split("\t")
                if len(w) == 6 and w[1] == str(depth) and w[3].split()[0] in ("cp", "mate"):
                    done.add(w[0])
    return done


def main(argv):
    exe, net, leaves, out, depth, engines = argv[0], argv[1], argv[2], argv[3], int(argv[4]), int(argv[5])
    limit = int(argv[6]) if len(argv) > 6 else None
    kernel = ctypes.windll.kernel32
    kernel.GetCurrentProcess.restype = ctypes.c_void_p
    kernel.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    kernel.SetPriorityClass(kernel.GetCurrentProcess(), BELOW_NORMAL)
    os.makedirs(out, exist_ok=True)
    with open(leaves, encoding="utf-8") as f:
        work = [line.rstrip("\n").split("\t") for line in f if line.strip()]
    if limit:
        work = work[:limit]
    done = done_keys(out, depth)
    todo = [(k, fen) for k, fen in work if k not in done]
    say(f"start: depth {depth}, {engines} engines, {len(work):,} leaves, {len(work) - len(todo):,} done before, "
        f"{len(todo):,} to search; engine {exe}, net {net}")
    jobs = queue.Queue()
    for item in todo:
        jobs.put(item)
    lock = threading.Lock()
    finished = collections.deque()   # (time, nodes, ms) of each search done in this run
    failed = []

    def worker(n):
        try:
            engine = Engine(exe, net)
        except Exception as e:
            failed.append(f"engine {n} didn't start: {e}")
            return
        with open(os.path.join(out, f"results-{n}.tsv"), "a", encoding="utf-8", buffering=1) as results:
            while True:
                try:
                    key, fen = jobs.get_nowait()
                except queue.Empty:
                    break
                for attempt in (1, 2):
                    try:
                        move, score, nodes, ms = engine.search(fen, depth)
                        break
                    except Exception as e:
                        say(f"engine {n}: {e} ({fen}); {'restarting it' if attempt == 1 else 'leaving the leaf'}")
                        engine.quit()
                        engine.start()
                else:
                    failed.append(key)
                    continue
                results.write(f"{key}\t{depth}\t{move}\t{score}\t{nodes}\t{ms}\n")
                with lock:
                    finished.append((time.time(), nodes, ms))
        engine.quit()

    threads = [threading.Thread(target=worker, args=(n,), daemon=True) for n in range(engines)]
    start = time.time()
    for t in threads:
        t.start()
    last = start
    while any(t.is_alive() for t in threads):
        for t in threads:
            t.join(timeout=5)
        if time.time() - last >= REPORT_S or not any(t.is_alive() for t in threads):
            last = now = time.time()
            with lock:
                count = len(finished)
                recent = [r for r in finished if r[0] >= now - 1800]
            span = min(1800, now - start)
            rate = len(recent) / span * 3600 if span > 0 else 0
            left = len(todo) - count
            knps = sum(r[1] for r in recent) / max(1, sum(r[2] for r in recent))
            perf = performance()
            say(f"{len(work) - len(todo) + count:,} of {len(work):,} done ({(len(work) - len(todo) + count) / len(work):.2%}); "
                f"{rate:,.0f} leaves/h over the last {span / 60:.0f} min, {left / rate if rate else 0:,.1f} h left at that rate; "
                f"{knps:,.0f} knps an engine, {sum(r[2] for r in recent) / max(1, len(recent)):,.0f} ms a leaf; "
                f"CPU at {'?' if perf is None else f'{perf:.0f}'}% of base")
    say(f"finished: {len(finished):,} searched in {(time.time() - start) / 3600:.2f} h"
        + (f"; {len(failed)} failed: {failed[:5]}" if failed else ""))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
