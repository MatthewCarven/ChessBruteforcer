@echo off
rem Turn the Lichess account whose token is in bot\lichess-bot\config.yml into a BOT account.
rem PERMANENT: that account can never play as a human again, and it must not have played any games.
rem Matthew runs this once, by hand.  Done for matthewcarven on 2026-10-13.
rem After upgrading, lichess-bot carries on running as the bot: close this window before bot\start-bot.cmd,
rem or Lichess sees two copies connected and rate-limits the second (it recovers a minute after the first goes).
setlocal
echo This makes the account behind the token in bot\lichess-bot\config.yml a BOT account, for good.
set /p ANSWER=Type YES to go ahead:
if not "%ANSWER%"=="YES" (
    echo Nothing done.
    pause
    exit /b 1
)
cd /d "%~dp0lichess-bot"
"%~dp0lichess-bot\.venv\Scripts\python.exe" lichess-bot.py -u
pause
