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
engine["online_moves"]["online_egtb"]["enabled"] = False      # our own tables, not Lichess's
# The opening book from our own games (scripts/make-book.py, scripts/month-book.cmd): what 2200+ players
# chose, from every month counted, weighted by how often and how well; played instantly for the first 12
# moves while the book has the position. (lichess-2026-09-2200.bin: September alone, the first build.)
book = os.path.join(here, "books", "lichess-2200.bin")
if not os.path.exists(book):
    book = os.path.join(here, "books", "lichess-2026-09-2200.bin")
engine["polyglot"] = {
    "enabled": os.path.exists(book),
    "book": {"standard": [book]},
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
    "hello": "Hi! I'm {me}: a brute-force engine that plays every position with 5 pieces or fewer perfectly. Good luck!",
    "goodbye": "Good game!",
    "hello_spectators": "Hi! I'm {me}. Once 5 pieces or fewer are left, every move I make comes from solved tables.",
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
