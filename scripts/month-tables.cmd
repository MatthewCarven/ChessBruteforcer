@echo off
rem The games of a downloaded Lichess month that reach our tables (5 pieces or fewer), with their clocks,
rem as a .cbg for `game takeover --moves`: scripts\table-games.py streams the .pgn.zst (nothing unpacked
rem to disk), then `game import`.  About 2 hours for September 2026 (89.6 M games, ~5.4 M kept).
rem   scripts\month-tables.cmd 2026-09
rem Writes games\<month>-tables.pgn and games\<month>-tables.cbg (refuses if the .cbg is there already:
rem import would append to it); progress in games\month-tables-<month>.log.
setlocal
set MONTH=%1
if "%MONTH%"=="" (
    echo usage: scripts\month-tables.cmd 2026-09
    exit /b 1
)
cd /d "%~dp0.."
set ZST=games\lichess_db_standard_rated_%MONTH%.pgn.zst
set PGN=games\%MONTH%-tables.pgn
set CBG=games\%MONTH%-tables.cbg
set LOG=games\month-tables-%MONTH%.log
if not exist %ZST% (
    echo %ZST% isn't there: download it first, scripts\download-lichess-month.cmd %MONTH%
    pause
    exit /b 1
)
if exist %CBG% (
    echo %CBG% is there already; move or delete it first, or import would append to it.
    pause
    exit /b 1
)
dotnet build src\ChessBruteforcer.Cli -c Release --nologo -v q
if errorlevel 1 (
    echo build failed
    pause
    exit /b 1
)
xcopy /e /i /q /y src\ChessBruteforcer.Cli\bin\Release\net8.0 games\.cli-import >nul
echo %date% %time%: filtering %ZST% >> %LOG%
echo %date% %time%: filtering %ZST% (about 50 min; progress every million games in %LOG%)
python -I scripts\table-games.py %ZST% %PGN% >> %LOG% 2>&1
if not exist %PGN% (
    echo filtering failed: see %LOG%
    pause
    exit /b 1
)
echo %date% %time%: importing %PGN% >> %LOG%
echo %date% %time%: importing %PGN% (about 70 min)
dotnet games\.cli-import\ChessBruteforcer.Cli.dll game import %CBG% %PGN% >> %LOG% 2>&1
echo %date% %time%: finished, exit %errorlevel% >> %LOG%
echo %date% %time%: finished, exit %errorlevel%
pause
