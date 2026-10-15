@echo off
rem Check one Lichess month on the desktop share against Lichess's SHA-256 (make-book.py verify), in its own
rem window, the check the month queue makes of a month counted before it was kept.
rem   scripts\verify-month.cmd 2026-08
rem Reads the whole file over the share (~30 GB, ~70-80 min). Log: games\verify-<month>.log.
setlocal
set MONTH=%1
if "%MONTH%"=="" (
    echo usage: scripts\verify-month.cmd 2026-08
    exit /b 1
)
cd /d "%~dp0.."
if not defined MONTHS set MONTHS=\\id10terror-desktop\chessbruteforcer\lichess
set ZST=%MONTHS%\lichess_db_standard_rated_%MONTH%.pgn.zst
set LOG=games\verify-%MONTH%.log
title SHA-256 of %MONTH%
echo %date% %time%: checking %ZST% against Lichess's SHA-256 (progress in %LOG%)
echo %date% %time%: checking %ZST% >> %LOG%
bot\lichess-bot\.venv\Scripts\python.exe -I scripts\make-book.py verify "%ZST%" --counts bot\books\counts\%MONTH%.counts >> %LOG% 2>&1
if errorlevel 1 (
    echo %date% %time%: the check failed >> %LOG%
    echo %date% %time%: the check failed: see %LOG%
) else (
    echo %date% %time%: finished >> %LOG%
    echo %date% %time%: finished
)
powershell -NoProfile -Command "Get-Content '%LOG%' -Tail 3"
pause
