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
engine["ponder"] = True                                       # think on the opponent's time (+83 Elo against itself
                                                              # at 10+0.1; on from 2026-10-16, WORKLOG "pondering")
engine["uci_options"] = {
    "EndgameTables": os.path.join(repo, "tables"),            # every table of 5 pieces or fewer
    "Hash": 256,
    "Threads": 4,                                             # LazySMP; 4 of the 8 cores for the heat (Matthew's (b),
                                                              # 2026-10-24: 8 took the CPU to 92 C), 8 before that
                                                              # (WORKLOG 2026-10-15 21:25, 2026-10-16)
    "Move Overhead": 100,
}
# The evaluation: the network h1536-3m-ob8 (Matthew's (b), 2026-10-24: h1536-3m with 8 output buckets, .nnue v3;
# +11 Elo [-3, +24] over h1536-3m after 1,200 games at 10+0.1), else
# h1536-3m (Matthew's 1(a), 2026-10-22: +7 Elo [-3, +18] over h1024-3m, 2,000 games at
# 10+0.1, LOS 91.3%, SPRT [0, 10] undecided at the limit; h1024-3m's recipe at 1536 hidden, held back -3.9%, 12.8%
# slower a node in bench 9; the bot thinks ~20x longer a move than 10+0.1, where speed costs less), else
# h1024-3m (NNUE.md; 2026-10-20: +52 Elo [+14, +91] over h512-3m after 188 games at
# 10+0.1, LOS 99.6%, which Matthew chose to play on before the SPRT finished; h512-3m's recipe at 1024 hidden, 15.8%
# slower a node), else h512-3m (2026-10-19: +11 Elo [-1, +22] over nnue1-h512, 2,000 games at 10+0.1, LOS 96.9%,
# SPRT [0, 10] undecided at the limit; nnue1-h512's recipe on July, August and September), else
# nnue1-h512 (2026-10-17: +76 Elo [+45, +107] over nnue1, 285 games at 10+0.1, after SPRT [0, 10] accepted H1;
# nnue1's recipe at 512 hidden, trained on the desktop's GTX 1660), else nnue1 (2026-10-17: +742 Elo [+645, +970]
# over sf1, 400 games at 10+0.1, after SPRT [0, 10] accepted H1), else sf1's numbers, Texel-tuned towards
# Stockfish's verdicts on Lichess's analysed games (2026-10-14: +208 Elo [+130, +311] over the built-in ones, 69
# games at 10+0.1, SPRT decided). Delete h1536-3m-ob8.nnue to go back to h1536-3m, h1536-3m.nnue as well for
# h1024-3m, h1024-3m.nnue as well for h512-3m,
# h512-3m.nnue as well for nnue1-h512,
# nnue1-h512.nnue as well for nnue1, nnue1.nnue as well for sf1, and eval-sf1.txt as well for the built-in numbers.
# (The deep nets lost to h1024-3m at 10+0.1, 2026-10-22: h1024-3m-l32 -85 Elo, h1024-3m-l16-32 about -55, both
# at about half its speed; MATCHES.md. The bot played h1024-3m-l16-32 for a game or two before that, then went back.)
for name in ("h1536-3m-ob8.nnue", "h1536-3m.nnue", "h1024-3m.nnue", "h512-3m.nnue", "nnue1-h512.nnue", "nnue1.nnue", "eval-sf1.txt"):
    evaluation = os.path.join(here, "eval", name)
    if os.path.exists(evaluation):
        engine["uci_options"]["EvalFile"] = evaluation
        break
engine["online_moves"]["online_egtb"]["enabled"] = False      # our own tables, not Lichess's
# The opening book from our own games (scripts/make-book.py, scripts/month-book.cmd): what 2200+ players
# chose, from every month counted, weighted by how often and how well; played instantly for the first 12
# moves while the book has the position. (lichess-2026-09-2200.bin: September alone, the first build.)
# In its place from 2026-10-25 (Matthew's (a)): lichess-2200-vetted-black.bin, the same book less the 57 Black moves
# BT1 (c) vetoed (scripts/book-tables/results/bt-c.md: no worse in a paired match, +32 Elo [+11, +54] where the books
# differ, under 1% of Black games; White's lines unchanged). A month rebuild renews lichess-2200.bin only, so the
# vetted copy lags it until vet-c-book.py is run again. Rename or delete it to go back to the human book.
# lichess-bot tries the books in order: the wide one (moves played 3+ times, not 10+) answers only where the
# main one can't, which is mostly a bot's rare move; it would have answered 33 of the first 74 games' exits.
book = os.path.join(here, "books", "lichess-2200-vetted-black.bin")
if not os.path.exists(book):
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
challenge["concurrency"] = 3                                  # up to three games at once, 4 threads each (Matthew,
                                                              # 2026-10-25: "accommodate anyone who asks for a game
                                                              # while we are busy"; one from 2026-10-22, two from
                                                              # 2026-10-18). Pondering keeps each engine busy, so three
                                                              # make 12 busy threads on 8 cores: more heat, each game
                                                              # slower. A challenge that comes with every slot taken
                                                              # is declined "later" at once (bot\lichess-bot-busy.patch),
                                                              # not queued to go stale
challenge["max_simultaneous_games_per_user"] = 1              # with several at once, against different people
challenge["modes"] = ["casual", "rated"]                      # rated from 2026-10-13 (six clean casual games first)
challenge["time_controls"] = ["bullet", "blitz", "rapid", "classical"]

config["greeting"] = {
    "hello": "Hi Bots! - I am {me}: a brute-force engine that plays every position with 5 pieces or fewer perfectly. "
             "Good luck! (!help).",                     # Matthew's wording, 2026-10-16: 131 with our name
    "goodbye": "Good game!",
    "hello_spectators": "Hi! I'm {me}: tables solve 5 pieces or fewer, an opening book learns from my games, and I think on "
                        "my opponent's time. !info",          # 133 with our name: Lichess's limit is 140, or it isn't sent
    "goodbye_spectators": "Thanks for watching!",
}
matchmaking = config["matchmaking"]                           # it challenges other bots when idle (2026-10-13)
matchmaking["allow_matchmaking"] = True
matchmaking["challenge_timeout"] = 1                          # minutes idle before it sends a challenge
matchmaking["challenge_initial_time"] = [180, 300]            # blitz: 3+2 or 5+3, chosen at random
matchmaking["challenge_increment"] = [2, 3]
matchmaking["challenge_mode"] = "rated"                       # its challenges count for rating (some bots refuse casual)
matchmaking["challenge_filter"] = "coarse"                    # don't keep asking bots that said no
# Opponents within 500 of its own blitz rating (Matthew, 2026-10-22: stronger bots are worth playing; 300 before).
# (Lichess starts a new bot at a provisional 3000, so for its first rated games this was a fixed 1800-2400 band
# instead; settled at 1974 after 61 games, 2026-10-13.)
matchmaking["opponent_rating_difference"] = 500
config["pgn_directory"] = os.path.join(here, "games")         # every game it plays, for `game takeover --moves`
config["pgn_file_grouping"] = "game"

with open(target, "w", encoding="utf-8", newline="\n") as f:
    f.write("# Written by bot/make-config.py: change settings there, not here (except the token).\n")
    yaml.safe_dump(config, f, sort_keys=False, allow_unicode=True)
print("wrote", target, "(token kept)" if token != "PASTE-YOUR-TOKEN-HERE" else "(paste the token into it)")
