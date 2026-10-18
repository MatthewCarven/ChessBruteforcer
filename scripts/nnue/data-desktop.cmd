@echo off
rem The network's training records for one Lichess month, made on the desktop from the month file on its own disk
rem (NNUE.md, session 5: more data), so nothing crosses the link and the laptop keeps the bot. Lives in the share's
rem nnue folder, i.e. on the desktop
rem   D:\-=Chess Bruteforcer=-\nnue\data-desktop.cmd 2026-07
rem and is started there by a scheduled task (scripts\nnue-desktop.ps1 data <month> [+ <month> ...]). The steps of
rem scripts\tune-data.cmd and scripts\nnue-data.cmd, at below-normal priority:
rem   1. scripts\eval-games.py: the month's analysed games, from ..\lichess\lichess_db_standard_rated_<month>.pgn.zst,
rem      into work\eval-games-<month>.pgn (~7 GB)
rem   2. game import: work\eval-games-<month>.cbg (~4 GB)
rem   3. nnue data: <month>\ (64 buckets, held-back.bin, data.json written last; ~7.5 GB), the .NET heap capped at
rem      1 GB as on the laptop
rem   4. records.py check <month>: every file against data.json, every board sane
rem Each step is skipped when its output is there, so a stopped run carries on from the step it was in. The command
rem line tool is cli\ (scripts\nnue-desktop.ps1 sync builds and copies it). Log: data-<month>.log.
setlocal
set M=%1
if "%M%"=="" (
    echo usage: data-desktop.cmd 2026-07
    exit /b 1
)
set ROOT=%~dp0
set ZST=%ROOT%..\lichess\lichess_db_standard_rated_%M%.pgn.zst
set WORK=%ROOT%work
set PGN=%WORK%\eval-games-%M%.pgn
set CBG=%WORK%\eval-games-%M%.cbg
set OUT=%ROOT%%M%
set LOG=%ROOT%data-%M%.log
set PY=C:\Program Files\Python314\python.exe
set VPY=%ROOT%.venv\Scripts\python.exe
set CLI=%ROOT%cli\ChessBruteforcer.Cli.dll
if not exist "%WORK%" mkdir "%WORK%"
if not exist "%ZST%" (
    echo %date% %time%: %ZST% isn't there >> "%LOG%"
    exit /b 1
)
echo %date% %time%: %M% on %COMPUTERNAME% >> "%LOG%"

if not exist "%CBG%" if not exist "%PGN%" (
    echo %date% %time%: 1. keeping the analysed games of %ZST% >> "%LOG%"
    start "" /b /wait /belownormal "%PY%" -I "%ROOT%scripts\eval-games.py" "%ZST%" "%PGN%.tmp" >> "%LOG%" 2>&1
    if errorlevel 1 (
        echo %date% %time%: filtering failed >> "%LOG%"
        exit /b 1
    )
    move /y "%PGN%.tmp" "%PGN%" >nul
)
if not exist "%CBG%" (
    echo %date% %time%: 2. importing %PGN% >> "%LOG%"
    start "" /b /wait /belownormal dotnet "%CLI%" game import "%CBG%.tmp" "%PGN%" >> "%LOG%" 2>&1
    if errorlevel 1 (
        echo %date% %time%: importing failed >> "%LOG%"
        exit /b 1
    )
    move /y "%CBG%.tmp" "%CBG%" >nul
)
if not exist "%OUT%\data.json" (
    echo %date% %time%: 3. nnue data %OUT% %CBG% >> "%LOG%"
    set DOTNET_GCHeapHardLimit=0x40000000
    start "" /b /wait /belownormal dotnet "%CLI%" nnue data "%OUT%" "%CBG%" >> "%LOG%" 2>&1
    if errorlevel 1 (
        echo %date% %time%: nnue data failed >> "%LOG%"
        exit /b 1
    )
    set DOTNET_GCHeapHardLimit=
)
echo %date% %time%: 4. checking %OUT% >> "%LOG%"
start "" /b /wait /belownormal "%VPY%" "%ROOT%scripts\nnue\records.py" check "%OUT%" >> "%LOG%" 2>&1
if errorlevel 1 (
    echo %date% %time%: the check FAILED >> "%LOG%"
    exit /b 1
)
echo %date% %time%: finished %M% >> "%LOG%"
exit /b 0
