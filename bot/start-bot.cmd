@echo off
rem Run BruteForceBot on Lichess (lichess-bot with our engine and tables).  Ctrl+C, or close the window, stops it;
rem if lichess-bot stops by itself (a network fault it didn't ride out), run-bot.ps1 starts it again.
rem   bot\start-bot.cmd
rem To stop it once its games are over instead, bot\quit-soon.cmd (it makes bot\quit.soon).
rem Runs from a copy of the engine build (bot\lichess-bot\engines\bruteforcer) so the repo stays buildable;
rem the copy is refreshed from the latest build each start.  Its log goes to bot\logs, its games to bot\games.
setlocal
cd /d "%~dp0.."
rem A fresh start plays: a quit.soon left from before goes (one dropped during the build below still counts).
if exist bot\quit.soon del bot\quit.soon
if exist bot\quit.soon.txt del bot\quit.soon.txt
dotnet build src\ChessBruteforcer.Engine -c Release --nologo -v q
if errorlevel 1 (
    rem Another window may have been building the same project at that moment: once more, after a pause.
    echo build failed: trying again in 20 s
    timeout /t 20 /nobreak >nul
    dotnet build src\ChessBruteforcer.Engine -c Release --nologo -v q
)
if errorlevel 1 (
    echo build failed
    pause
    exit /b 1
)
xcopy /e /i /q /y src\ChessBruteforcer.Engine\bin\Release\net8.0 bot\lichess-bot\engines\bruteforcer >nul
if not exist bot\logs mkdir bot\logs
set PY=%~dp0lichess-bot\.venv\Scripts\python.exe
"%PY%" "%~dp0make-config.py"
findstr /c:"PASTE-YOUR-TOKEN-HERE" bot\lichess-bot\config.yml >nul
if %errorlevel%==0 (
    echo.
    echo The token isn't in bot\lichess-bot\config.yml yet: open it in Notepad and paste it on the token line.
    pause
    exit /b 1
)
rem Our chat commands (!info, and !help listing it): bot\chat_commands.py into lichess-bot, and the small change
rem to its lib\conversation.py that asks it first (bot\lichess-bot-chat.patch), unless that's in already.
copy /y bot\chat_commands.py bot\lichess-bot\lib\bruteforcer_chat.py >nul
git -C bot\lichess-bot apply --reverse --check "%~dp0lichess-bot-chat.patch" >nul 2>&1
if errorlevel 1 (
    git -C bot\lichess-bot apply "%~dp0lichess-bot-chat.patch"
    if errorlevel 1 echo The chat commands didn't go in ^(lichess-bot changed?^): starting without !info.
)
rem A game whose stream lichess.org closes mid-game is opened again rather than left to run out of time
rem (bot\lichess-bot-rejoin.patch, 2026-10-17; bot\test-rejoin.py tests it), unless that's in already.
git -C bot\lichess-bot apply --reverse --check "%~dp0lichess-bot-rejoin.patch" >nul 2>&1
if errorlevel 1 (
    git -C bot\lichess-bot apply "%~dp0lichess-bot-rejoin.patch"
    if errorlevel 1 echo The rejoin fix didn't go in ^(lichess-bot changed?^): a dropped game stream will lose games again.
)
rem A challenge that comes while every game slot is taken is declined at once ("later") rather than queued, where it
rem used to go stale (bot\lichess-bot-busy.patch, 2026-10-18; bot\test-busy.py tests it), unless that's in already.
git -C bot\lichess-bot apply --reverse --check "%~dp0lichess-bot-busy.patch" >nul 2>&1
if errorlevel 1 (
    git -C bot\lichess-bot apply "%~dp0lichess-bot-busy.patch"
    if errorlevel 1 echo The busy decline didn't go in ^(lichess-bot changed?^): challenges will queue and go stale again.
)
rem A game already being played isn't started a second time when lichess.org sends its gameStart again (it does on
rem every event-stream reconnect; the game's own player rejoins by itself): bot\lichess-bot-one-player.patch,
rem 2026-10-19; bot\test-one-player.py tests it. Unless that's in already.
git -C bot\lichess-bot apply --reverse --check "%~dp0lichess-bot-one-player.patch" >nul 2>&1
if errorlevel 1 (
    git -C bot\lichess-bot apply "%~dp0lichess-bot-one-player.patch"
    if errorlevel 1 echo The one-player fix didn't go in ^(lichess-bot changed?^): a reconnect can put two players on a game.
)
rem A move found after a ponder hit says so in the log ("Source: Engine, ponder hit"), and why it has no stats when it has
rem none: bot\lichess-bot-ponder-note.patch, 2026-10-20; bot\test-ponder-note.py tests it. Unless that's in already.
git -C bot\lichess-bot apply --reverse --check "%~dp0lichess-bot-ponder-note.patch" >nul 2>&1
if errorlevel 1 (
    git -C bot\lichess-bot apply "%~dp0lichess-bot-ponder-note.patch"
    if errorlevel 1 echo The ponder note didn't go in ^(lichess-bot changed?^): ponder hits log no source note, as before.
)
rem A Ctrl+C, even held down, stops lichess-bot cleanly: its handler only sets the stop flags. It used to log a line
rem through the Manager the main loop waits on, and a held Ctrl+C then ended in a TypeError in accept_challenges (4 of
rem 26 stops): bot\lichess-bot-sigint.patch, 2026-10-20; bot\test-sigint.py tests it. Unless that's in already.
git -C bot\lichess-bot apply --reverse --check "%~dp0lichess-bot-sigint.patch" >nul 2>&1
if errorlevel 1 (
    git -C bot\lichess-bot apply "%~dp0lichess-bot-sigint.patch"
    if errorlevel 1 echo The Ctrl+C fix didn't go in ^(lichess-bot changed?^): a held Ctrl+C can end in a TypeError again.
)
rem bot\quit.soon (bot\quit-soon.cmd) stops the bot once its games are over: no new games, Matthew's goodbye at the end
rem of the ones still on, then lichess-bot stops and run-bot.ps1 starts nothing: bot\lichess-bot-quit-soon.patch,
rem 2026-10-20; bot\test-quit-soon.py tests it. Unless that's in already.
git -C bot\lichess-bot apply --reverse --check "%~dp0lichess-bot-quit-soon.patch" >nul 2>&1
if errorlevel 1 (
    git -C bot\lichess-bot apply "%~dp0lichess-bot-quit-soon.patch"
    if errorlevel 1 echo The quit.soon stop didn't go in ^(lichess-bot changed?^): bot\quit.soon will do nothing.
)
set BRUTEFORCER_TABLES=%CD%\tables
rem lichess-bot in a loop (bot\run-bot.ps1): started again whenever it stops on its own, once lichess.org answers;
rem Ctrl+C stops both, and restarts.log says so.
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-bot.ps1"
pause
