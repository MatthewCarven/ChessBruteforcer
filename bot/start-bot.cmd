@echo off
rem Run BruteForceBot on Lichess (lichess-bot with our engine and tables).  Ctrl+C, or close the window, stops it;
rem if lichess-bot stops by itself (a network fault it didn't ride out), run-bot.ps1 starts it again.
rem   bot\start-bot.cmd
rem Runs from a copy of the engine build (bot\lichess-bot\engines\bruteforcer) so the repo stays buildable;
rem the copy is refreshed from the latest build each start.  Its log goes to bot\logs, its games to bot\games.
setlocal
cd /d "%~dp0.."
dotnet build src\ChessBruteforcer.Engine -c Release --nologo -v q
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
set BRUTEFORCER_TABLES=%CD%\tables
rem lichess-bot in a loop (bot\run-bot.ps1): started again whenever it stops on its own, once lichess.org answers.
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-bot.ps1"
pause
