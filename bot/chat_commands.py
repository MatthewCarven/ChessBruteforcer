"""Our own chat commands for the bot (Matthew, 2026-10-14): !info, !stats, and !help listing them.

lichess-bot's commands are written into its lib/conversation.py, with no way to add one, so start-bot.cmd
copies this file into lichess-bot as lib/bruteforcer_chat.py and applies bot/lichess-bot-chat.patch, which
makes Conversation.command ask reply() first; whatever this doesn't answer goes on to lichess-bot's own.

!info: how many tables we have, whether the game is in them now, and where the opening book ran out. In
the spectators' room (or from the bot's own account) it adds what the tables say about the game; the
opponent isn't told that, just as lichess-bot's !eval isn't for the opponent (Matthew's pick). Then two
lines about the project (ABOUT, 2026-10-16: the greeting has room for little more than a pointer here).
Lichess chat messages are 140 characters at most: lichess-bot drops a longer one unsent. Kept to a few
messages a command, as a burst could trip Lichess's chat flood filter.

!stats (Matthew, 2026-10-25: "a figure on the total computed positions would be nice", not long, and it "does not
need be 100% acurate"): roughly how many positions our engine has searched this game, in one message. The same goes
into the goodbyes at the game's end, in the same message as "Good game!" (outgoing(), which lichess-bot's
Conversation.send_message asks first: bot/lichess-bot-goodbye-stats.patch), so the end sends no more than before.
"""
import os
import chess
import chess.engine

TABLE_PIECES = 5   # every ending with this many pieces or fewer is solved
BOOK_SOURCE = "lichess-bot-source:Opening Book"
CHAT_LIMIT = 140   # characters in a Lichess chat message
HELP = ("Commands: !info (about me, and my endgame tables now), !stats, !name, !queue, "
        "!eval (spectators), !wait (a minute for my first move)")
ABOUT = [   # each 140 characters at most; the figures are from WORKLOG (September 2026's games, the bot's config)
    "About me: a thesis project on how small real chess is, mapping games back from the end. "
    "Every ending of 5 pieces or fewer solved here.",
    "Lichess games vs my tables: ~1 in 4 that reach 5 pieces end wrongly. "
    "I play on 8 threads, Texel-tuned; a neural evaluation is next.",
]

_tables = None


def tables_folder():
    """The tables the engine plays from: BRUTEFORCER_TABLES (start-bot.cmd sets it), or the repo's tables
    folder (this file sits in bot/lichess-bot/lib once copied)."""
    here = os.path.dirname(os.path.abspath(__file__))
    return os.environ.get("BRUTEFORCER_TABLES") or os.path.join(here, "..", "..", "..", "tables")


def table_count():
    """Endings with a table (a .cbt or .cbz), counted once."""
    global _tables
    if _tables is None:
        try:
            _tables = len({os.path.splitext(name)[0] for name in os.listdir(tables_folder())
                           if name.endswith((".cbt", ".cbz"))})
        except OSError:
            _tables = 0
    return _tables


def replay(game):
    """The game's positions, from the start to now."""
    fen = game.initial_fen
    board = chess.Board() if fen in (None, "", "startpos") else chess.Board(fen)
    boards = [board.copy()]
    for uci in game.state.get("moves", "").split():
        board.push_uci(uci)
        boards.append(board.copy())
    return boards


def book_note(engine):
    """Where the book ran out, from the source of each of our moves (lichess-bot keeps one entry per move
    it played in this game, from move comment_start_index of the game on)."""
    comments, start = engine.move_commentary, engine.comment_start_index
    if not comments or start > 1:   # nothing played yet, or the bot restarted mid-game and doesn't know
        return ""
    for i, info in enumerate(comments):
        if not str((info or {}).get("string", "")).startswith(BOOK_SOURCE):
            return "Book: none this game." if i == 0 else f"Book: out since my move {(start + 2 * i) // 2 + 1}."
    return f"Book: still in it ({len(comments)} moves)."


def verdict(engine, boards):
    """What the tables said at our last move, if we were in them then, from our side. In the tables our
    engine reports a mate distance or 0 (Search.TablebaseMove), by the 50-move rule at the game's clock."""
    comments, start = engine.move_commentary, engine.comment_start_index
    if not comments or start < 0:
        return ""
    at = start + 2 * (len(comments) - 1)   # the position our last move was played from
    if at >= len(boards) or len(boards[at].piece_map()) > TABLE_PIECES:
        return "The tables have no say yet: ask again after my next move."
    score = (comments[-1] or {}).get("score")
    if not isinstance(score, chess.engine.PovScore):
        return ""
    ours = score.relative
    if ours.mate() is not None:
        n = ours.mate()
        return f"The tables say: I win, mate in {n}." if n > 0 else f"The tables say: I lose, mated in {-n}."
    if ours.score() == 0:   # a draw, or a win the 50-move rule would run out on (the engine scores it 0)
        return "The tables say: a draw."
    return ""


def amount(number):
    """A count in words, rounded: 7.0 billion, 11 billion, 850 million, 12 thousand."""
    for size, word in ((1e9, "billion"), (1e6, "million"), (1e3, "thousand")):
        if number >= size * 0.9995:   # (so 999.8 million reads 1.0 billion, not 1000 million)
            value = number / size
            return f"{value:.1f} {word}" if size == 1e9 and value < 9.95 else f"{value:.0f} {word}"
    return str(int(number))


def searched(engine):
    """The positions our engine searched for its moves this game, and the seconds that took: the sum of what it
    reported for each move lichess-bot kept (engine.move_commentary: nodes, and time or nps). A ponder hit's search
    began in the opponent's time, and its count runs from then. A ponder miss's search is stopped and thrown away by
    python-chess unreported, so it isn't in: with the tables' moves, that leaves about three quarters of the engine's
    thinking time from its first search to its last counted (the bot's logs of 2026-10-22 to 24, 34 games one at a
    time: 73% the median, 66-80% the middle half; 0.9 to 11 billion positions a game, 6.8 billion the median). Book
    moves report nothing."""
    nodes, seconds = 0, 0.0
    for info in engine.move_commentary:
        count = (info or {}).get("nodes")
        if not isinstance(count, int) or count <= 0:
            continue
        nodes += count
        if info.get("time"):
            seconds += info["time"]
        elif info.get("nps"):
            seconds += count / info["nps"]
    return nodes, seconds


def stats_sentence(engine, so_far):
    """What we searched this game in a sentence, or "" before the first search. lichess-bot keeps only the moves it
    played since it last started, so after a restart mid-game (as book_note tells it) the count says so."""
    nodes, seconds = searched(engine)
    if not nodes:
        return ""
    since = "this game" if engine.comment_start_index <= 1 else "since my restart"
    who = "So far I've" if so_far else "I"
    text = f"{who} searched ~{amount(nodes)} positions {since}"
    if seconds > 0 and nodes / seconds >= 1e6:
        text += f", ~{amount(nodes / seconds)} a second"
    return text + "."


def game_on(conversation):
    """Whether the game is still being played (lichess-bot's own test, is_game_over, the other way round)."""
    return conversation.game.state.get("status", "started") == "started"


def outgoing(conversation, room, message):
    """A message lichess-bot sends unasked, as it would send it (its Conversation.send_message asks this first:
    bot/lichess-bot-goodbye-stats.patch). With the game over it's a goodbye, and it gets what we searched added when
    the two fit in one message; Matthew's quit.soon goodbye (113 characters) doesn't, and goes as it was. The hellos
    come before the game's over, and are left alone."""
    if not message or game_on(conversation):
        return message
    said = stats_sentence(conversation.engine, so_far=False)
    joined = f"{message} {said}"
    return joined if said and len(joined) <= CHAT_LIMIT else message


def reply(conversation, line, cmd):
    """Our answer to a command (a list of chat messages), or None to let lichess-bot answer it."""
    if cmd in ("commands", "help"):
        return [HELP]
    if cmd == "stats":   # for anyone: a count gives nothing away about the game
        said = stats_sentence(conversation.engine, so_far=game_on(conversation))
        return [said or "I haven't searched any positions yet this game."]
    if cmd != "info":
        return None
    boards = replay(conversation.game)
    pieces = len(boards[-1].piece_map())
    count = table_count()
    first = f"Tables: {count} endings, all with {TABLE_PIECES} pieces or fewer. "
    if pieces <= TABLE_PIECES:
        first += f"Now {pieces} pieces: every move I make comes from them."
    else:
        first += f"Now {pieces} pieces on the board: not yet."
    messages = [first]
    note = book_note(conversation.engine)
    if note:
        if len(first) + 1 + len(note) <= 140:
            messages[0] = first + " " + note
        else:
            messages.append(note)
    from_self = line.username == conversation.game.username
    if pieces <= TABLE_PIECES and (line.room == "spectator" or from_self):
        said = verdict(conversation.engine, boards)
        if said:
            messages.append(said)
    return messages + ABOUT
