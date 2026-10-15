@echo off
rem One more month for the opening book: download it (or finish downloading it), count it, and rebuild
rem the book from every month counted so far.
rem   scripts\month-book.cmd 2026-08
rem The months are kept on Matthew's desktop (from 2026-10-13), in MONTHS below: set MONTHS first to use
rem another folder (games was the old place).  The download is MONTHS\lichess_db_standard_rated_<month>.pgn.zst
rem (about 30 GB, about 8-10 hours here; it resumes if stopped), with Lichess's SHA-256 beside it.  The count
rem reads it from there (about an hour; it rides out the share dropping for up to 30 minutes at a time, and
rem checks the whole file against the SHA-256) into bot\books\counts\<month>.counts (about 100 MB), and the
rem books go to bot\books\lichess-2200.bin and lichess-2200-wide.bin, which a running bot picks up at its
rem next move.  Counts and books stay on this machine, so the bot never needs the desktop.  The months
rem stay on the desktop for when we want to count them some other way.
rem Progress: games\download-<month>.log, then bot\books\month-book-<month>.log.
setlocal
set MONTH=%1
if "%MONTH%"=="" (
    echo usage: scripts\month-book.cmd 2026-08
    exit /b 1
)
cd /d "%~dp0.."
if not defined MONTHS set MONTHS=\\id10terror-desktop\chessbruteforcer\lichess
set PY=bot\lichess-bot\.venv\Scripts\python.exe
set ZST=%MONTHS%\lichess_db_standard_rated_%MONTH%.pgn.zst
set COUNTS=bot\books\counts\%MONTH%.counts
set LOG=bot\books\month-book-%MONTH%.log
if not exist bot\books\counts mkdir bot\books\counts

if exist "%COUNTS%" goto book

rem The desktop may be asleep or the network busy for a moment: wait up to 10 minutes for it.
set TRIES=0
:reach
if exist "%MONTHS%\" goto reached
set /a TRIES+=1
if %TRIES% GTR 20 (
    echo Can't reach %MONTHS% after 10 minutes: is the desktop on?
    pause
    exit /b 1
)
if %TRIES%==1 echo Waiting for %MONTHS% ...
timeout /t 30 /nobreak >nul
goto reach
:reached

if exist "%ZST%" goto count
if exist "%ZST%.part" goto download
for /f %%f in ('%PY% -c "import shutil, sys; print(shutil.disk_usage(sys.argv[1]).free // 2**30)" "%MONTHS%"') do set FREE=%%f
if %FREE% LSS 32 (
    echo Only %FREE% GB free in %MONTHS%, and a month is about 30 GB.  Free some space first.
    pause
    exit /b 1
)

:download
echo %date% %time%: downloading %MONTH% to %MONTHS% (progress in games\download-%MONTH%.log)
where pwsh >nul 2>&1
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\download-lichess-month.ps1 %MONTH% -Dir "%MONTHS%"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\download-lichess-month.ps1 %MONTH% -Dir "%MONTHS%"
)
if not exist "%ZST%" (
    echo The download didn't finish: run this again to carry on.
    pause
    exit /b 1
)

:count
echo %date% %time%: counting %ZST% (about an hour; progress in %LOG%) >> %LOG%
echo %date% %time%: counting %ZST% (about an hour; progress in %LOG%)
"%PY%" -I scripts\make-book.py count "%ZST%" %COUNTS% >> %LOG% 2>&1
if not exist "%COUNTS%" (
    echo Counting failed: see %LOG%
    pause
    exit /b 1
)

:book
echo %date% %time%: rebuilding the book from every counts file >> %LOG%
"%PY%" -I scripts\make-book.py book bot\books\lichess-2200.bin "bot\books\counts\*.counts" >> %LOG% 2>&1
rem The wide book, which the bot falls back on where the main one has nothing: moves played 3+ times, not 10+.
"%PY%" -I scripts\make-book.py book bot\books\lichess-2200-wide.bin "bot\books\counts\*.counts" --min-games 3 >> %LOG% 2>&1
type %LOG% | findstr /c:"book:" /c:"SHA-256" /c:"dropped out"
echo %date% %time%: finished >> %LOG%
echo %date% %time%: finished.  The month stays in %MONTHS%; its counts are in %COUNTS%.
pause
