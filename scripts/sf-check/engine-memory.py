r"""What one engine commits with the tables and without (MATCHES.md, 2026-10-25: 1,150 MB and 343 MB at Hash 256;
~1,680 MB once the tables' block cache fills). The bot's engine and net, a 1 s search in a 5-piece ending so the
probe cache gets used, then its private bytes (commit) and working set. For TODO's tables-copy item: run it again
after a change to how the tables load. Starts two engines, one after the other; from anywhere:
  bot\lichess-bot\.venv\Scripts\python.exe -I scripts\sf-check\engine-memory.py
"""
import os, subprocess
import chess, chess.engine

here = os.path.dirname(os.path.abspath(__file__))
git_dir = subprocess.run(["git", "-C", here, "rev-parse", "--path-format=absolute", "--git-common-dir"],
                         capture_output=True, text=True, check=True).stdout.strip()
main = os.path.dirname(os.path.normpath(git_dir))
exe = os.path.join(main, "bot", "lichess-bot", "engines", "bruteforcer", "ChessBruteforcer.Engine.exe")
for tables in (os.path.join(main, "tables"), None):
    engine = chess.engine.SimpleEngine.popen_uci(exe)
    opts = {"Hash": 256, "Threads": 1, "EvalFile": os.path.join(main, "bot", "eval", "h1536-3m-ob8.nnue")}
    if tables:
        opts["EndgameTables"] = tables
    engine.configure(opts)
    engine.ping()
    engine.play(chess.Board("8/8/4k3/8/2R5/8/3PK3/6r1 w - - 0 1"), chess.engine.Limit(time=1.0))
    pid = engine.transport.get_pid()
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          f"$p = Get-Process -Id {pid}; '{{0:N0}} MB private (commit), {{1:N0}} MB working set'"
                          f" -f ($p.PrivateMemorySize64 / 1MB), ($p.WorkingSet64 / 1MB)"],
                         capture_output=True, text=True).stdout.strip()
    print("tables" if tables else "no tables", ":", out)
    engine.quit()
