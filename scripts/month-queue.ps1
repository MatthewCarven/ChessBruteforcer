# Every Lichess month onto the desktop share, newest first (Matthew, 2026-10-13: "queue the lot up").
#
#   scripts\month-queue.cmd                      (its own window; or: pwsh -File scripts\month-queue.ps1)
#   pwsh -File scripts\month-queue.ps1 -DryRun   (what it would do, touching nothing)
#
# Each month: downloaded to -Dir with Lichess's SHA-256 (download-lichess-month.ps1, which resumes and
# rides out drops); then, if it has no counts yet, counted for the book (make-book.py count, which checks
# the SHA-256) and both books rebuilt (a running bot picks them up at its next move); if it was counted
# before it was kept (August and September 2026), just checked against the SHA-256 (make-book.py verify).
#
# It stops before a download would leave the share with less than -KeepFreeGB (the whole archive is
# ~2.6 TB, so with 1.4 TB free it gets back to about early 2023), or when a download can't finish (the
# internet down for hours): run it again to carry on.  Months done are listed in games\month-queue.done,
# damaged ones in games\month-queue.damaged (left where they are, for Matthew to look at).  A month whose
# .part is growing (month-book.cmd downloading it in another window) is left for the end, and waited for.
# Its log: games\month-queue.log.
param([string] $Dir = '\\id10terror-desktop\chessbruteforcer\lichess', [int] $KeepFreeGB = 100, [switch] $DryRun)
$ErrorActionPreference = 'Continue'
Set-Location (Split-Path $PSScriptRoot -Parent)
$py = 'bot\lichess-bot\.venv\Scripts\python.exe'
$log = 'games\month-queue.log'
$doneFile = 'games\month-queue.done'
$damagedFile = 'games\month-queue.damaged'

function Log([string] $text) {
    $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $text
    Write-Host $line
    if (-not $DryRun) { Add-Content -Path $log -Value $line }
}

function Wait-Dir {
    $since = $null
    while (-not (Test-Path -LiteralPath $Dir)) {
        if (-not $since) { $since = Get-Date; Log "can't reach $Dir; waiting for it (up to 6 hours)" }
        if (((Get-Date) - $since).TotalHours -ge 6) { Log "STOPPING: $Dir unreachable for 6 hours (run this again to carry on)"; exit 1 }
        Start-Sleep -Seconds 30
    }
    if ($since) { Log ('{0} is back after {1:N0} min' -f $Dir, ((Get-Date) - $since).TotalMinutes) }
}

function Free-GB { [long](& $py -c "import shutil, sys; print(shutil.disk_usage(sys.argv[1]).free // 10**9)" $Dir) }

function Remote-Bytes([string] $url) {
    $head = curl.exe -sI $url
    [long](($head | Select-String 'Content-Length: (\d+)').Matches[0].Groups[1].Value)
}

function Growing([string] $path) {
    # Someone else (month-book.cmd in another window) downloading this month: its .part grows.  Watched for
    # longer than the downloader's minute between attempts.
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    $before = (Get-Item -LiteralPath $path).Length
    Start-Sleep -Seconds 150
    return (Get-Item -LiteralPath $path).Length -ne $before
}

function Rebuild-Books([string] $monthLog) {
    & $py -I scripts\make-book.py book bot\books\lichess-2200.bin "bot\books\counts\*.counts" *>> $monthLog
    & $py -I scripts\make-book.py book bot\books\lichess-2200-wide.bin "bot\books\counts\*.counts" --min-games 3 *>> $monthLog
    Get-Content $monthLog | Select-String '^book:' | Select-Object -Last 2 | ForEach-Object { Log "  $($_.Line -replace ' -> .*', '')" }
}

function Do-Month([string] $month) {
    # 'done', 'damaged', 'deferred', or 'stop'
    $name = "lichess_db_standard_rated_$month.pgn.zst"
    $final = Join-Path $Dir $name
    $counts = "bot\books\counts\$month.counts"
    $monthLog = "bot\books\month-book-$month.log"
    Wait-Dir
    if (-not (Test-Path -LiteralPath $final)) {
        if (Growing "$final.part") { Log "$month is being downloaded in another window: leaving it for the end"; return 'deferred' }
        $bytes = Remote-Bytes "https://database.lichess.org/standard/$name"
        $have = if (Test-Path -LiteralPath "$final.part") { (Get-Item -LiteralPath "$final.part").Length } else { 0 }
        $free = Free-GB
        if ($free - ($bytes - $have) / 1e9 -lt $KeepFreeGB) {
            Log ('STOPPING before {0}: {1:N1} GB to fetch, {2:N0} GB free on the share, keeping {3} GB free' -f $month, (($bytes - $have) / 1e9), $free, $KeepFreeGB)
            return 'stop'
        }
        Log ('{0}: downloading {1:N1} GB ({2:N0} GB free)' -f $month, (($bytes - $have) / 1e9), $free)
        & .\scripts\download-lichess-month.ps1 -Month $month -Dir $Dir | Out-Host
        if (-not (Test-Path -LiteralPath $final)) { Log "STOPPING: $month's download didn't finish (run this again to carry on)"; return 'stop' }
    }
    if (Test-Path $counts) {
        Log "$month`: counted before; checking it against Lichess's SHA-256"
        & $py -I scripts\make-book.py verify $final --counts $counts *>> $monthLog
        if ($LASTEXITCODE -ne 0) { Log "DAMAGED: $month (see $monthLog)"; return 'damaged' }
        Log "$month`: matches"
    } else {
        Log "$month`: counting (progress in $monthLog)"
        Add-Content $monthLog ('{0:yyyy-MM-dd HH:mm:ss}: counting {1} (month-queue)' -f (Get-Date), $final)
        & $py -I scripts\make-book.py count $final $counts *>> $monthLog
        if (-not (Test-Path $counts)) {
            if (Select-String -Path $monthLog -Pattern "doesn't match Lichess's SHA-256" -Quiet) { Log "DAMAGED: $month (see $monthLog)"; return 'damaged' }
            Log "STOPPING: counting $month failed (see $monthLog)"; return 'stop'
        }
        Log ("$month`: " + ((Get-Content $monthLog | Select-String 'games read' | Select-Object -Last 1).Line -replace ' to bot.*', ''))
        Rebuild-Books $monthLog
    }
    return 'done'
}

Wait-Dir
$names = curl.exe -sS https://database.lichess.org/standard/list.txt | ForEach-Object { Split-Path $_ -Leaf }
$months = $names | Where-Object { $_ -match '^lichess_db_standard_rated_(\d{4}-\d{2})\.pgn\.zst$' } |
    ForEach-Object { $_ -replace '^lichess_db_standard_rated_(\d{4}-\d{2})\.pgn\.zst$', '$1' } | Sort-Object -Descending
if ($months.Count -lt 100) { Log "STOPPING: Lichess's list gave only $($months.Count) months"; exit 1 }
$done = @(if (Test-Path $doneFile) { Get-Content $doneFile })
$damaged = @(if (Test-Path $damagedFile) { Get-Content $damagedFile })
$todo = @($months | Where-Object { $_ -notin $done -and $_ -notin $damaged })
Log ('=== {0} months on Lichess, {1} done, {2} damaged, {3} to go, newest first ({4}); {5:N0} GB free on the share' -f $months.Count, $done.Count, $damaged.Count, $todo.Count, $todo[0], (Free-GB))

if ($DryRun) {
    $free = Free-GB
    foreach ($m in $todo) {
        $name = "lichess_db_standard_rated_$m.pgn.zst"
        $final = Join-Path $Dir $name
        $step = if (Test-Path "bot\books\counts\$m.counts") { 'verify' } else { 'count' }
        if (Test-Path -LiteralPath $final) { "$m  on the share already, then $step"; continue }
        $bytes = Remote-Bytes "https://database.lichess.org/standard/$name"
        if ($free - $bytes / 1e9 -lt $KeepFreeGB) { '{0}  would stop here: {1:N1} GB with {2:N0} GB left' -f $m, ($bytes / 1e9), $free; break }
        $free -= $bytes / 1e9
        '{0}  download {1,5:N1} GB, then {2}; {3:N0} GB left after' -f $m, ($bytes / 1e9), $step, $free
    }
    return
}

$deferred = @()
foreach ($m in $todo) {
    $result = Do-Month $m | Select-Object -Last 1
    if ($result -eq 'done') { Add-Content $doneFile $m }
    elseif ($result -eq 'damaged') { Add-Content $damagedFile $m }
    elseif ($result -eq 'deferred') { $deferred += $m }
    else { exit 1 }
}
foreach ($m in $deferred) {
    $final = Join-Path $Dir "lichess_db_standard_rated_$m.pgn.zst"
    while (-not (Test-Path -LiteralPath $final) -and (Growing "$final.part")) { }
    $result = Do-Month $m | Select-Object -Last 1
    if ($result -eq 'done') { Add-Content $doneFile $m }
    elseif ($result -eq 'damaged') { Add-Content $damagedFile $m }
    else { exit 1 }
}
Log '=== the queue is through'
