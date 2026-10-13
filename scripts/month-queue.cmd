@echo off
rem Every Lichess month onto the desktop share, newest first, each counted for the book (or checked, if counted
rem before): scripts\month-queue.ps1 says how.  Closing the window stops it; running it again carries on.
rem   scripts\month-queue.cmd            scripts\month-queue.cmd -DryRun
cd /d "%~dp0.."
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\month-queue.ps1 %*
pause