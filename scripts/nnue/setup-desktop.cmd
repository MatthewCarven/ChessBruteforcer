@echo off
rem The desktop's trainer environment (NNUE.md, session 2b): .venv beside this file in the share's nnue folder
rem (D:\-=Chess Bruteforcer=-\nnue on the desktop), made from files already copied over the share, so nothing is
rem downloaded:
rem   wheels\torch-2.14.1+cu132-cp314-cp314-win_amd64.whl   the laptop's wheel (games\nnue\wheels\)
rem   wheels\torch-deps-cp314-win_amd64.zip                  torch's seven small dependencies as installed in the
rem                                                          laptop's venv (filelock, fsspec, jinja2, markupsafe,
rem                                                          mpmath, networkx, sympy; the same Python, 3.14.4)
rem numpy, typing-extensions and setuptools come from the desktop's own Python (--system-site-packages).
rem Run it on the desktop (over SSH: ssh Admin@id10terror-desktop "D:\-=Chess Bruteforcer=-\nnue\setup-desktop.cmd").
setlocal
cd /d "%~dp0"
set BASE=C:\Program Files\Python314\python.exe
set WHEEL=wheels\torch-2.14.1+cu132-cp314-cp314-win_amd64.whl
if not exist "%WHEEL%" (
    echo %WHEEL% isn't there.
    exit /b 1
)
if not exist .venv\Scripts\python.exe "%BASE%" -m venv --system-site-packages .venv || exit /b 1
.venv\Scripts\python.exe -I -c "import sysconfig, zipfile; zipfile.ZipFile(r'wheels\torch-deps-cp314-win_amd64.zip').extractall(sysconfig.get_paths()['purelib'])" || exit /b 1
.venv\Scripts\python.exe -m pip install --no-index --no-deps --no-cache-dir "%WHEEL%" || exit /b 1
.venv\Scripts\python.exe -m pip check || exit /b 1
.venv\Scripts\python.exe -c "import torch; print(torch.__version__, torch.cuda.is_available(), torch.cuda.get_device_name(0), torch.cuda.get_arch_list())"
