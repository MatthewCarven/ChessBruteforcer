@echo off
rem BT4, Matthew's way: the books against each other, our engine on both sides (book-duel.py), in a window of its own.
rem   scripts\book-tables\book-duel.cmd round-robin [<games a pair, 1000>] [<stop time HH:MM>]
rem   scripts\book-tables\book-duel.cmd final <winner: table-a, table-b or vetted> [<stop time HH:MM>]
rem The bot off. round-robin: table-a, table-b and vetted, each against each, into games\book-duel\round-robin.jsonl
rem (raise the games and start it again to add more). final: the winner against no book, SPRT [0, 10], at most 2,000
rem games, into games\book-duel\final-<winner>.jsonl. Both 10+0.1, 6 games at once, two CPUs a game on CPUs 2-13 (one
rem physical core). Once: the bot's engine copied to games\book-duel\engine. Started again, either carries on. At the
rem stop time the games under way are dropped and the window ends; `book-duel.py summary <file>` reads what's there.
setlocal
set HERE=%~dp0
for /f "delims=" %%g in ('git -C "%HERE%." rev-parse --path-format^=absolute --git-common-dir') do set GITDIR=%%g
for %%m in ("%GITDIR%\..") do set MAIN=%%~fm
set PY=%MAIN%\bot\lichess-bot\.venv\Scripts\python.exe
set DIR=%MAIN%\games\book-duel
set STOP=
if /i "%~1"=="final" if "%~2"=="" (echo final needs the winner: table-a, table-b or vetted & exit /b 1)
if /i "%~1"=="round-robin" (
    set ARMS=table-a,table-b,vetted
    set OUT=%DIR%\round-robin.jsonl
    set GAMES=1000
    if not "%~2"=="" set GAMES=%~2
    if not "%~3"=="" set STOP=--stop-at %~3
    set SPRT=
) else if /i "%~1"=="final" (
    set ARMS=%~2,none
    set OUT=%DIR%\final-%~2.jsonl
    set GAMES=2000
    if not "%~3"=="" set STOP=--stop-at %~3
    set SPRT=--sprt 0,10
) else (
    echo usage: book-duel.cmd round-robin [games a pair] [HH:MM]  or  book-duel.cmd final ^<winner^> [HH:MM]
    exit /b 1
)
if not exist "%PY%" (echo no python at %PY% & exit /b 1)
if not exist "%DIR%" mkdir "%DIR%"
if not exist "%DIR%\engine\ChessBruteforcer.Engine.exe" robocopy "%MAIN%\bot\lichess-bot\engines\bruteforcer" "%DIR%\engine" /E /NFL /NDL /NJH /NJS /NP >nul
if not exist "%DIR%\engine\ChessBruteforcer.Engine.exe" (echo the engine copy failed & exit /b 1)
"%PY%" -I "%HERE%book-duel.py" run "%DIR%\engine\ChessBruteforcer.Engine.exe" "%OUT%" --arms "%ARMS%" --games %GAMES% %SPRT% %STOP%
if errorlevel 3 (echo stopped at the stop time: start this again to carry on & exit /b 3)
if errorlevel 1 (echo the match stopped: see %OUT:.jsonl=.log% & exit /b 1)
echo book-duel finished %date% %time%
