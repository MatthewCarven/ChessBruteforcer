"""A test for bot/lichess-bot-sigint.patch: a Ctrl+C stops lichess-bot cleanly, even held down.

    bot\\lichess-bot\\.venv\\Scripts\\python.exe bot\\test-sigint.py

2026-10-20: 4 of the bot's 26 Ctrl+C stops (16 to 20 October) ended "Quitting lichess-bot due to an error: TypeError:
'NoneType' object cannot be interpreted as an integer" in accept_challenges, and read as crashes. lichess-bot's SIGINT
handler logged a line, and in the main process logging goes through a Manager queue on the same connection as the main
loop's own calls to the Manager. A held Ctrl+C (a SIGINT every 33 ms) stacked handlers on a log line that couldn't get
through while the main loop waited in control_queue.get(); at 82 deep the recursion limit broke them off with their
replies still to come, and the replies went to the wrong calls: challenge_queue's length came back None. The patched
handler only sets the stop flags.

Two checks, each run with lichess-bot's handler as patched, then as a control with its original one, which must show
the fault (or this test proves nothing):
  1. One SIGINT landing between a call to the Manager (challenge_queue's length, in accept_challenges) and its reply.
  2. A Ctrl+C held down while the main loop waits for an event: 200 SIGINTs 10 ms apart, the first keep-alive coming
     1.6 s after the first of them (the bot's come 6 s apart), then one every 0.2 s.
The SIGINTs are raised inside this process (signal.raise_signal), so no console event reaches anything else. The
lichess-bot tested is bot\\lichess-bot, or the folder in LICHESS_BOT (a copy, so a running bot is left alone).
"""
import io
import logging
import multiprocessing
import os
import signal
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
BOT = os.environ.get("LICHESS_BOT") or os.path.join(HERE, "lichess-bot")
os.chdir(BOT)
sys.path.insert(0, BOT)

from lib import lichess_bot  # noqa: E402

stop = lichess_bot.stop


def original_handler(signum, frame) -> None:  # noqa: ARG001
    """lichess-bot's signal_handler before the patch (2026.10.11.1): it logs first."""
    if not stop.terminated:
        lichess_bot.logger.debug("Received SIGINT. Terminating client.")
        stop.terminated = True
    else:
        lichess_bot.logger.debug("Received second SIGINT. Quitting now.")
        stop.force_quit = True


class Tap:
    """The main thread's connection to the Manager, with a SIGINT raised straight after one `__len__` request is sent."""

    def __init__(self, conn) -> None:
        self.conn = conn
        self.armed = True

    def send(self, obj) -> None:
        self.conn.send(obj)
        if self.armed and obj[1] == "__len__":
            self.armed = False
            signal.raise_signal(signal.SIGINT)  # its handler runs here, before the reply is read

    def __getattr__(self, name):
        return getattr(self.conn, name)


class Setup:
    """The bot's main process in small: a Manager with the challenge list and the two queues, the root logger on a
    QueueHandler (lichess-bot's thread_logging_configurer), the SIGINT handler under test, stderr captured."""

    def __init__(self, handler) -> None:
        self.manager = multiprocessing.Manager()
        self.challenge_queue = self.manager.list()
        self.control_queue = self.manager.Queue()
        self.logging_queue = self.manager.Queue()
        lichess_bot.thread_logging_configurer(self.logging_queue)
        stop.terminated = stop.force_quit = stop.restart = False
        signal.signal(signal.SIGINT, handler)
        self.stderr = io.StringIO()
        self.real_stderr, sys.stderr = sys.stderr, self.stderr

    def close(self) -> str:
        signal.signal(signal.SIGINT, signal.default_int_handler)
        sys.stderr = self.real_stderr
        logging.getLogger().handlers.clear()
        self.manager.shutdown()
        text = self.stderr.getvalue()
        errors = text.count("--- Logging error ---")
        return f"{errors} logging error(s){', RecursionError' if 'RecursionError' in text else ''}"


def one_sigint_between_call_and_reply(handler) -> tuple[str, bool]:
    """Check 1: accept_challenges on an empty queue, a SIGINT landing inside its `challenge_queue` call."""
    s = Setup(handler)
    try:
        len(s.challenge_queue)  # makes the main thread's connection
        tls = s.challenge_queue._tls
        tls.connection = Tap(tls.connection)
        try:
            lichess_bot.accept_challenges(None, s.challenge_queue, {}, 2, 2)
            got, clean = "returned", True
        except Exception as e:  # noqa: BLE001
            got, clean = f"{type(e).__name__}: {e}", False
        tls.connection = tls.connection.conn
    finally:
        log = s.close()
    return f"{got} ({log})", clean


def held_ctrl_c(handler) -> tuple[str, bool]:
    """Check 2: lichess_bot_main's loop, cut down to next_event and accept_challenges, under a held Ctrl+C."""
    s = Setup(handler)
    waiting, done = threading.Event(), threading.Event()
    first_sigint: list[float] = []

    def ctrl_c() -> None:
        waiting.wait()
        time.sleep(0.2)  # well inside the loop's wait for an event
        first_sigint.append(time.monotonic())
        for _ in range(200):
            signal.raise_signal(signal.SIGINT)
            time.sleep(0.010)

    def keep_alive() -> None:
        while not first_sigint and not done.is_set():
            time.sleep(0.01)
        if done.wait(1.6):
            return
        while not done.is_set():
            s.control_queue.put_nowait({"type": "ping"})
            done.wait(0.2)

    threads = [threading.Thread(target=ctrl_c, daemon=True), threading.Thread(target=keep_alive, daemon=True)]
    try:
        for t in threads:
            t.start()
        waiting.set()
        try:
            while not (stop.terminated or stop.restart):
                event = lichess_bot.next_event(s.control_queue)
                if not event:
                    continue
                lichess_bot.accept_challenges(None, s.challenge_queue, {}, 2, 2)
                s.control_queue.task_done()
            got, clean = "stopped", True
        except Exception as e:  # noqa: BLE001
            got, clean = f"{type(e).__name__}: {e}", False
        took = time.monotonic() - first_sigint[0] if first_sigint else float("nan")
        while threads[0].is_alive():  # the key stays down while the bot shuts down
            time.sleep(0.05)
        done.set()
        threads[1].join()
    finally:
        log = s.close()
    clean = clean and took < 0.5 and log.startswith("0 logging error")
    return f"{got} {took:.2f} s after the first SIGINT ({log})", clean


def main() -> int:
    if os.name == "nt":  # a crash in here puts no error box on the desktop
        import ctypes
        ctypes.windll.kernel32.SetErrorMode(0x8003)
    failures = 0

    def case(name: str, check, handler, want_clean: bool) -> None:
        nonlocal failures
        got, clean = check(handler)
        ok = clean == want_clean
        failures += not ok
        print(f"  {'ok  ' if ok else 'FAIL'} {name}: {got}")

    print("lichess-bot's SIGINT handler, as patched:")
    case("one SIGINT between a call and its reply", one_sigint_between_call_and_reply, lichess_bot.signal_handler, True)
    case("Ctrl+C held through the wait for an event", held_ctrl_c, lichess_bot.signal_handler, True)
    print("control, lichess-bot's original handler (must fail):")
    case("one SIGINT between a call and its reply", one_sigint_between_call_and_reply, original_handler, False)
    case("Ctrl+C held through the wait for an event", held_ctrl_c, original_handler, False)
    print("passed" if not failures else f"FAILED: {failures} case(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
