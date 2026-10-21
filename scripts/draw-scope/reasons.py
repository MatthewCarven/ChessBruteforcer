"""How the drawn games ended (repetition, 50-move rule, insufficient material, agreement), from the bot logs,
for all draws and for the level-stretch games, by era.

    python -I reasons.py data.json <bot folder>
"""
import collections
import glob
import importlib.util
import os
import re
import sys

spec = importlib.util.spec_from_file_location("an", __file__.replace("reasons.py", "analyze.py"))
an = importlib.util.module_from_spec(spec)
spec.loader.exec_module(an)

bot = sys.argv[2]
reason = {}
pending = None
for path in sorted(glob.glob(os.path.join(bot, "logs", "bot-*.log"))):
    for line in open(path, "rb").read().decode("utf-8", "replace").splitlines():
        if "token" in line.lower() or "authoriz" in line.lower():
            continue
        m = re.search(r"INFO Game drawn (by|from) (.*)\.$", line)
        if m:
            pending = m.group(2)
            continue
        m = re.search(r"--- https://lichess\.org/(\w{8})/\w+ Game over", line)
        if m:
            if pending:
                reason[m.group(1)] = pending
            pending = None

era = an.era
for e_ in ("early", "networks"):
    draws = [g for g in an.done if an.S(g) == 0.5 and era(g) == e_]
    lvl = [g for g in draws if an.stretch(g, 10)]
    print(e_, "all draws", len(draws), dict(collections.Counter(reason.get(g["id"], "?") for g in draws)))
    print(e_, "draws with a 10-move level stretch", len(lvl), dict(collections.Counter(reason.get(g["id"], "?") for g in lvl)))
