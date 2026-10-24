@echo off
rem BT1 (b)'s searches in a window of their own (scripts\book-tables\table-b.py; BOOK-TABLES.md, decision BT):
rem   scripts\book-tables\table-b.cmd <engine folder> [table-b.py options: --limit 8, --engines 8, --depth 18]
rem <engine folder> holds a copy of a Release build of src\ChessBruteforcer.Engine (not the bot's: a running DLL
rem blocks rebuilds) and h1536-3m.nnue, in a path with no spaces. The log is scripts\book-tables\results\table-b.jsonl
rem in this checkout, and a run started again carries on from it; progress goes to table-b.log beside it. To stop
rem with the running searches finished, create table-b.jsonl.stop there (delete it before the next run). To keep
rem only N engines busy (beside the GPU training), write N into table-b.jsonl.engines there; it's read every ~30 s.
rem Python is the main checkout's bot venv (python-chess).
setlocal
set ENG=%~1
if "%ENG%"=="" (
    echo usage: scripts\book-tables\table-b.cmd ^<engine folder^> [table-b.py options]
    exit /b 1
)
for /f "tokens=1,*" %%a in ("%*") do set REST=%%b
cd /d "%~dp0..\.."
rem The main checkout: the folder above git's common directory (this checkout itself when it isn't a worktree).
for /f "delims=" %%g in ('git rev-parse --git-common-dir') do set GITDIR=%%g
for %%m in ("%GITDIR%\..") do set MAIN=%%~fm
title BT1 (b): the 5-ply table
"%MAIN%\bot\lichess-bot\.venv\Scripts\python.exe" -I scripts\book-tables\table-b.py scripts\book-tables\results\table-b.jsonl --engine "%ENG%\ChessBruteforcer.Engine.exe" --eval "%ENG%\h1536-3m.nnue" %REST%
echo table-b.py finished: exit %errorlevel%
