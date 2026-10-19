@echo off
rem Train a network (NNUE.md, session 2) in its own window:
rem   scripts\nnue-train.cmd nnue1 [train.py train options]
rem PyTorch from scripts\nnue\.venv (scripts\nnue\setup-venv.cmd makes it). Reads games\nnue\2026-09 of the main
rem checkout unless --data says otherwise; writes games\nnue\nets\<run>.pt after every pass (--resume carries on
rem from it), <run>.nnue at the end, and <run>.log (train.py's own lines; Python's errors go to <run>.err).
rem Below-normal priority, so the bot comes first; runs a copy of the scripts, so the repo stays editable.
setlocal
set RUN=%1
if "%RUN%"=="" (
    echo usage: scripts\nnue-train.cmd ^<run name^> [train.py train options]
    exit /b 1
)
rem Every option after the run name (%2..%9 would drop a tenth token, as train-desktop.cmd once lost --progress 2);
rem so no quotes in the options.
set OPTIONS=
for /f "tokens=1,*" %%a in ("%*") do set OPTIONS=%%b
cd /d "%~dp0.."
set PY=%CD%\scripts\nnue\.venv\Scripts\python.exe
rem A worktree has no venv of its own: the main checkout's then (games\ is the main checkout's anyway).
if not exist "%PY%" set PY=C:\Users\Admin\Desktop\-=Programming=-\Chess Bruteforcer\scripts\nnue\.venv\Scripts\python.exe
if not exist "%PY%" (
    echo %PY% isn't there: run scripts\nnue\setup-venv.cmd first.
    pause
    exit /b 1
)
set NETS=C:\Users\Admin\Desktop\-=Programming=-\Chess Bruteforcer\games\nnue\nets
set COPY=%TEMP%\cb-nnue-train-%RUN%
if not exist "%NETS%" mkdir "%NETS%"
if not exist "%COPY%\nnue" mkdir "%COPY%\nnue"
if not exist "%COPY%\nnue-reference" mkdir "%COPY%\nnue-reference"
copy /y scripts\nnue\*.py "%COPY%\nnue\" >nul
copy /y scripts\nnue-reference\nnue_reference.py "%COPY%\nnue-reference\" >nul
echo %date% %time%: train %RUN% %OPTIONS%, from %CD% >> "%NETS%\%RUN%.err"
start "" /b /wait /belownormal "%PY%" -u "%COPY%\nnue\train.py" train %RUN% --below-normal %OPTIONS% 2>> "%NETS%\%RUN%.err"
set CODE=%errorlevel%
echo %date% %time%: finished, exit %CODE% >> "%NETS%\%RUN%.err"
echo %date% %time%: finished, exit %CODE%; the last lines of %NETS%\%RUN%.log:
powershell -NoProfile -Command "Get-Content '%NETS%\%RUN%.log' -Tail 8"
pause
