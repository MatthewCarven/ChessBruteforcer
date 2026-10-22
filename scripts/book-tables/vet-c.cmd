@echo off
rem BT1 (c): vetting the human book's Black lines, in a window of its own (vet-c-search.py, BOOK-TABLES.md).
rem   scripts\book-tables\vet-c.cmd <engine exe, a copy of a Release build> <engines at once>
rem First the screen: tiers 1-2 of results\vet-c-positions.csv at depth 16 (book moves after the move at 15), into
rem results\vet-c-screen.csv. Then the confirm: the moves 50 cp or more behind there, at depth 18 (17), into
rem results\vet-c-confirm.csv. Both append and skip what's done, so stopping the window and starting it again
rem carries on. Logs beside the .csv files (.log).
setlocal
set HERE=%~dp0
for /f "delims=" %%g in ('git -C "%HERE%." rev-parse --path-format^=absolute --git-common-dir') do set GITDIR=%%g
for %%m in ("%GITDIR%\..") do set MAIN=%%~fm
set PY=%MAIN%\bot\lichess-bot\.venv\Scripts\python.exe
if not exist "%PY%" (echo no python at %PY% & exit /b 1)
"%PY%" -I "%HERE%vet-c-search.py" %1 "%HERE%results\vet-c-screen.csv" --depth 16 --tiers 1,2 --workers %2
if errorlevel 1 (echo the screen stopped: see results\vet-c-screen.log & exit /b 1)
"%PY%" -I "%HERE%vet-c-search.py" %1 "%HERE%results\vet-c-confirm.csv" --depth 18 --tiers 1,2 --workers %2 --confirm "%HERE%results\vet-c-screen.csv"
if errorlevel 1 (echo the confirm stopped: see results\vet-c-confirm.log & exit /b 1)
echo vet-c finished %date% %time%
