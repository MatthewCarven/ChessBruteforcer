@echo off
rem Start everything that runs in the background, each in its own window: the bot and the month queue.
rem Anything already running is left alone.  Double-click it, or run it in a terminal.  (scripts\start.ps1)
cd /d "%~dp0"
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\start.ps1 %*
pause
