# Start the project's long-running jobs, each in its own window, leaving alone any that are running already
# (two bots would be rate-limited by Lichess, and two queues would download the same month at once).
#
#   start.cmd                       (in the repo's folder: double-click it, or run it in a terminal)
#   pwsh -File scripts\start.ps1 -DryRun     (what it would start, starting nothing)
#
# The jobs: the bot (bot\start-bot.cmd: rebuilds the engine copy and the config, then plays on Lichess), the
# month queue (scripts\month-queue.cmd: every Lichess month onto the desktop share, counted for the book) and
# the book learner (bot\learn.cmd: studies each finished game's out-of-book positions into bot\books\ours.bin).
param([switch] $DryRun)
Set-Location (Split-Path $PSScriptRoot -Parent)

$jobs = @(
    @{ Name = 'the bot';         Start = 'bot\start-bot.cmd';         Running = @('*lichess-bot.py*', '*start-bot.cmd*') },
    @{ Name = 'the month queue'; Start = 'scripts\month-queue.cmd';   Running = @('*month-queue.ps1*', '*month-queue.cmd*') },
    @{ Name = 'the book learner'; Start = 'bot\learn.cmd';            Running = @('*learn.py*', '*learn.cmd*') }
)
$processes = Get-CimInstance Win32_Process | Where-Object { $_.CommandLine }
foreach ($job in $jobs) {
    $found = @($processes | Where-Object { $line = $_.CommandLine; ($job.Running | Where-Object { $line -like $_ }).Count -gt 0 })
    if ($found.Count -gt 0) {
        $since = ($found | Sort-Object CreationDate | Select-Object -First 1).CreationDate
        '{0}: already running (since {1:ddd HH:mm}), left alone' -f $job.Name, $since
    } elseif ($DryRun) {
        "$($job.Name): would start $($job.Start)"
    } else {
        Start-Process -FilePath $job.Start -WorkingDirectory (Get-Location) | Out-Null
        "$($job.Name): started ($($job.Start), its own window)"
    }
}

# Where things stand.
if (Test-Path games\month-queue.log) { 'month queue, last line: ' + (Get-Content games\month-queue.log -Tail 1) }
$downloads = Get-ChildItem games\download-*.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if ($downloads) { "$($downloads.Name), last line: " + (Get-Content $downloads.FullName -Tail 1) }
'C: has {0:N1} GB free' -f ((Get-PSDrive C).Free / 1GB)
