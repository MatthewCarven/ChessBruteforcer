"""A test for bot/lichess-bot-not-answering.patch: while lichess.org isn't answering a request, lichess-bot says so in
its window and bot\\logs, and says when lichess.org answers again.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe bot\\test-not-answering.py

2026-10-25 09:18: lichess.org stalled after our move 60 (Rc1) in Jc2ds3P2 was sent. lichess-bot retried the move for
51 s and logged every try at DEBUG only, so the window showed nothing while our clock ran. The bot looked frozen, and a
Ctrl+C stopped it for good, though it would have carried on by itself. Matthew's (a): "print a line while lichess.org
isn't answering and when it's back". The patch says so at INFO once a request has waited 5 s, again every 10 s, and
when lichess.org answers or the retries give up.

The test uses lichess-bot's own Lichess class (lib/lichess.py, as start-bot.cmd patches it) and its retries (backoff).
Its requests session is swapped for a pretend lichess.org, and its clocks for a pretend one, so a minute of retries
takes no time. A try lichess.org doesn't answer takes 2.6 s, as at 09:18 (a new connection, then the 2 s read timeout);
one it can't connect to takes 2 s; an answer takes 0.6 s. Cases:
  1. the token check at start, stalled: the line names it, and the token is in no line
  2. one unanswered try, then the answer (a blip, as most days have): nothing at INFO
  3. 09:18 again, but lichess.org answers on the 15th try: a line at ~5 s, one every ~10 s, then "answered again"
  4. it never answers: the same, then "didn't answer" when the retries give up after a minute
  5. a stall that ends in an error answer (HTTP 400): "answered again ... with HTTP 400"
  6. an error answer at once (HTTP 400, no retries): nothing at INFO
  7. the event stream (a GET), lichess.org unreachable for 3 tries: "can't connect"
  8. two threads: a request on another thread starts in the middle of a stall and waits there; each thread's lines
     are its own
The lichess-bot tested is bot\\lichess-bot, or the folder in LICHESS_BOT (a copy, so a running bot is left alone).
"""
import datetime
import logging
import os
import random
import re
import sys
import threading
import types

HERE = os.path.dirname(os.path.abspath(__file__))
BOT = os.environ.get("LICHESS_BOT") or os.path.join(HERE, "lichess-bot")
os.chdir(BOT)
sys.path.insert(0, BOT)

import backoff._sync                                            # noqa: E402
import requests                                                 # noqa: E402
from requests.exceptions import ConnectionError as RequestsConnectionError, HTTPError, ReadTimeout  # noqa: E402
from lib import lichess, timer                                  # noqa: E402

TOKEN = "lip_pretendTokenNotARealOne0123"
GAME, MOVE = "Jc2ds3P2", "g1c1"
NO_REPLY, NO_CONNECTION = "no reply", "can't connect"


class Clock:
    """The pretend time. lichess-bot's Timers (lib/timer.py's perf_counter) and backoff's elapsed time and waits run on
    it."""

    def __init__(self) -> None:
        self.now = 1000.0

    def perf_counter(self) -> float:
        return self.now

    def sleep(self, seconds: float) -> None:
        self.now += seconds


clock = Clock()


class PretendDatetime(datetime.datetime):
    """datetime.datetime for backoff (its elapsed time and max_time), on the pretend clock."""

    @classmethod
    def now(cls, tz=None):  # noqa: ARG003
        return datetime.datetime(2026, 10, 25, 9, 0) + datetime.timedelta(seconds=clock.now)


class PretendResponse:
    """An answer from lichess.org."""

    def __init__(self, status: int, body: object) -> None:
        self.status_code = status
        self.body = body
        self.encoding = None
        self.text = ""

    def raise_for_status(self) -> None:
        if self.status_code >= 400:
            raise HTTPError(f"{self.status_code} Client Error", response=self)

    def json(self) -> object:
        return self.body


def endpoint_of(url: str) -> str:
    """The name in lib/lichess.py's ENDPOINTS of the url asked for."""
    path = url.split("lichess.org", 1)[-1].split("?")[0]
    for name, template in lichess.ENDPOINTS.items():
        if re.fullmatch(re.escape(template).replace(r"\{\}", "[^/]+"), path):
            return name
    return path


class PretendSession:
    """lichess.org, as lichess-bot's requests session sees it. steps[endpoint] says what each try at it gets, in turn:
    NO_REPLY, NO_CONNECTION, an HTTP status, or a function to call first (then the next step). When the steps run
    out, it answers."""

    def __init__(self) -> None:
        self.headers: dict[str, str] = {}
        self.steps: dict[str, list] = {}

    def get(self, url, params=None, timeout=None, stream=False):  # noqa: ARG002
        return self.request(url)

    def post(self, url, data=None, headers=None, params=None, json=None, timeout=None):  # noqa: ARG002
        return self.request(url)

    def request(self, url: str) -> PretendResponse:
        endpoint = endpoint_of(url)
        steps = self.steps.setdefault(endpoint, [])
        step = steps.pop(0) if steps else 200
        while callable(step):
            step()
            step = steps.pop(0) if steps else 200
        if step == NO_REPLY:
            clock.now += 2.6
            raise ReadTimeout("HTTPSConnectionPool(host='lichess.org', port=443): Read timed out. (read timeout=2)")
        if step == NO_CONNECTION:
            clock.now += 2.0
            raise RequestsConnectionError("HTTPSConnectionPool(host='lichess.org', port=443): Max retries exceeded")
        clock.now += 0.6
        body = {TOKEN: {"scopes": "bot:play"}} if endpoint == "token_test" else {"ok": True}
        return PretendResponse(step, body)


class Lines(logging.Handler):
    """lib.lichess's lines at INFO and above (the window's and bot\\logs'), each with the pretend time it came at."""

    def __init__(self) -> None:
        super().__init__(logging.INFO)
        self.lines: list[tuple[float, str]] = []

    def emit(self, record: logging.LogRecord) -> None:
        self.lines.append((clock.now, record.getMessage()))


def lines_wanted(request: str, reason: str, waiting: int, end: str) -> list[str]:
    """The lines a stall should give, as patterns: `waiting` lines while it waits, then the `end` one ("answered",
    "HTTP 400" or "gave up"); the seconds in each are a group."""
    r = re.escape(request)
    first = (rf"lichess\.org isn't answering \({r}: {reason}\)\. Still trying after (\d+) s, for up to a minute: leave "
             r"the bot running, it carries on by itself when lichess\.org is back \(Ctrl\+C stops it for good\)\.")
    again = rf"lichess\.org still isn't answering \({r}: {reason}\), (\d+) s so far\."
    ends = {"answered": rf"lichess\.org answered again \({r}\) after (\d+) s\.",
            "HTTP 400": rf"lichess\.org answered again \({r}\) after (\d+) s, with HTTP 400\.",
            "gave up": rf"lichess\.org didn't answer \({r}: {reason}\) in (\d+) s: lichess-bot gives that up and "
                       r"carries on\."}
    return ([first] + [again] * (waiting - 1) + [ends[end]]) if waiting else []


def problems_with(got: list[tuple[float, str]], wanted: list[str], took: float) -> list[str]:
    """What's wrong with the lines a call gave (pretend seconds after its start, text) against the patterns. The first
    waiting line comes 5-8 s in and each after it 10-13 s after the one before, the end line when the call ends, and
    every line's seconds match its time."""
    if len(got) != len(wanted):
        return [f"{len(got)} line(s), wanted {len(wanted)}"]
    problems = []
    for i, ((at, text), pattern) in enumerate(zip(got, wanted)):
        m = re.fullmatch(pattern, text)
        if not m:
            problems.append(f"line {i + 1} isn't as wanted: {text!r}")
            continue
        if abs(int(m.group(1)) - at) > 1:
            problems.append(f"line {i + 1} says {m.group(1)} s at {at:.1f} s")
        last = i == len(got) - 1
        if last and abs(at - took) > 0.01:
            problems.append(f"the end line came at {at:.1f} s, the call ended at {took:.1f} s")
        elif not last and i == 0 and not 5 <= at < 8:
            problems.append(f"the first line came at {at:.1f} s")
        elif not last and i > 0 and not 10 <= at - got[i - 1][0] < 13:
            problems.append(f"line {i + 1} came {at - got[i - 1][0]:.1f} s after the one before")
    return problems


def main() -> int:
    if not hasattr(lichess, "watch_unanswered"):
        print(f"FAILED: the patch isn't in {BOT}: start-bot.cmd applies bot\\lichess-bot-not-answering.patch")
        return 1
    timer.perf_counter = clock.perf_counter
    backoff._sync.datetime = types.SimpleNamespace(datetime=PretendDatetime)
    backoff._sync.time = types.SimpleNamespace(sleep=clock.sleep)
    random.seed(20261025)   # backoff's jitter on its 0.1 s waits
    session = PretendSession()
    requests.Session = lambda: session   # lib/lichess.py makes its sessions with requests.Session()
    lines = Lines()
    log = logging.getLogger("lib.lichess")
    log.setLevel(logging.INFO)
    log.addHandler(lines)
    failures = 0

    def case(name: str, endpoint: str, steps: list, call, wanted: list[str], raises: type | None = None) -> None:
        nonlocal failures
        session.steps[endpoint] = list(steps)
        first, start = len(lines.lines), clock.now
        try:
            call()
            raised = None
        except Exception as e:  # noqa: BLE001
            raised = e
        took = clock.now - start
        got = [(at - start, text) for at, text in lines.lines[first:]]
        problems = problems_with(got, wanted, took)
        if not (isinstance(raised, raises) if raises else raised is None):
            problems.append(f"raised {raised!r}, wanted {raises.__name__ if raises else 'nothing'}")
        failures += bool(problems)
        print(f"  {'ok  ' if not problems else 'FAIL'} {name}: {len(got)} line(s) in {took:.1f} s"
              + "".join(f"\n         {p}" for p in problems))
        for at, text in got:
            print(f"         {at:5.1f} s  {text}")

    print("lichess-bot's lines while lichess.org isn't answering (lib/lichess.py with a pretend lichess.org and clock):")
    made: dict[str, lichess.Lichess] = {}
    case("1. the token check at start, stalled", "token_test", [NO_REPLY] * 2,
         lambda: made.update(li=lichess.Lichess(TOKEN, "https://lichess.org/", "test", logging.INFO, 3)),
         lines_wanted("token_test", "no reply", 1, "answered"))
    if "li" not in made:
        print("FAILED: lichess-bot's Lichess couldn't be made")
        return 1
    li = made["li"]
    move = f"our move {MOVE} in game {GAME}"
    case("2. one unanswered try, then the answer", "move", [NO_REPLY], lambda: li.make_move(GAME, MoveLike(MOVE)), [])
    case("3. 09:18 again, answered on the 15th try", "move", [NO_REPLY] * 14,
         lambda: li.make_move(GAME, MoveLike(MOVE)), lines_wanted(move, "no reply", 4, "answered"))
    case("4. never answered", "move", [NO_REPLY] * 99, lambda: li.make_move(GAME, MoveLike(MOVE)),
         lines_wanted(move, "no reply", 6, "gave up"), ReadTimeout)
    case("5. a stall ending in HTTP 400", "move", [NO_REPLY] * 3 + [400], lambda: li.make_move(GAME, MoveLike(MOVE)),
         lines_wanted(move, "no reply", 1, "HTTP 400"), HTTPError)
    case("6. HTTP 400 at once", "move", [400], lambda: li.make_move(GAME, MoveLike(MOVE)), [], HTTPError)
    case("7. the event stream, unreachable", "stream_event", [NO_CONNECTION] * 3, li.get_event_stream,
         lines_wanted("stream_event", "can't connect", 1, "answered"))

    inside, release, other = threading.Event(), threading.Event(), {}

    def hold() -> None:
        inside.set()
        release.wait(10)

    def other_thread() -> None:
        thread = threading.Thread(target=lambda: other.update(answer=li.api_get_json("playing")), daemon=True)
        other["thread"] = thread
        thread.start()
        inside.wait(10)

    session.steps["playing"] = [hold]
    case("8. two threads, the other one's request waiting in the middle of a stall", "move",
         [NO_REPLY, other_thread] + [NO_REPLY] * 5, lambda: li.make_move(GAME, MoveLike(MOVE)),
         lines_wanted(move, "no reply", 2, "answered"))
    release.set()
    other["thread"].join(10)
    naming_it = [text for _, text in lines.lines if "playing" in text]
    if other.get("answer") != {"ok": True} or naming_it:
        failures += 1
        print(f"  FAIL the other thread: answer {other.get('answer')!r}, lines naming its request {naming_it!r}")

    if any(TOKEN in text for _, text in lines.lines):
        failures += 1
        print("  FAIL the token is in a line")
    print("passed" if not failures else f"FAILED: {failures} case(s)")
    return 1 if failures else 0


class MoveLike:
    """What lichess-bot's make_move takes (a PlayResult), as far as it looks: its move."""

    def __init__(self, uci: str) -> None:
        self.move = uci
        self.draw_offered = False
        self.resigned = False


if __name__ == "__main__":
    sys.exit(main())
