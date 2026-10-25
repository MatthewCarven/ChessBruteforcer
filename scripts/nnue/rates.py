"""The trainer's speed between its progress lines: positions a second over each interval, with the interval's end time.

python scripts\\nnue\\rates.py <run .log> [from HH:MM:SS] [to HH:MM:SS]

For a run that seems slow, or to time one machine against another (the progress lines are train.py's
"HH:MM:SS pass P bucket B/T: N positions").
"""
import re
import statistics
import sys

path = sys.argv[1]
lo = sys.argv[2] if len(sys.argv) > 2 else "00:00:00"
hi = sys.argv[3] if len(sys.argv) > 3 else "99:99:99"
pat = re.compile(r"^(\d\d):(\d\d):(\d\d)\s+pass (\d+) bucket (\d+)/(\d+): ([\d,]+) positions")
prev = None
rates = []
for line in open(path, encoding="utf-8", errors="replace"):
    m = pat.match(line)
    if not m:
        continue
    h, mi, s, p, b, total, n = m.groups()
    t = int(h) * 3600 + int(mi) * 60 + int(s)
    n = int(n.replace(",", ""))
    stamp = f"{h}:{mi}:{s}"
    if prev and prev[0] == p and lo <= stamp <= hi:
        dt = t - prev[1]
        rate = (n - prev[2]) / dt if dt > 0 else 0
        rates.append(rate)
        print(f"{stamp}  pass {p} bucket {b}/{total}  {rate:,.0f} a second over {dt} s")
    prev = (p, t, n)
if rates:
    print(f"n {len(rates)}  median {statistics.median(rates):,.0f}  min {min(rates):,.0f}  max {max(rates):,.0f}")
