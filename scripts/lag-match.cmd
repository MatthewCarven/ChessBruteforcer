@echo off
rem MoveLag against none, with every move charged a lag as Lichess charges the bot's round trip (TODO, "How the bot
rem uses its clock when time runs low", Matthew's (b)), in its own window:
rem   scripts\lag-match.cmd <clock> <lag ms> [games] [concurrency] [MoveLag ms] [Move Overhead ms]
rem e.g. scripts\lag-match.cmd 12+0.2 80   (the bot's 1+1 scaled 5x down: 0.4 s of lag a move becomes 80 ms).
rem Both sides are the same build and net (LAG_NET, else h1536-3m-ob8) with the same Move Overhead (420 by default:
rem online the engine is told its clock less lichess-bot's 2,000 ms and keeps its own 100, scaled 5x down); "movelag"
rem has MoveLag set (to the lag, by default), "none" hasn't. The match tool charges both the lag (--lag) on every
rem move. Played as nnue-match.cmd plays: the 300 book openings in colour-reversed pairs, our tables adjudicating,
rem 1,000 games by default and no SPRT (the time losses are wanted as much as the score), 5 at a time, below-normal
rem priority. The engine and match tool are copied to %TEMP%\cb-lag first, so the repo stays buildable.
rem Logs: games\lag\lag-<clock>-<lag>ms.log and .pgn in the main checkout; the summary counts each side's time losses.
setlocal
set TC=%1
set LAG=%2
set GAMES=%3
set CONC=%4
set MOVELAG=%5
set OVERHEAD=%6
if "%LAG%"=="" (
    echo usage: scripts\lag-match.cmd ^<clock^> ^<lag ms^> [games] [concurrency] [MoveLag ms] [Move Overhead ms]
    exit /b 1
)
if "%GAMES%"=="" set GAMES=1000
if "%CONC%"=="" set CONC=5
if "%MOVELAG%"=="" set MOVELAG=%LAG%
if "%OVERHEAD%"=="" set OVERHEAD=420
set NET=%LAG_NET%
if "%NET%"=="" set NET=h1536-3m-ob8
cd /d "%~dp0.."
rem The main checkout: the folder above git's common directory (this checkout itself when it isn't a worktree).
for /f "delims=" %%g in ('git rev-parse --git-common-dir') do set GITDIR=%%g
for %%m in ("%GITDIR%\..") do set MAIN=%%~fm
set NETFILE=%MAIN%\games\nnue\nets\%NET%.nnue
set TAG=lag-%TC%-%LAG%ms
if not "%MOVELAG%"=="%LAG%" set TAG=%TAG%-movelag%MOVELAG%
if not "%GAMES%"=="1000" set TAG=%TAG%-%GAMES%
if not exist "%MAIN%\games\lag" mkdir "%MAIN%\games\lag"
set MLOG=%MAIN%\games\lag\%TAG%.log
set PGN=%MAIN%\games\lag\%TAG%.pgn
set COPY=%TEMP%\cb-lag

if not exist "%NETFILE%" (
    echo %NETFILE% isn't there.
    pause
    exit /b 1
)
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
    echo %COPY% is in use: is another lag match running?
    pause
    exit /b 1
)
xcopy /e /i /q /y src\ChessBruteforcer.Engine\bin\Release\net8.0 "%COPY%\engine" >nul
xcopy /e /i /q /y src\ChessBruteforcer.Match\bin\Release\net8.0 "%COPY%\match" >nul
copy /y "%NETFILE%" "%COPY%\%NET%.nnue" >nul

rem What the engine says it offers and loaded, before any game.
echo %date% %time%: movelag (MoveLag %MOVELAG%) v none, %NET%, Move Overhead %OVERHEAD%, lag %LAG% ms, from %CD% > "%MLOG%"
(echo uci& echo setoption name EvalFile value %COPY%\%NET%.nnue& echo isready& echo quit) | dotnet "%COPY%\engine\ChessBruteforcer.Engine.dll" | findstr /c:"MoveLag" /c:"evaluation" >> "%MLOG%"

echo %date% %time%: lag match at %TC%, %LAG% ms a move, %GAMES% games, %CONC% at a time (progress in %MLOG%)
start "" /b /wait /belownormal dotnet "%COPY%\match\ChessBruteforcer.Match.dll" ^
    --engine name=movelag cmd="dotnet %COPY%\engine\ChessBruteforcer.Engine.dll" option.EvalFile=%COPY%\%NET%.nnue "option.Move Overhead=%OVERHEAD%" option.MoveLag=%MOVELAG% ^
    --engine name=none cmd="dotnet %COPY%\engine\ChessBruteforcer.Engine.dll" option.EvalFile=%COPY%\%NET%.nnue "option.Move Overhead=%OVERHEAD%" ^
    --games %GAMES% --tc %TC% --lag %LAG% --concurrency %CONC% --tables "%MAIN%\tables" ^
    --openings "%MAIN%\games\tune\openings-300.txt" --pgn "%PGN%" >> "%MLOG%" 2>&1
echo %date% %time%: finished, exit %errorlevel% >> "%MLOG%"
powershell -NoProfile -Command "$l = Get-Content '%MLOG%'; foreach ($s in 'movelag', 'none') { $n = @($l | Select-String -SimpleMatch \"$s lost on time\").Count; \"time losses, $s`: $n\" }" >> "%MLOG%"
echo %date% %time%: finished; the last lines of %MLOG%:
powershell -NoProfile -Command "Get-Content '%MLOG%' -Tail 8"
pause
