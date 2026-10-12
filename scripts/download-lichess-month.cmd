@echo off
rem Download, or finish downloading, a month of Lichess games (see download-lichess-month.ps1).
rem   scripts\download-lichess-month.cmd 2026-09
cd /d "%~dp0.."
where pwsh >nul 2>&1
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0download-lichess-month.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0download-lichess-month.ps1" %*
)
pause
