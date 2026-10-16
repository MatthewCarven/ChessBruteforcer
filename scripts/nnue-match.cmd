@echo off
rem A network against another evaluation (NNUE.md, "How it is judged"), in its own window:
rem   scripts\nnue-match.cmd nnue1 sf1 [games] [concurrency]
rem <net> is games\nnue\nets\<net>.nnue; <old> is another net there, or evaluation numbers (bot\eval\eval-<old>.txt,
rem else games\tune\eval-<old>.txt). Played as sf1's and sf2's matches were: 10+0.1, 5 at a time, the 300 book
rem openings in colour-reversed pairs, our tables adjudicating, SPRT [0, 10], at most 2000 games ([games] for a
rem smoke test, [concurrency] beside the bot). The nets, openings, tables and logs are the main checkout's, so this
rem runs from a worktree too. The match tool splits its engine command at the first space, so the engine, the match
rem tool and both evaluations are copied to %TEMP%\cb-match first (and the repo stays buildable). Below-normal
rem priority, which the engines inherit, so the bot's games come first.
rem Logs: games\nnue\match-<net>-<old>.log and .pgn (match-<net>-<old>-<games>.* when [games] isn't 2000).
setlocal
set NET=%1
set OLD=%2
set GAMES=%3
set CONC=%4
if "%OLD%"=="" (
    echo usage: scripts\nnue-match.cmd nnue1 sf1 [games] [concurrency]
    exit /b 1
)
if "%GAMES%"=="" set GAMES=2000
if "%CONC%"=="" set CONC=5
cd /d "%~dp0.."
rem The main checkout: the folder above git's common directory (this checkout itself when it isn't a worktree).
for /f "delims=" %%g in ('git rev-parse --git-common-dir') do set GITDIR=%%g
for %%m in ("%GITDIR%\..") do set MAIN=%%~fm
set NETS=%MAIN%\games\nnue\nets
set TAG=%NET%-%OLD%
if not "%GAMES%"=="2000" set TAG=%NET%-%OLD%-%GAMES%
set MLOG=%MAIN%\games\nnue\match-%TAG%.log
set PGN=%MAIN%\games\nnue\match-%TAG%.pgn
set COPY=%TEMP%\cb-match

if not exist "%NETS%\%NET%.nnue" (
    echo %NETS%\%NET%.nnue isn't there.
    pause
    exit /b 1
)
set OLDFILE=
if exist "%NETS%\%OLD%.nnue" set OLDFILE=%NETS%\%OLD%.nnue
if "%OLDFILE%"=="" if exist bot\eval\eval-%OLD%.txt set OLDFILE=%CD%\bot\eval\eval-%OLD%.txt
if "%OLDFILE%"=="" if exist "%MAIN%\games\tune\eval-%OLD%.txt" set OLDFILE=%MAIN%\games\tune\eval-%OLD%.txt
if "%OLDFILE%"=="" (
    echo no evaluation called %OLD%: looked for %NETS%\%OLD%.nnue, bot\eval\eval-%OLD%.txt and games\tune\eval-%OLD%.txt.
    pause
    exit /b 1
)
for %%f in ("%OLDFILE%") do set OLDCOPY=%%~nxf
if exist "%PGN%" (
    echo %PGN% is there already: rename it ^(and its .log^) to play %TAG% again.
    pause
    exit /b 1
)

for %%p in (Engine Match) do (
    dotnet build src\ChessBruteforcer.%%p -c Release --nologo -v q
    if errorlevel 1 (
        echo build of %%p failed
        pause
        exit /b 1
    )
)
if exist "%COPY%" rmdir /s /q "%COPY%"
if exist "%COPY%" (
    echo %COPY% is in use: is another match running?
    pause
    exit /b 1
)
xcopy /e /i /q /y src\ChessBruteforcer.Engine\bin\Release\net8.0 "%COPY%\engine" >nul
xcopy /e /i /q /y src\ChessBruteforcer.Match\bin\Release\net8.0 "%COPY%\match" >nul
copy /y "%NETS%\%NET%.nnue" "%COPY%\%NET%.nnue" >nul
copy /y "%OLDFILE%" "%COPY%\%OLDCOPY%" >nul

rem What each engine says it loaded, before any game.
echo %date% %time%: %NET% (%NETS%\%NET%.nnue) v %OLD% (%OLDFILE%), from %CD% > "%MLOG%"
for %%e in (%NET%.nnue %OLDCOPY%) do (
    (echo uci& echo setoption name EvalFile value %COPY%\%%e& echo isready& echo quit) | dotnet "%COPY%\engine\ChessBruteforcer.Engine.dll" | findstr /c:"evaluation" >> "%MLOG%"
)
certutil -hashfile "%COPY%\%NET%.nnue" SHA256 | findstr /v /c:"hash" >> "%MLOG%"

echo %date% %time%: match %NET% v %OLD%, %GAMES% games, %CONC% at a time (progress in %MLOG%)
start "" /b /wait /belownormal dotnet "%COPY%\match\ChessBruteforcer.Match.dll" ^
    --engine name=%NET% cmd="dotnet %COPY%\engine\ChessBruteforcer.Engine.dll" option.EvalFile=%COPY%\%NET%.nnue ^
    --engine name=%OLD% cmd="dotnet %COPY%\engine\ChessBruteforcer.Engine.dll" option.EvalFile=%COPY%\%OLDCOPY% ^
    --games %GAMES% --tc 10+0.1 --concurrency %CONC% --tables "%MAIN%\tables" --sprt 0 10 ^
    --openings "%MAIN%\games\tune\openings-300.txt" --pgn "%PGN%" >> "%MLOG%" 2>&1
echo %date% %time%: finished, exit %errorlevel% >> "%MLOG%"
echo %date% %time%: finished; the last lines of %MLOG%:
powershell -NoProfile -Command "Get-Content '%MLOG%' -Tail 8"
pause
