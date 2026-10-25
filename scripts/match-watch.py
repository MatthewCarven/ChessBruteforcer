"""Watch the running part of a match played in parts, and run SPRT [0, 10] on all its parts together.

python scripts\\match-watch.py <part's .log> <net> <old> W L D [W L D ...] [--dry]

The earlier parts' wins, losses and draws (for <net>, as scripts\\match-parts.py takes them) are added to the log's
running "<net> vs <old>: +W -L =D" tally, every 15 s. When the LLR over all of them passes either bound (+-2.94), it
stops the match: only the dotnet processes run from %TEMP%\\cb-match, where scripts\\nnue-match.cmd copies the engine
and the match tool (so one such match at a time). Otherwise it waits for the match's own end, up to 5 h. --dry prints
the combined tally and LLR once and stops nothing.
"""
import math
import re
import subprocess
import sys
import time

args = [a for a in sys.argv[1:] if a != "--dry"]
DRY = "--dry" in sys.argv
LOG, NET, OLD = args[:3]
v = list(map(int, args[3:]))
EARLIER = (sum(v[0::3]), sum(v[1::3]), sum(v[2::3]))
TALLY = re.compile(re.escape(f"{NET} vs {OLD}: ") + r"\+(\d+) -(\d+) =(\d+)")
BOUND = math.log(0.95 / 0.05)
STOP = ("Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' -and "
        "$_.CommandLine -match '\\\\cb-match\\\\' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }")


def score(e):
    return 1 / (1 + 10 ** (-e / 400))


def llr(w, l, d):
    n = w + l + d
    s = (w + d / 2) / n
    var = (w * (1 - s) ** 2 + d * (0.5 - s) ** 2 + l * s ** 2) / n
    s0, s1 = score(0), score(10)
    return n * (s1 - s0) * (2 * s - s0 - s1) / (2 * var)


deadline = time.time() + 5 * 3600
w, l, d = EARLIER
x = llr(w, l, d) if w + l + d else 0.0
while time.time() < deadline:
    text = open(LOG, encoding="utf-8", errors="replace").read()
    tallies = TALLY.findall(text)
    if tallies:
        w, l, d = (a + int(b) for a, b in zip(EARLIER, tallies[-1]))
        x = llr(w, l, d)
    if DRY:
        print(f"combined +{w} -{l} ={d}, LLR {x:.2f}; this part {tallies[-1] if tallies else 'no games yet'}")
        break
    if tallies and abs(x) >= BOUND:
        subprocess.run(["powershell.exe", "-NoProfile", "-Command", STOP])
        print(f"{time.strftime('%H:%M:%S')} combined SPRT decided: +{w} -{l} ={d}, LLR {x:.2f}; match stopped")
        print(f"this part so far: {tallies[-1]}")
        break
    if "finished, exit" in text:
        print(f"{time.strftime('%H:%M:%S')} match finished by itself; combined +{w} -{l} ={d}, LLR {x:.2f}")
        break
    time.sleep(15)
else:
    print("still running after 5 h")
