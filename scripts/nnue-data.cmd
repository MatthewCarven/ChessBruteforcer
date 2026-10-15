@echo off
rem The network's training data for one month (NNUE.md, session 1), in its own window:
rem   scripts\nnue-data.cmd 2026-09 [--speeds blitz,rapid,classical] [--per-game N] [--buckets 64]
rem Reads games\tune\eval-games-<month>.cbg (tune-data.cmd makes it) and writes games\nnue\<month>\: 64 bucket
rem files, held-back.bin (games numbered 0 mod 50) and data.json, written last. September: ~235 M positions,
rem ~7.5 GB, ~45 min. Below-normal priority, so the bot comes first; the .NET heap capped at 1 GB (it needs tens
rem of MB), so a shortfall stops this and nothing else; run from a copy of the build, so the repo stays
rem buildable. Log: games\nnue\<month>.log. Then: python scripts\nnue\records.py check games\nnue\<month>
rem CB_GAMES=<folder> reads and writes another checkout's games folder instead (a worktree has none).
setlocal
set MONTH=%1
if "%MONTH%"=="" (
    echo usage: scripts\nnue-data.cmd 2026-09 [nnue data options]
    exit /b 1
)
cd /d "%~dp0.."
if "%CB_GAMES%"=="" set CB_GAMES=games
set GAMES=%CB_GAMES%\tune\eval-games-%MONTH%.cbg
set OUT=%CB_GAMES%\nnue\%MONTH%
set LOG=%CB_GAMES%\nnue\%MONTH%.log
set COPY=%TEMP%\cb-nnue-data
if not exist "%GAMES%" (
    echo %GAMES% isn't there.
    pause
    exit /b 1
)
dotnet build src\ChessBruteforcer.Cli -c Release --nologo -v q
if errorlevel 1 (
    echo the build failed
    pause
    exit /b 1
)
if not exist "%CB_GAMES%\nnue" mkdir "%CB_GAMES%\nnue"
xcopy /e /i /q /y src\ChessBruteforcer.Cli\bin\Release\net8.0 "%COPY%" >nul
echo %date% %time%: nnue data %OUT% %GAMES% %2 %3 %4 %5 %6 %7 (progress in %LOG%)
echo %date% %time%: nnue data %OUT% %GAMES% %2 %3 %4 %5 %6 %7, from %CD% >> "%LOG%"
set DOTNET_GCHeapHardLimit=0x40000000
start "" /b /wait /belownormal dotnet "%COPY%\ChessBruteforcer.Cli.dll" nnue data "%OUT%" "%GAMES%" %2 %3 %4 %5 %6 %7 >> "%LOG%" 2>&1
set CODE=%errorlevel%
set DOTNET_GCHeapHardLimit=
echo %date% %time%: finished, exit %CODE% >> "%LOG%"
echo %date% %time%: finished, exit %CODE%; the last lines of %LOG%:
powershell -NoProfile -Command "Get-Content '%LOG%' -Tail 8"
pause
