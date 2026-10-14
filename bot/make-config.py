"""Write lichess-bot's config.yml for BruteForceBot: lichess-bot's defaults with our settings on top.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe bot\\make-config.py

Our settings live here, in git; config.yml itself is out of git, because it holds the
Lichess token. A token already in config.yml is kept, so this can be run again after
changing a setting. Otherwise the token line reads PASTE-YOUR-TOKEN-HERE: paste the
token there yourself (Notepad), between the quotes.
"""
import os
import yaml

here = os.path.dirname(os.path.abspath(__file__))
repo = os.path.dirname(here)
bot = os.path.join(here, "lichess-bot")
target = os.path.join(bot, "config.yml")

with open(os.path.join(bot, "config.yml.default"), encoding="utf-8") as f:
    config = yaml.safe_load(f)
token = "PASTE-YOUR-TOKEN-HERE"
if os.path.exists(target):
    with open(target, encoding="utf-8") as f:
        token = (yaml.safe_load(f) or {}).get("token") or token

config["token"] = token
engine = config["engine"]
engine["dir"] = os.path.join(bot, "engines", "bruteforcer")   # a copy of the engine build (see start-bot.cmd)
engine["name"] = "ChessBruteforcer.Engine.exe"
engine["working_dir"] = engine["dir"]
engine["ponder"] = False                                      # our engine doesn't ponder
engine["uci_options"] = {
    "EndgameTables": os.path.join(repo, "tables"),            # every table of 5 pieces or fewer
    "Hash": 256,
    "Move Overhead": 100,
}
# The evaluation's numbers, Texel-tuned towards Stockfish's verdicts on Lichess's analysed games (2026-10-14:
# +208 Elo [+130, +311] over the built-in ones, 69 games at 10+0.1, SPRT decided). Delete the file to go back.
evaluation = os.path.join(here, "eval", "eval-sf1.txt")
if os.path.exists(evaluation):
    engine["uci_options"]["EvalFile"] = evaluation
engine["online_moves"]["online_egtb"]["enabled"] = False      # our own tables, not Lichess's
# The opening book from our own games (scripts/make-book.py, scripts/month-book.cmd): what 2200+ players
# chose, from every month counted, weighted by how often and how well; played instantly for the first 12
# moves while the book has the position. (lichess-2026-09-2200.bin: September alone, the first build.)
# lichess-bot tries the books in order: the wide one (moves played 3+ times, not 10+) answers only where the
# main one can't, which is mostly a bot's rare move; it would have answered 33 of the first 74 games' exits.
book = os.path.join(here, "books", "lichess-2200.bin")
if not os.path.exists(book):
    book = os.path.join(here, "books", "lichess-2026-09-2200.bin")
# Between them, our own book (bot/learn.py: moves the learner worked out after the bot's games, where the main
# book had nothing or the wide book's move didn't stand up). It always exists, with one inert entry when the
# learner has nothing yet, because lichess-bot can't open an empty book; the learner rebuilds it live.
ours = os.path.join(here, "books", "ours.bin")
if not os.path.exists(ours):
    os.makedirs(os.path.dirname(ours), exist_ok=True)
    with open(ours, "wb") as f:
        f.write((0).to_bytes(8, "big") + (0).to_bytes(2, "big") + (1).to_bytes(2, "big") + (0).to_bytes(4, "big"))   # key 0, move 0, weight 1
books = [b for b in (book, ours, os.path.join(here, "books", "lichess-2200-wide.bin")) if os.path.exists(b)]
engine["polyglot"] = {
    "enabled": bool(books),
    "book": {"standard": books},
    "min_weight": 1,
    "selection": "weighted_random",
    "max_depth": 12,                                          # moves (lichess-bot doubles it into plies)
    "normalization": "none",
}
for kind in ("syzygy", "gaviota"):
    engine["lichess_bot_tbs"][kind]["enabled"] = False
engine["draw_or_resign"]["resign_enabled"] = False            # play every game out (for now)

challenge = config["challenge"]
challenge["concurrency"] = 1                                  # one game at a time on the laptop
challenge["modes"] = ["casual", "rated"]                      # rated from 2026-10-13 (six clean casual games first)
challenge["time_controls"] = ["bullet", "blitz", "rapid", "classical"]

config["greeting"] = {
    "hello": "Hi! I'm {me}: a brute-force engine that plays every position with 5 pieces or fewer perfectly. "
             "Good luck! (!help)",
    "goodbye": "Good game!",
    "hello_spectators": "Hi! I'm {me}. Once 5 pieces or fewer are left, every move I make comes from solved tables. "
                        "!info says how close we are.",
    "goodbye_spectators": "Thanks for watching!",
}
matchmaking = config["matchmaking"]                           # it challenges other bots when idle (2026-10-13)
matchmaking["allow_matchmaking"] = True
matchmaking["challenge_timeout"] = 1                          # minutes idle before it sends a challenge
matchmaking["challenge_initial_time"] = [180, 300]            # blitz: 3+2 or 5+3, chosen at random
matchmaking["challenge_increment"] = [2, 3]
matchmaking["challenge_mode"] = "rated"                       # its challenges count for rating (some bots refuse casual)
matchmaking["challenge_filter"] = "coarse"                    # don't keep asking bots that said no
# Opponents within 300 of its own blitz rating. (Lichess starts a new bot at a provisional 3000, so for its
# first rated games this was a fixed 1800-2400 band instead; settled at 1974 after 61 games, 2026-10-13.)
matchmaking["opponent_rating_difference"] = 300
config["pgn_directory"] = os.path.join(here, "games")         # every game it plays, for `game takeover --moves`
config["pgn_file_grouping"] = "game"

with open(target, "w", encoding="utf-8", newline="\n") as f:
    f.write("# Written by bot/make-config.py: change settings there, not here (except the token).\n")
    yaml.safe_dump(config, f, sort_keys=False, allow_unicode=True)
print("wrote", target, "(token kept)" if token != "PASTE-YOUR-TOKEN-HERE" else "(paste the token into it)")
