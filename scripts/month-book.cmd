@echo off
rem One more month for the opening book: download it (or finish downloading it), count it, and rebuild
rem the book from every month counted so far.
rem   scripts\month-book.cmd 2026-08
rem The download is games\lichess_db_standard_rated_<month>.pgn.zst (about 30 GB, about 10 hours here;
rem it resumes if stopped).  The count goes to bot\books\counts\<month>.counts (about 50 MB, about an hour),
rem and the books to bot\books\lichess-2200.bin and lichess-2200-wide.bin, which a running bot picks up at
rem its next move.  Once a month is counted its .zst isn't needed for the books any more (Matthew's to delete).
rem Progress: games\download-<month>.log, then bot\books\month-book-<month>.log.
setlocal
set MONTH=%1
if "%MONTH%"=="" (
    echo usage: scripts\month-book.cmd 2026-08
    exit /b 1
)
cd /d "%~dp0.."
set PY=bot\lichess-bot\.venv\Scripts\python.exe
set ZST=games\lichess_db_standard_rated_%MONTH%.pgn.zst
set COUNTS=bot\books\counts\%MONTH%.counts
set LOG=bot\books\month-book-%MONTH%.log
if not exist bot\books\counts mkdir bot\books\counts

if exist %COUNTS% goto book
if exist %ZST% goto count
if exist %ZST%.part goto download
for /f %%f in ('powershell -NoProfile -Command "[math]::Floor((Get-PSDrive C).Free/1GB)"') do set FREE=%%f
if %FREE% LSS 32 (
    echo Only %FREE% GB free on C:, and a month is about 30 GB.  Free some space first.
    pause
    exit /b 1
)

:download
echo %date% %time%: downloading %MONTH% (progress in games\download-%MONTH%.log)
where pwsh >nul 2>&1
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\download-lichess-month.ps1 %MONTH%
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\download-lichess-month.ps1 %MONTH%
)
if not exist %ZST% (
    echo The download didn't finish: run this again to carry on.
    pause
    exit /b 1
)

:count
echo %date% %time%: counting %ZST% (about an hour; progress in %LOG%) >> %LOG%
echo %date% %time%: counting %ZST% (about an hour; progress in %LOG%)
"%PY%" -I scripts\make-book.py count %ZST% %COUNTS% >> %LOG% 2>&1
if not exist %COUNTS% (
    echo Counting failed: see %LOG%
    pause
    exit /b 1
)

:book
echo %date% %time%: rebuilding the book from every counts file >> %LOG%
"%PY%" -I scripts\make-book.py book bot\books\lichess-2200.bin "bot\books\counts\*.counts" >> %LOG% 2>&1
rem The wide book, which the bot falls back on where the main one has nothing: moves played 3+ times, not 10+.
"%PY%" -I scripts\make-book.py book bot\books\lichess-2200-wide.bin "bot\books\counts\*.counts" --min-games 3 >> %LOG% 2>&1
type %LOG% | findstr /c:"book:"
echo %date% %time%: finished >> %LOG%
echo %date% %time%: finished.  %ZST% can go now if you like (the counts are kept).
pause
