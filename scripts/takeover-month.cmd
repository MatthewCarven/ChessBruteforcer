@echo off
rem `game takeover --moves` on a whole month's games that reach our tables (games\<month>-tables.cbg, from
rem scripts\month-tables.cmd): every move played inside the tables against the tables' verdict, by rating,
rem time control, table and clock.  It reads the games in batches of 100,000 and keeps a small summary of
rem each, so memory stays under ~2 GB.  About 3 hours for September 2026 (5.26 M games).
rem   scripts\takeover-month.cmd 2026-09
rem Writes scripts\takeover-moves-<month>-month.txt; progress in games\takeover-<month>.log.
setlocal
set MONTH=%1
if "%MONTH%"=="" (
    echo usage: scripts\takeover-month.cmd 2026-09
    exit /b 1
)
cd /d "%~dp0.."
set CBG=games\%MONTH%-tables.cbg
set OUT=scripts\takeover-moves-%MONTH%-month.txt
set LOG=games\takeover-%MONTH%.log
if not exist %CBG% (
    echo %CBG% isn't there: scripts\month-tables.cmd %MONTH% makes it.
    pause
    exit /b 1
)
dotnet build src\ChessBruteforcer.Cli -c Release --nologo -v q
if errorlevel 1 (
    echo build failed
    pause
    exit /b 1
)
xcopy /e /i /q /y src\ChessBruteforcer.Cli\bin\Release\net8.0 games\.cli-takeover >nul
echo %date% %time%: game takeover %CBG% --moves >> %LOG%
echo %date% %time%: game takeover %CBG% --moves (about 3 hours; progress in %LOG%)
dotnet games\.cli-takeover\ChessBruteforcer.Cli.dll game takeover %CBG% --moves > %OUT%.tmp 2>> %LOG%
if errorlevel 1 (
    echo %date% %time%: failed, exit %errorlevel% >> %LOG%
    echo failed: see %LOG%
    pause
    exit /b 1
)
move /y %OUT%.tmp %OUT% >nul
echo %date% %time%: finished: %OUT% >> %LOG%
echo %date% %time%: finished: %OUT%
pause
