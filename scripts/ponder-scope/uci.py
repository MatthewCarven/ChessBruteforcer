"""A raw UCI driver: one engine process, lines timestamped as they arrive."""
import queue
import re
import subprocess
import threading
import time

# (" lowerbound": since P4, a depth the clock cut, sent because it proved the move played better)
INFO = re.compile(r"^info depth (\d+) score (cp -?\d+|mate -?\d+)( lowerbound)? nodes (\d+) nps \d+ time (\d+) hashfull (\d+) pv (.*)$")


class Engine:
    def __init__(self, exe, options):
        self.p = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                  text=True, bufsize=1, cwd=exe.rsplit("\\", 1)[0])
        self.q = queue.Queue()
        threading.Thread(target=self._read, daemon=True).start()
        self.send("uci")
        self.wait("uciok")
        for name, value in options.items():
            self.send(f"setoption name {name} value {value}")
        self.ready()

    def _read(self):
        for line in self.p.stdout:
            self.q.put((time.perf_counter(), line.rstrip("\n")))
        self.q.put((time.perf_counter(), None))

    def send(self, text):
        self.p.stdin.write(text + "\n")
        self.p.stdin.flush()
        return time.perf_counter()

    def wait(self, prefix, timeout=600):
        """Lines up to and including the first that starts with prefix: [(t, line)]."""
        lines = []
        end = time.perf_counter() + timeout
        while True:
            t, line = self.q.get(timeout=max(0.01, end - time.perf_counter()))
            if line is None:
                raise RuntimeError("engine exited")
            lines.append((t, line))
            if line.startswith(prefix):
                return lines

    def ready(self):
        self.send("isready")
        return self.wait("readyok")

    def drain(self):
        lines = []
        while True:
            try:
                lines.append(self.q.get_nowait())
            except queue.Empty:
                return lines

    def quit(self):
        try:
            self.send("quit")
            self.p.wait(timeout=10)
        except Exception:
            self.p.kill()


def infos(lines):
    out = []
    for t, line in lines:
        m = INFO.match(line)
        if m:
            out.append({"t": t, "depth": int(m.group(1)), "score": m.group(2), "nodes": int(m.group(4)),
                        "ms": int(m.group(5)), "hashfull": int(m.group(6)), "pv": m.group(7).split(),
                        "partial": m.group(3) is not None})
    return out


def bestmove(lines):
    for t, line in lines:
        if line.startswith("bestmove"):
            w = line.split()
            return t, w[1], (w[3] if len(w) > 3 and w[2] == "ponder" else None)
    return None, None, None


def budget(remaining, inc, overhead):
    """Search.TimeBudget with movestogo 30: (soft, hard) ms."""
    remaining = max(1, remaining - overhead)
    soft = remaining // 30 + inc * 3 // 4
    hard = min(soft * 3, remaining // 3 + inc)
    hard = max(5, min(hard, remaining - 20))
    return min(soft, hard), hard
