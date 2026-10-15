@echo off
rem The book learner (bot\learn.py): after each of the bot's games, thinks about the positions where our books
rem had no move (or only a rarely played one) and keeps the best moves in bot\books\ours.bin.  Low priority,
rem its own window; close it any time, it carries on where it was.  start.cmd starts it with the bot.
setlocal
cd /d "%~dp0.."
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
xcopy /e /i /q /y src\ChessBruteforcer.Engine\bin\Release\net8.0 bot\lichess-bot\engines\learner >nul
"%~dp0lichess-bot\.venv\Scripts\python.exe" -I "%~dp0learn.py" %*
pause
