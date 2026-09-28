@echo off
rem Solves every 5-piece table without pawns (see build-five-piece.ps1).
rem Double-click to start; run it again to carry on after a stop.
cd /d "%~dp0.."
where pwsh >nul 2>&1
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-five-piece.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-five-piece.ps1" %*
)
pause
