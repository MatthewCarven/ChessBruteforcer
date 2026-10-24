@echo off
rem Decision C (a): the vetted book against the human book as Black, in a window of its own (vet-c-match.py).
rem   scripts\book-tables\vet-c-match.cmd [<stop time HH:MM>] [<games at once, 6>]
rem The bot off. Once: the bot's engine copied to games\vet-c-match\engine, and the pairs made (games\vet-c-match\
rem pairs.json, a few minutes of one core). Then the games: 10+0.1, SPRT [-10, 0], two CPUs a game on CPUs 2-13 (one
rem physical core), into games\vet-c-match\games.jsonl (games.log beside it). Started again, it carries on. At the
rem stop time the games under way are dropped and the window ends; `vet-c-match.py summary` reads what's there.
setlocal
set HERE=%~dp0
set STOP=
if not "%~1"=="" set STOP=--stop-at %~1
set N=6
if not "%~2"=="" set N=%~2
for /f "delims=" %%g in ('git -C "%HERE%." rev-parse --path-format^=absolute --git-common-dir') do set GITDIR=%%g
for %%m in ("%GITDIR%\..") do set MAIN=%%~fm
set PY=%MAIN%\bot\lichess-bot\.venv\Scripts\python.exe
set DIR=%MAIN%\games\vet-c-match
if not exist "%PY%" (echo no python at %PY% & exit /b 1)
if not exist "%DIR%\engine\ChessBruteforcer.Engine.exe" robocopy "%MAIN%\bot\lichess-bot\engines\bruteforcer" "%DIR%\engine" /E /NFL /NDL /NJH /NJS /NP >nul
if not exist "%DIR%\engine\ChessBruteforcer.Engine.exe" (echo the engine copy failed & exit /b 1)
if not exist "%DIR%\pairs.json" "%PY%" -I "%HERE%vet-c-match.py" pairs "%DIR%\pairs.json" || (echo making the pairs failed & exit /b 1)
"%PY%" -I "%HERE%vet-c-match.py" run "%DIR%\engine\ChessBruteforcer.Engine.exe" "%DIR%\pairs.json" "%DIR%\games.jsonl" --concurrency %N% %STOP%
if errorlevel 3 (echo stopped at the stop time: start this again to carry on & exit /b 3)
if errorlevel 1 (echo the match stopped: see games\vet-c-match\games.log & exit /b 1)
echo vet-c-match finished %date% %time%
