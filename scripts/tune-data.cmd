@echo off
rem Texel tuning data from one Lichess month: its analysed games (Stockfish's evaluation after every move) as
rem positions with Stockfish's verdict as the target, ready for `tune fit`.
rem   scripts\tune-data.cmd 2026-09 [positions per game, default 2]
rem Steps (about 2.5 hours for a whole month): scripts\eval-games.py over the month on the desktop share
rem -> games\tune\eval-games-<month>.pgn (~7 GB), `game import` -> games\tune\eval-games-<month>.cbg (~4 GB),
rem `tune positions --lambda 0` -> games\tune\positions-<month>-sf.tsv.  Each step is skipped if its output
rem is already there; the .pgn can go once the .cbg exists.  Progress: games\tune\tune-data-<month>.log.
setlocal
set MONTH=%1
set PER=%2
if "%MONTH%"=="" (
    echo usage: scripts\tune-data.cmd 2026-09 [positions per game]
    exit /b 1
)
if "%PER%"=="" set PER=2
cd /d "%~dp0.."
if not defined MONTHS set MONTHS=\\id10terror-desktop\chessbruteforcer\lichess
set ZST=%MONTHS%\lichess_db_standard_rated_%MONTH%.pgn.zst
set PGN=games\tune\eval-games-%MONTH%.pgn
set CBG=games\tune\eval-games-%MONTH%.cbg
set TSV=games\tune\positions-%MONTH%-sf.tsv
set LOG=games\tune\tune-data-%MONTH%.log
if not exist games\tune mkdir games\tune
if not exist "%ZST%" (
    echo %ZST% isn't there.
    pause
    exit /b 1
)
dotnet build src\ChessBruteforcer.Cli -c Release --nologo -v q
if errorlevel 1 (
    echo build failed
    pause
    exit /b 1
)
xcopy /e /i /q /y src\ChessBruteforcer.Cli\bin\Release\net8.0 games\.cli-tune >nul

if exist %CBG% goto positions
if exist %PGN% goto import
echo %date% %time%: keeping the analysed games of %ZST% (about 50 min) >> %LOG%
echo %date% %time%: keeping the analysed games of %ZST% (about 50 min; progress in %LOG%)
python -I scripts\eval-games.py "%ZST%" %PGN% >> %LOG% 2>&1
if errorlevel 1 (
    echo filtering failed: see %LOG%
    pause
    exit /b 1
)

:import
echo %date% %time%: importing %PGN% (about an hour) >> %LOG%
echo %date% %time%: importing %PGN% (about an hour)
dotnet games\.cli-tune\ChessBruteforcer.Cli.dll game import %CBG%.tmp %PGN% >> %LOG% 2>&1
if errorlevel 1 (
    echo importing failed: see %LOG%
    pause
    exit /b 1
)
move /y %CBG%.tmp %CBG% >nul

:positions
if exist %TSV% goto done
echo %date% %time%: positions, %PER% a game (about 40 min) >> %LOG%
echo %date% %time%: positions, %PER% a game (about 40 min)
dotnet games\.cli-tune\ChessBruteforcer.Cli.dll tune positions %TSV%.tmp %CBG% --min-elo 0 --per-game %PER% --lambda 0 >> %LOG% 2>&1
if errorlevel 1 (
    echo positions failed: see %LOG%
    pause
    exit /b 1
)
move /y %TSV%.tmp %TSV% >nul

:done
echo %date% %time%: finished >> %LOG%
echo %date% %time%: finished: %TSV%
pause
