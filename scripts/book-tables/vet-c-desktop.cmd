@echo off
rem BT1 (c) on the desktop's CPU: vet-c-desktop.cmd <engines>, run from the share's book-vet-c\ folder by the scheduled
rem task scripts\book-tables\vet-c-desktop.ps1 starts (a program started over SSH dies with the connection). The
rem screen (depth 16) then the confirm (depth 18), as vet-c.cmd on the laptop; results in
rem scripts\book-tables\results\, each search's log beside them, anything else (a traceback) in run.log. Started
rem again, it carries on where it was.
setlocal
set HERE=%~dp0
set PY=C:\Program Files\Python314\python.exe
echo %date% %time% start, %1 engines >> "%HERE%run.log"
"%PY%" -I -u "%HERE%scripts\book-tables\vet-c-search.py" "%HERE%engine\ChessBruteforcer.Engine.exe" "%HERE%scripts\book-tables\results\vet-c-screen.csv" --depth 16 --tiers 1,2 --workers %1 >> "%HERE%run.log" 2>&1
if errorlevel 1 (echo %date% %time% the screen stopped >> "%HERE%run.log" & exit /b 1)
"%PY%" -I -u "%HERE%scripts\book-tables\vet-c-search.py" "%HERE%engine\ChessBruteforcer.Engine.exe" "%HERE%scripts\book-tables\results\vet-c-confirm.csv" --depth 18 --tiers 1,2 --workers %1 --confirm "%HERE%scripts\book-tables\results\vet-c-screen.csv" >> "%HERE%run.log" 2>&1
if errorlevel 1 (echo %date% %time% the confirm stopped >> "%HERE%run.log" & exit /b 1)
echo %date% %time% vet-c finished >> "%HERE%run.log"
