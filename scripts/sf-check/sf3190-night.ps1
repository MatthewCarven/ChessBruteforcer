# The Stockfish check's half with tables, played out to 500 games in a night (Matthew's (b), 2026-10-26; MATCHES.md).
# Stints of one game at a time (sf3190-night.cmd, each in its own window and log: sf3190-2.log, sf3190-3.log, ...).
# A stint stops when commit free falls under 2 GB or RAM available under 1 GB (the RAM line tripped twice on
# 2026-10-26, from the tables' file-backed pages). Once our engine and Stockfish are gone and RAM available is back
# over 3 GB, the next stint plays the games still missing. It ends at 500 games, at 06:55 (the laptop's heat line),
# on an error in a stint's log, or if RAM isn't back within 5 minutes. On ponder-scope's go, after a quiet check,
# with the bot off; from the PowerShell tool or pwsh (not powershell 5.1):
#   pwsh -File scripts\sf-check\sf3190-night.ps1 [-Target 500] [-DryRun]
# -DryRun says what it would start and starts nothing. Everything it prints also goes to games\sf-check\sf3190-night.log.
param([int]$Target = 500, [switch]$DryRun)

$main = Split-Path (git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir)
$dir = Join-Path $main 'games\sf-check'
$out = Join-Path $dir 'sf3190.jsonl'
$py = Join-Path $main 'bot\lichess-bot\.venv\Scripts\python.exe'
$launcher = Join-Path $PSScriptRoot 'sf3190-night.cmd'
$nightLog = Join-Path $dir 'sf3190-night.log'

function Say($text) { $line = "$(Get-Date -Format 'HH:mm:ss') $text"; $line; if (-not $DryRun) { Add-Content $nightLog $line } }
function Played { if (Test-Path $out) { @(Get-Content $out | Where-Object { $_.Trim() }).Count } else { 0 } }
function LastLineWhole { if (-not (Test-Path $out)) { return $true }; try { $null = Get-Content $out -Tail 1 | ConvertFrom-Json; $true } catch { $false } }
function CommitFree { (Get-CimInstance Win32_OperatingSystem).FreeVirtualMemory / 1MB }
function RamAvailable { (Get-Counter '\Memory\Available MBytes').CounterSamples[0].CookedValue }
function PastHeatLine { $n = Get-Date; ($n.Hour -eq 6 -and $n.Minute -ge 55) -or ($n.Hour -ge 7 -and $n.Hour -lt 12) }
# The match's processes: book-match.py on this file, our engine (any but the learner's, so the bot's too) and Stockfish.
function MatchProcesses {
    @(Get-CimInstance Win32_Process | Where-Object {
        ($_.Name -eq 'python.exe' -and $_.CommandLine -match 'sf3190\.jsonl') -or
        ($_.Name -eq 'ChessBruteforcer.Engine.exe' -and $_.ExecutablePath -notmatch '\\learner\\') -or
        ($_.Name -eq 'wsl.exe' -and $_.CommandLine -match 'stockfish') })
}
function NextLog { $n = 2; while (Test-Path (Join-Path $dir "sf3190-$n.log")) { $n++ }; "sf3190-$n.log" }
function LogErrors($path) { if (Test-Path $path) { @(Select-String -Path $path -Pattern 'Traceback|Error|Exception' -CaseSensitive) } else { @() } }

if (-not (Test-Path $py)) { Say "no python at $py"; exit 1 }
$minFree = 99; $minRam = 99999
$stint = 0
while ($true) {
    $done = Played
    if ($done -ge $Target) { Say "all $Target played"; break }
    if (PastHeatLine) {
        if (-not $DryRun) { Say "06:55, the heat line: stopping at $done of $Target"; break }
        Say "(past 06:55, the heat line: a real run would stop here)"
    }
    if (-not (LastLineWhole)) { Say "the last line of $out isn't whole: holding"; exit 1 }
    # Our engine and Stockfish gone, and RAM back, before a stint: 5 minutes at most.
    $deadline = (Get-Date).AddMinutes(5)
    while ($true) {
        $busy = MatchProcesses; $ram = RamAvailable; $free = CommitFree
        if ($busy.Count -eq 0 -and $ram -ge 3000 -and $free -ge 4) { break }
        if ($DryRun -or (Get-Date) -gt $deadline) {
            $what = if ($busy.Count) { 'still running: ' + (($busy | ForEach-Object { "$($_.Name) $($_.ProcessId)" }) -join ', ') } else { 'nothing running' }
            Say ("not starting: {0}; RAM available {1:N0} MB, commit free {2:N2} GB" -f $what, $ram, $free)
            exit 1
        }
        Start-Sleep 10
    }
    $left = $Target - $done
    $log = NextLog
    if ($DryRun) {
        Say ("would start: $launcher $left $log ({0} played; RAM available {1:N0} MB, commit free {2:N2} GB)" -f $done, $ram, $free)
        exit 0
    }
    $stint++
    Say ("stint {0}: {1} games from game {2}, log {3}; RAM available {4:N0} MB, commit free {5:N2} GB" -f $stint, $left, $done, $log, $ram, $free)
    $p = Start-Process -FilePath cmd.exe -ArgumentList '/c', "`"$launcher`" $left $log" -WorkingDirectory $main -PassThru
    $logPath = Join-Path $dir $log
    Start-Sleep 10
    $why = $null
    while (Get-Process -Id $p.Id -ErrorAction SilentlyContinue) {
        $free = CommitFree; $ram = RamAvailable
        if ($free -lt $minFree) { $minFree = $free }; if ($ram -lt $minRam) { $minRam = $ram }
        if ($free -lt 2) { $why = "commit free {0:N2} GB" -f $free }
        elseif ($ram -lt 1000) { $why = "RAM available {0:N0} MB" -f $ram }
        elseif (PastHeatLine) { $why = "06:55, the heat line" }
        if ($why) { Say "stint ${stint} stopped: $why"; taskkill /T /F /PID $p.Id | Out-Null; break }
        if ((LogErrors $logPath).Count) { taskkill /T /F /PID $p.Id | Out-Null; break }
        Start-Sleep 20
    }
    Say "stint ${stint} ended: $(Played) played"
    $errors = LogErrors $logPath
    if ($errors.Count) { Say "an error in ${log}: holding"; $errors | Select-Object -First 5 | ForEach-Object { Say $_.Line }; exit 1 }
    if (-not $why -and (Played) -eq $done) { Say "stint ${stint} played nothing: holding (see $log)"; exit 1 }
}
if ($stint) { Say ("lowest commit free {0:N2} GB, lowest RAM available {1:N0} MB" -f $minFree, $minRam) }
& $py -I (Join-Path $main 'scripts\book-match.py') $out --summary | ForEach-Object { Say $_ }
