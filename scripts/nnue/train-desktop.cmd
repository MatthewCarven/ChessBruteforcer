@echo off
rem Train a network on the desktop's GPU (NNUE.md, session 2b). Lives in the share's nnue folder, i.e. on the desktop
rem   D:\-=Chess Bruteforcer=-\nnue\train-desktop.cmd <run name> [train.py train options]
rem and is started there by a scheduled task (scripts\nnue-desktop.ps1 train, from the laptop), since a program
rem started over SSH dies with the connection. Everything it touches is on the desktop's own disk, beside it:
rem   .venv\              PyTorch (the laptop's wheel, copied over the share; NNUE.md has the set-up)
rem   scripts\nnue\, scripts\nnue-reference\   the trainer, as scripts\nnue-desktop.ps1 sync copied it
rem   2026-09\            the records (--data; a later option on the line wins)
rem   nets\               <run>.log (train.py's own lines), <run>.err (Python's errors, start and finish), <run>.pt
rem                       after every pass (--resume), <run>.nnue at the end; <run>.pause and <run>.duty steer it
rem   runs\<run>\         the copy of the scripts this run uses, so the share's can be updated meanwhile
rem The laptop follows a run in \\id10terror-desktop\chessbruteforcer\nnue\nets\<run>.log. Below-normal priority.
setlocal
set RUN=%1
if "%RUN%"=="" (
    echo usage: train-desktop.cmd ^<run name^> [train.py train options]
    exit /b 1
)
rem Every option after the run name (%2..%9 would drop a ninth token: --progress 2 was lost that way once).
set OPTIONS=
for /f "tokens=1,*" %%a in ("%*") do set OPTIONS=%%b
set ROOT=%~dp0
set PY=%ROOT%.venv\Scripts\python.exe
set NETS=%ROOT%nets
set COPY=%ROOT%runs\%RUN%
if not exist "%NETS%" mkdir "%NETS%"
if not exist "%PY%" (
    echo %date% %time%: %PY% isn't there: make the venv first ^(NNUE.md, session 2b^) >> "%NETS%\%RUN%.err"
    exit /b 1
)
if not exist "%COPY%\nnue" mkdir "%COPY%\nnue"
if not exist "%COPY%\nnue-reference" mkdir "%COPY%\nnue-reference"
copy /y "%ROOT%scripts\nnue\*.py" "%COPY%\nnue\" >nul
copy /y "%ROOT%scripts\nnue-reference\nnue_reference.py" "%COPY%\nnue-reference\" >nul
echo %date% %time%: train %RUN% %OPTIONS%, on %COMPUTERNAME% >> "%NETS%\%RUN%.err"
start "" /b /wait /belownormal "%PY%" -u "%COPY%\nnue\train.py" train %RUN% --data "%ROOT%2026-09" --out "%NETS%" --below-normal %OPTIONS% >nul 2>> "%NETS%\%RUN%.err"
set CODE=%errorlevel%
echo %date% %time%: finished, exit %CODE% >> "%NETS%\%RUN%.err"
exit /b %CODE%
