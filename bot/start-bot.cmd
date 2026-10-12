@echo off
rem Run BruteForceBot on Lichess (lichess-bot with our engine and tables).  Ctrl+C, or close the window, stops it.
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
cd /d "%~dp0lichess-bot"
for /f %%d in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-dd"') do set TODAY=%%d
"%PY%" lichess-bot.py -l "%~dp0logs\bot-%TODAY%.log"
pause
