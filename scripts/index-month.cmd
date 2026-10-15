@echo off
rem Index session 1 on one Lichess month (INDEX.md): the read, the checks, duplicates, the queries on the rows
rem and the Lookuptables trial, one after another, all output into games\index\index-<month>.log.
rem   scripts\index-month.cmd 2026-07 [a log to wait for]
rem With a second argument, it first waits (checking each minute) until that log has its "final:" line, as a
rem match's log does when the match is over: the read and the trials need the memory the match is using.
rem Steps: index-month.py rows (one read of the month on the share, ~50-60 min, the rows written as Parquet as it
rem goes, the first 16 moves of each game kept for the trial; then the game count against Lichess's, from the
rem <month>.count beside it: a count SHORT BY n stops the run there, with everything written for a look), check,
rem dups, trial; then scripts\lines-trial.cs on the first 8 M games.
setlocal
set MONTH=%1
set WAITFOR=%2
if "%MONTH%"=="" (
    echo usage: scripts\index-month.cmd 2026-07 [log to wait for]
    exit /b 1
)
cd /d "%~dp0.."
if not defined MONTHS set MONTHS=\\id10terror-desktop\chessbruteforcer\lichess
set ZST=%MONTHS%\lichess_db_standard_rated_%MONTH%.pgn.zst
set DIR=games\index\%MONTH%
set LOG=games\index\index-%MONTH%.log
if not exist games\index mkdir games\index
if not exist "%ZST%" (
    echo %ZST% isn't there.
    pause
    exit /b 1
)
if "%WAITFOR%"=="" goto read
echo %date% %time%: waiting for "final:" in %WAITFOR%
echo %date% %time%: waiting for "final:" in %WAITFOR% >> %LOG%
:wait
findstr /c:"final:" "%WAITFOR%" >nul 2>&1
if not errorlevel 1 goto read
timeout /t 60 /nobreak >nul
goto wait

:read
echo %date% %time%: reading %ZST% (about an hour; progress in %LOG%)
echo %date% %time%: rows >> %LOG%
python -I scripts\index-month.py rows "%ZST%" %DIR% --lines 16 >> %LOG% 2>&1
if errorlevel 1 goto failed
echo %date% %time%: check >> %LOG%
python -I scripts\index-month.py check "%ZST%" %DIR% >> %LOG% 2>&1
if errorlevel 1 goto failed
echo %date% %time%: dups >> %LOG%
python -I scripts\index-month.py dups "%ZST%" %DIR% >> %LOG% 2>&1
if errorlevel 1 goto failed
echo %date% %time%: trial >> %LOG%
python -I scripts\index-month.py trial %DIR% >> %LOG% 2>&1
if errorlevel 1 goto failed
echo %date% %time%: lines trial >> %LOG%
dotnet run -c Release scripts\lines-trial.cs -- %DIR% --games 8000000 >> %LOG% 2>&1
if errorlevel 1 goto failed
echo %date% %time%: finished >> %LOG%
echo %date% %time%: finished: %LOG%
pause
exit /b 0

:failed
echo %date% %time%: a step failed >> %LOG%
echo %date% %time%: a step failed: see %LOG%
pause
exit /b 1
