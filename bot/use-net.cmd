@echo off
rem Which net the bot's next games play on (Matthew's (a), 2026-10-25): writes bot\eval\net.txt, which lichess-bot
rem reads as each game starts (bot\lichess-bot-net-file.patch), so no restart; games on now keep their net. A name is
rem a net in bot\eval or a full path; "default" empties the file, for make-config.py's choice (the first net on its
rem list that's in bot\eval). With no name: the nets in bot\eval and what net.txt says. The bot's window logs each
rem game's net, and says so when a net is cut or damaged (the config's net plays then).
rem   bot\use-net.cmd h512-6m-l32.nnue
rem   bot\use-net.cmd default
rem   bot\use-net.cmd
setlocal
set "NETS=%~dp0eval"
if "%~1"=="" goto show
if /i "%~1"=="default" (
    type nul > "%NETS%\net.txt"
    echo From the next game on: make-config.py's net ^(net.txt is empty^).
    goto end
)
set "NET=%~1"
if not exist "%NETS%\%NET%" if not exist "%NET%" (
    echo There's no %NET% in %NETS%.
    goto show
)
> "%NETS%\net.txt" echo %NET%
echo From the next game on: %NET% ^(games on now keep theirs^).
goto end
:show
echo Nets in %NETS%:
dir /b "%NETS%\*.nnue"
if exist "%NETS%\net.txt" (
    echo net.txt says:
    type "%NETS%\net.txt"
) else (
    echo No net.txt: make-config.py's net.
)
:end
endlocal
