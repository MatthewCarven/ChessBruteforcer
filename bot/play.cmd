@echo off
rem Play the bot at home (bot\play.py): a board and clocks against the engine with the bot's network, tables and
rem books; or load a real game and take over from any move.  Games go to bot\home\games, what it learns to
rem bot\books\home.bin (only this window plays from it; the Lichess bot and the learner never see either).
rem Beside the live bot it thinks on 2 threads at low priority.  Double-click it.
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
rem Its own copy of the engine, so a rebuild never meets a running engine's locked files.  A copy that fails (this
rem window's engine still running from before) keeps the old one.
xcopy /e /i /q /y src\ChessBruteforcer.Engine\bin\Release\net8.0 bot\home\engine >nul || echo (the engine copy is in use: playing on the one from before)
"%~dp0lichess-bot\.venv\Scripts\python.exe" -I "%~dp0play.py" %*
if errorlevel 1 pause
