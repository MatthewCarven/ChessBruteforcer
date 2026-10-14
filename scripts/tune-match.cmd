@echo off
rem One round of Texel tuning towards Stockfish: fit new numbers, then a match against the current ones.
rem   scripts\tune-match.cmd sf2 sf1 games\tune\positions-2026-09-sf.tsv
rem 1. `tune fit` -> games\tune\eval-<new>.txt (skipped if it's there): every thread at below-normal priority,
rem    so the bot's games come first; ~3 GB of memory for 13.6 M positions (capped at 3.5 GB, so a shortfall
rem    stops this and nothing else).
rem 2. A match, <new> v <old> (games\tune\eval-<old>.txt): 10+0.1, 5 at a time, 300 openings from our book,
rem    SPRT [0, 10], at most 2000 games, our tables adjudicating. The match tool splits its engine command at
rem    the first space, so the engine and both evaluations are copied to %TEMP%\cb-match first.
rem Logs: games\tune\fit-<new>.log, games\tune\match-<new>.log and .pgn.
setlocal
set NEW=%1
set OLD=%2
set POS=%3
if "%POS%"=="" (
    echo usage: scripts\tune-match.cmd sf2 sf1 games\tune\positions-2026-09-sf.tsv
    exit /b 1
)
cd /d "%~dp0.."
set EVAL=games\tune\eval-%NEW%.txt
set FITLOG=games\tune\fit-%NEW%.log
set MLOG=games\tune\match-%NEW%.log
set COPY=%TEMP%\cb-match
if not exist games\tune\eval-%OLD%.txt (
    echo games\tune\eval-%OLD%.txt isn't there.
    pause
    exit /b 1
)
if not exist "%POS%" (
    echo %POS% isn't there.
    pause
    exit /b 1
)
for %%p in (Cli Engine Match) do (
    dotnet build src\ChessBruteforcer.%%p -c Release --nologo -v q
    if errorlevel 1 (
        echo build of %%p failed
        pause
        exit /b 1
    )
)

if exist %EVAL% (
    echo %EVAL% is there already: straight to the match.
) else (
    echo %date% %time%: fitting %EVAL% on %POS% ^(about 10-15 min; progress in %FITLOG%^)
    xcopy /e /i /q /y src\ChessBruteforcer.Cli\bin\Release\net8.0 games\.cli-tune >nul
    set DOTNET_GCHeapHardLimit=0xE0000000
    start "" /b /wait /belownormal dotnet games\.cli-tune\ChessBruteforcer.Cli.dll tune fit %POS% %EVAL%.tmp >> %FITLOG% 2>&1
    set DOTNET_GCHeapHardLimit=
)
if not exist %EVAL% (
    if not exist %EVAL%.tmp (
        echo the fit failed: see %FITLOG%
        pause
        exit /b 1
    )
    move /y %EVAL%.tmp %EVAL% >nul
)
echo %date% %time%: fitted; the last lines of %FITLOG%:
powershell -NoProfile -Command "Get-Content '%FITLOG%' -Tail 10"

if exist "%COPY%" rmdir /s /q "%COPY%"
xcopy /e /i /q /y src\ChessBruteforcer.Engine\bin\Release\net8.0 "%COPY%\engine" >nul
copy /y %EVAL% "%COPY%\eval-%NEW%.txt" >nul
copy /y games\tune\eval-%OLD%.txt "%COPY%\eval-%OLD%.txt" >nul
echo %date% %time%: match %NEW% v %OLD% (progress in %MLOG%)
dotnet src\ChessBruteforcer.Match\bin\Release\net8.0\ChessBruteforcer.Match.dll ^
    --engine name=%NEW% cmd="dotnet %COPY%\engine\ChessBruteforcer.Engine.dll" option.EvalFile=%COPY%\eval-%NEW%.txt ^
    --engine name=%OLD% cmd="dotnet %COPY%\engine\ChessBruteforcer.Engine.dll" option.EvalFile=%COPY%\eval-%OLD%.txt ^
    --games 2000 --tc 10+0.1 --concurrency 5 --tables tables --sprt 0 10 ^
    --openings games\tune\openings-300.txt --pgn games\tune\match-%NEW%.pgn > %MLOG% 2>&1
echo %date% %time%: finished; the last lines of %MLOG%:
powershell -NoProfile -Command "Get-Content '%MLOG%' -Tail 6"
pause
