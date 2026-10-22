@echo off
rem BT1 (a)'s leaf searches on the desktop (BOOK-TABLES.md): table-a-desktop.cmd <depth> <engines> [<limit>]
rem The scheduled task scripts\book-tables\table-a-desktop.ps1 starts runs this from the share's booktable\ folder (a
rem program started over SSH dies with the connection). Answers go to d<depth>\results-<n>.tsv and the log to
rem d<depth>.log; started again, it carries on from the answers already there.
setlocal
set HERE=%~dp0
"C:\Program Files\Python314\python.exe" -I -u "%HERE%table-a-search.py" "%HERE%engine\ChessBruteforcer.Engine.exe" "%HERE%h1536-3m.nnue" "%HERE%leaves.tsv" "%HERE%d%1" %1 %2 %3 >> "%HERE%d%1.log" 2>&1
