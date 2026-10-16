@echo off
rem The trainer's Python environment (NNUE.md, session 2): scripts\nnue\.venv (ignored by git) with PyTorch
rem installed from the wheel already downloaded, so pip fetches only torch's small dependencies (~10 MB) and
rem nothing big goes over the household's line uncapped. numpy comes from the system Python (--system-site-packages).
rem   scripts\nnue\setup-venv.cmd [wheel]
rem The wheel defaults to games\nnue\wheels\ in the main checkout (download it with curl --limit-rate 500k first).
rem The base Python is the one on PATH with numpy (C:\Program Files\Python314; `py -3.14` finds another, without
rem numpy); CB_PYTHON=<python.exe> picks a different one.
setlocal
cd /d "%~dp0"
if "%CB_PYTHON%"=="" set CB_PYTHON=C:\Program Files\Python314\python.exe
set WHEEL=%1
if "%WHEEL%"=="" set WHEEL=C:\Users\Admin\Desktop\-=Programming=-\Chess Bruteforcer\games\nnue\wheels\torch-2.14.1+cu132-cp314-cp314-win_amd64.whl
if not exist "%WHEEL%" (
    echo %WHEEL% isn't there.
    exit /b 1
)
if not exist .venv\Scripts\python.exe "%CB_PYTHON%" -m venv --system-site-packages .venv
if errorlevel 1 exit /b 1
.venv\Scripts\python.exe -m pip install --no-cache-dir "%WHEEL%"
if errorlevel 1 exit /b 1
.venv\Scripts\python.exe -c "import torch; print(torch.__version__, torch.cuda.is_available(), torch.cuda.get_device_name(0) if torch.cuda.is_available() else '')"
