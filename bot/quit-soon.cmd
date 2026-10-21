@echo off
rem Stop the bot once its games are over (Matthew, 2026-10-20): makes bot\quit.soon. lichess-bot then declines new
rem challenges ("later"), ends the games still on with "Good game! Sorry, no new challenges at this time: we're going
rem to sleep, and when we wake up we'll be even better!", and stops when none is left; run-bot.ps1 then deletes the
rem file and starts nothing. Deleting bot\quit.soon before that cancels. bot\start-bot.cmd starts the bot again.
rem   bot\quit-soon.cmd
type nul > "%~dp0quit.soon"
echo The bot stops once its games are over (bot\quit.soon is there; delete it to cancel).
timeout /t 10
