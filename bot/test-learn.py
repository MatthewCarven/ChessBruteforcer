"""A test for bot/learn.py's evaluation: the learner thinks with the evaluation the bot plays with (2026-10-22, Matthew's
O3 (b)), the first of make-config.py's list present in bot/eval, read from make-config.py's text without running it.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe bot\\test-learn.py

For each set of files in eval\\, on a copy in a temporary folder: make-config.py is run there for real (it writes the
copy's config.yml, with the placeholder token), and the EvalFile it chose must be the file learn.bot_evaluation()
picks. Then the cases make-config.py can't show: no file of the list there, a make-config.py without the list, one that
doesn't parse, none at all (each: None, so the learner falls back to sf1's numbers, then the built-in ones). No engine
runs. config.yml.default comes from bot\\lichess-bot, or the folder in LICHESS_BOT; the real config.yml is never read.
"""
import importlib.util
import os
import shutil
import subprocess
import sys
import tempfile

import yaml

HERE = os.path.dirname(os.path.abspath(__file__))
BOT = os.environ.get("LICHESS_BOT") or os.path.join(HERE, "lichess-bot")

spec = importlib.util.spec_from_file_location("learn", os.path.join(HERE, "learn.py"))
learn = importlib.util.module_from_spec(spec)
spec.loader.exec_module(learn)

LIST = ("h1024-3m.nnue", "h512-3m.nnue", "nnue1-h512.nnue", "nnue1.nnue", "eval-sf1.txt")
failures = []


def check(name, got, want):
    ok = got == want
    print(f"{'ok  ' if ok else 'FAIL'} {name}: {got!r}" + ("" if ok else f", wanted {want!r}"))
    if not ok:
        failures.append(name)


def folder(root, present, source=None):
    """A bot folder: make-config.py (or `source` in its place, or none), eval\\ with `present`, lichess-bot's default
    config."""
    bot = os.path.join(root, "bot")
    os.makedirs(os.path.join(bot, "eval"))
    os.makedirs(os.path.join(bot, "lichess-bot"))
    shutil.copy(os.path.join(BOT, "config.yml.default"), os.path.join(bot, "lichess-bot"))
    if source is None:
        shutil.copy(os.path.join(HERE, "make-config.py"), bot)
    elif source:
        with open(os.path.join(bot, "make-config.py"), "w", encoding="utf-8") as f:
            f.write(source)
    for name in present:
        with open(os.path.join(bot, "eval", name), "wb") as f:
            f.write(b"x")
    return bot


def bot_choice(bot):
    """What make-config.py itself chooses: run it on the copy and read the copy's config.yml."""
    subprocess.run([sys.executable, os.path.join(bot, "make-config.py")], check=True, capture_output=True)
    with open(os.path.join(bot, "lichess-bot", "config.yml"), encoding="utf-8") as f:
        evaluation = yaml.safe_load(f)["engine"]["uci_options"].get("EvalFile")
    return os.path.normcase(evaluation) if evaluation else None


def learner_choice(bot):
    evaluation = learn.bot_evaluation(bot)
    return os.path.normcase(evaluation) if evaluation else None


# The bot and the learner agree, whichever files are there (each list's first, a gap in the middle, the last alone).
for present in (LIST, LIST[1:], ("nnue1-h512.nnue", "eval-sf1.txt"), ("h1024-3m.nnue", "eval-sf1.txt"),
                ("eval-sf1.txt",), ()):
    with tempfile.TemporaryDirectory() as root:
        bot = folder(root, present)
        want = bot_choice(bot)
        check(f"present {', '.join(present) or 'nothing'}: the bot's {os.path.basename(want or 'None')}",
              learner_choice(bot), want)

# The learner's own fallbacks: None from bot_evaluation in each case.
for name, source in (("no list in make-config.py", "for kind in ('syzygy', 'gaviota'):\n    pass\n"),
                     ("make-config.py doesn't parse", "for name in (\n"),
                     ("no make-config.py", "")):
    with tempfile.TemporaryDirectory() as root:
        check(name, learner_choice(folder(root, LIST, source)), None)

# And this tree: the learner's EVAL is the bot's first evaluation present in bot\eval.
check("this tree's EVAL", os.path.normcase(learn.EVAL or ""),
      os.path.normcase(next((os.path.join(HERE, "eval", n) for n in LIST if os.path.exists(os.path.join(HERE, "eval", n))), "")))
print(f"this tree's learner thinks with {learn.EVAL_NAME}")

print("\nall passed" if not failures else f"\n{len(failures)} FAILED: {', '.join(failures)}")
sys.exit(1 if failures else 0)
