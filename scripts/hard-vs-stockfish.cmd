@echo off
rem Hard positions from the endgame tables against Stockfish 16 (in WSL): per table, 6 long-DTZ wins,
rem 6 cursed wins and 6 blessed losses, each played twice with our engine on each side.  The same
rem positions (same seed) as the 100 ms runs of 2026-10-09 (scripts\hard-vs-stockfish*.txt).
rem
rem   scripts\hard-vs-stockfish.cmd [ms a move]      default 1000: about 7 hours; plug the laptop in
rem
rem Runs from a copy of the build (tables\.cli-hard) so the repo stays buildable; the results go to
rem tables\hard-vs-stockfish-<ms>ms.txt.  It stops by itself if Stockfish stops answering.
setlocal
set MS=%1
if "%MS%"=="" set MS=1000
cd /d "%~dp0.."
set CHESS_TABLES=%CD%\tables
dotnet build src\ChessBruteforcer.Cli -c Release --nologo -v q
if errorlevel 1 (
    echo build failed
    pause
    exit /b 1
)
if exist tables\.cli-hard rmdir /s /q tables\.cli-hard
xcopy /e /i /q src\ChessBruteforcer.Cli\bin\Release\net8.0 tables\.cli-hard >nul
echo %date% %time%: started, %MS% ms a move, results in tables\hard-vs-stockfish-%MS%ms.txt
dotnet tables\.cli-hard\ChessBruteforcer.Cli.dll hard KBNvK,KQvKR,KBBvKN,KBNvKN,KRBvKR,KQRvKQ,KBBvKQ,KNNvKQ,KRNvKQ,KQNvKQ,KRNvKR,KQPvKQ,KRPvKQ,KRPvKB,KNNvKP,KBPvKN,KRPvKR,KPPvKP 6 "wsl.exe -d Ubuntu-24.04 -- /usr/games/stockfish" %MS% > tables\hard-vs-stockfish-%MS%ms.txt
echo %date% %time%: finished, exit %errorlevel%
pause
