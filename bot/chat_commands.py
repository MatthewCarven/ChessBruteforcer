"""Our own chat commands for the bot (Matthew, 2026-10-14): !info, and !help listing it.

lichess-bot's commands are written into its lib/conversation.py, with no way to add one, so start-bot.cmd
copies this file into lichess-bot as lib/bruteforcer_chat.py and applies bot/lichess-bot-chat.patch, which
makes Conversation.command ask reply() first; whatever this doesn't answer goes on to lichess-bot's own.

!info: how many tables we have, whether the game is in them now, and where the opening book ran out. In
the spectators' room (or from the bot's own account) it adds what the tables say about the game; the
opponent isn't told that, just as lichess-bot's !eval isn't for the opponent (Matthew's pick).
Lichess chat messages are 140 characters at most.
"""
import os
import chess
import chess.engine

TABLE_PIECES = 5   # every ending with this many pieces or fewer is solved
BOOK_SOURCE = "lichess-bot-source:Opening Book"
HELP = ("Commands: !info (my endgame tables, and whether we're in them), !name, !queue, "
        "!eval (spectators), !wait (a minute for my first move)")

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


def reply(conversation, line, cmd):
    """Our answer to a command (a list of chat messages), or None to let lichess-bot answer it."""
    if cmd in ("commands", "help"):
        return [HELP]
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
    return messages
