# Keep lichess-bot running through network trouble (Matthew, 2026-10-14: he moves between the upstairs and
# downstairs wireless networks). bot\start-bot.cmd builds, configures and then runs this.
#
# lichess-bot rides out short drops by itself: each request to Lichess is retried for 60 s, a game's stream
# for 10 minutes, and a network error at startup restarts it after 10 s. What it doesn't survive is any other
# fault: it logs "Quitting lichess-bot due to an error" and stops, and the bot would stay off until noticed.
# So this runs it in a loop: whenever it stops on its own, wait until lichess.org answers, then start it
# again. Ctrl+C stops the whole thing (lichess-bot and this loop: both run in this window, so both get it). If it
# stops 5 times running within 2 minutes of starting, something isn't network (the token, say): this gives up and
# says so. Every start and stop also goes to bot\logs\restarts.log, a Ctrl+C included (2026-10-20: four stops by a
# held Ctrl+C, with no line here, were taken for crashes).
$ErrorActionPreference = 'Continue'
$here = $PSScriptRoot
$py = Join-Path $here 'lichess-bot\.venv\Scripts\python.exe'
$logs = Join-Path $here 'logs'
$restarts = Join-Path $logs 'restarts.log'
New-Item -ItemType Directory -Force $logs | Out-Null

function Note([string] $text) {
    $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $text
    Write-Host $line
    Add-Content -Path $restarts -Value $line
}

function Lichess-Answers {
    try {
        Invoke-WebRequest -Uri 'https://lichess.org/api/user/matthewcarven' -UseBasicParsing -TimeoutSec 10 -Method Head | Out-Null
        return $true
    } catch {
        return $false
    }
}

Set-Location (Join-Path $here 'lichess-bot')
$quick = 0
$running = $null  # when the lichess-bot running now started, if one is
$byCtrlC = $true  # the loop only ends by Ctrl+C, unless it gives up
try {
    while ($true) {
        if (-not (Lichess-Answers)) {
            Note 'lichess.org not answering: waiting for the network'
            $since = Get-Date
            while (-not (Lichess-Answers)) { Start-Sleep -Seconds 10 }
            Note ('the network is back after {0:N0} s' -f ((Get-Date) - $since).TotalSeconds)
        }
        $started = Get-Date
        $logFile = Join-Path $logs ('bot-{0:yyyy-MM-dd}.log' -f $started)
        Note 'starting lichess-bot'
        $running = $started
        & $py lichess-bot.py -l $logFile
        $code = $LASTEXITCODE
        $running = $null
        $ran = (Get-Date) - $started
        if ($ran.TotalMinutes -lt 2) { $quick++ } else { $quick = 0 }
        if ($quick -ge 5) {
            Note "lichess-bot stopped 5 times within 2 minutes of starting (exit $code): giving up. Its log: $logFile"
            $byCtrlC = $false
            exit 1
        }
        Note ('lichess-bot stopped by itself (exit {0}) after {1:N0} min: starting it again in 20 s (Ctrl+C to stop for good)' -f $code, $ran.TotalMinutes)
        Start-Sleep -Seconds 20
    }
} finally {
    # PowerShell runs this when Ctrl+C stops the script.
    if ($byCtrlC) {
        if ($running) {
            Note ('lichess-bot stopped by Ctrl+C after {0:N0} min: not starting it again (bot\start-bot.cmd does)' -f ((Get-Date) - $running).TotalMinutes)
        } else {
            Note 'stopped by Ctrl+C between runs of lichess-bot: not starting it again (bot\start-bot.cmd does)'
        }
    }
}
