@echo off
rem One stint of the Stockfish check's half with tables (MATCHES.md, 2026-10-26), in a window of its own: our engine
rem (the repo's tables, Hash 256, h1536-3m-ob8) against Stockfish 16 at UCI_Elo 3190, 10+0.1, one game at a time, into
rem games\sf-check\sf3190.jsonl in the main checkout.
rem   scripts\sf-check\sf3190-night.cmd <games still to play> <log name, e.g. sf3190-2.log>
rem book-match.py's --games adds that many games to what the file has. sf3190-night.ps1 starts these, watches memory
rem and starts the next one after a stop.
setlocal
set HERE=%~dp0
for /f "delims=" %%g in ('git -C "%HERE%." rev-parse --path-format^=absolute --git-common-dir') do set GITDIR=%%g
for %%m in ("%GITDIR%\..") do set MAIN=%%~fm
set PY=%MAIN%\bot\lichess-bot\.venv\Scripts\python.exe
if "%~2"=="" (echo usage: sf3190-night.cmd ^<games still to play^> ^<log name^> & exit /b 1)
if not exist "%PY%" (echo no python at %PY% & exit /b 1)
title Stockfish check 3190: with tables, 1 at a time (%~2)
cd /d "%MAIN%"
echo %date% %time% with tables, 1 at a time, %1 games: games\sf-check\sf3190.jsonl, log %~2
"%PY%" -I scripts\book-match.py games\sf-check\sf3190.jsonl --games %1 --tc 10+0.1 --sf-elo 3190 --concurrency 1 --arms "no book" --eval bot\eval\h1536-3m-ob8.nnue > "games\sf-check\%~2" 2>&1
echo %date% %time% done (exit %errorlevel%)
