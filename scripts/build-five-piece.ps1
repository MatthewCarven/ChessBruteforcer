# Solves every 5-piece table without pawns (60 of them): the mate distances
# (.cbt) and the 50-move rule (.cbz, which also counts the cursed wins),
# into tables\ (or $env:CHESS_TABLES).
#
# Restartable: a table with both files complete on disk is skipped, so if it
# stops (a reboot, Ctrl+C) just run it again.  It stops by itself if the disk
# gets down to -MinFreeGB.  Everything goes to tables\five-piece.log.
#
#   scripts\build-five-piece.cmd                  (double-click, or from a prompt)
#   pwsh scripts\build-five-piece.ps1 -List       what it would do, and what's done
#   pwsh scripts\build-five-piece.ps1 -Only KQRRvK,KRBvKR
#
# Expect ~18 hours and ~31 GB for all 60 on Matthew's laptop, one table at a
# time (~1-2 GB of memory each).  Each table is compressed once both files are
# done (~9x smaller; `compress` checks every value first); -Plain leaves them.
#
# -Pawns: the 50 with pawns instead (KRPvKR, done, then 49 more), in the order
# `game endings` chose (TODO step D): each after the tables its promotions
# lead to.  ~49 hours, ~2.8 GB plain at most at a time, ~13 GB compressed.
# After each table a sample of both kinds is verified against its moves
# (every 1009th position, ~4 min); a failure or a mismatch stops the run,
# since the tables after it are built on it.
#
#   scripts\build-five-piece.cmd -Pawns
#   pwsh scripts\build-five-piece.ps1 -Pawns -List
param(
    [int] $MinFreeGB = 10,
    [string[]] $Only,
    [switch] $List,
    [switch] $Plain,
    [switch] $Pawns
)
$ErrorActionPreference = 'Continue'
Set-Location (Split-Path $PSScriptRoot -Parent)
$tables = if ($env:CHESS_TABLES) { $env:CHESS_TABLES } else { 'tables' }
New-Item -ItemType Directory -Force $tables | Out-Null
$log = Join-Path $tables 'five-piece.log'
$build = 'src\ChessBruteforcer.Cli\bin\Release\net8.0'
$cli = Join-Path $build 'ChessBruteforcer.Cli.dll'
$VerifyStride = 1009   # a prime, so the sample doesn't line up with the numbering

function Log([string] $text) {
    $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $text
    Write-Host $line
    Add-Content -Path $log -Value $line
}

# A table file's first four bytes: "CBT2" finished, "CBT3" capped, "CBC1" compressed.
function Magic([string] $path) {
    if (-not (Test-Path $path)) { return $null }
    $stream = [IO.File]::OpenRead((Resolve-Path $path))
    try {
        $magic = New-Object byte[] 4
        [void] $stream.Read($magic, 0, 4)
        return [Text.Encoding]::ASCII.GetString($magic)
    } finally { $stream.Dispose() }
}

# A mate-distance table that is finished (only complete tables are compressed).
function Complete([string] $path) { return (Magic $path) -in 'CBT2', 'CBC1' }

function Compressed([string] $path) { return (Magic $path) -eq 'CBC1' }

# Both files of a finished table, compressed in place unless -Plain.
function Compress-Table([string] $m) {
    if ($Plain) { return }
    foreach ($file in (Join-Path $tables "$m.cbt"), (Join-Path $tables "$m.cbz")) {
        if ((Test-Path $file) -and -not (Compressed $file)) {
            $output = & dotnet $cli compress $file
            $code = $LASTEXITCODE
            $output | Select-Object -First 1 | ForEach-Object { Log "compress: $_" }
            if ($code -ne 0) { Log "compress ${file}: exit $code, left plain" }
        }
    }
}

# The 60: three pieces against a bare king, and two against one.  Pieces are
# written strongest first (Q, R, B, N), as the tables name them.
$pieces = 'Q', 'R', 'B', 'N'
$materials = @()
for ($a = 0; $a -lt 4; $a++) { for ($b = $a; $b -lt 4; $b++) {
    for ($c = $b; $c -lt 4; $c++) { $materials += "K$($pieces[$a])$($pieces[$b])$($pieces[$c])vK" }
    for ($d = 0; $d -lt 4; $d++) { $materials += "K$($pieces[$a])$($pieces[$b])vK$($pieces[$d])" }
} }
# Tables with identical pieces first: they are a half or a sixth the size, so quicker.
$materials = $materials | Sort-Object { if ($_ -match '(.)\1') { 0 } else { 1 } }, { $_ }
if ($Pawns) {
    # Step B's order (`game endings`, 2026-09-30): the most games wholly inside the tables per
    # position solved, each table after the ones its promotions lead to.  KRPvKR is done already
    # (listed so it gets compressed).  The first 15 are a quarter of the work and take the games
    # wholly in the tables from 14% to 42%; K+P+P v K+P, the most reached, needs nearly all the rest.
    $materials = @(
        'KRPvKR',
        'KQRPvK', 'KQQPvK', 'KQBPvK', 'KRBPvK', 'KQNPvK', 'KRNPvK', 'KRRPvK', 'KBNPvK', 'KBBPvK', 'KNNPvK',
        'KRPPvK', 'KQPPvK', 'KBPPvK', 'KNPPvK', 'KPPPvK',
        'KQPvKQ', 'KQRvKP', 'KQPvKR', 'KBPvKB', 'KBPvKR', 'KNPvKN', 'KQQvKP', 'KRPvKB', 'KQBvKP', 'KRPvKQ',
        'KNPvKR', 'KRPvKN', 'KQNvKP', 'KNPvKB', 'KRBvKP', 'KBPvKN', 'KQPvKN', 'KQPvKB', 'KBPvKQ', 'KRNvKP',
        'KNPvKQ', 'KRRvKP', 'KBNvKP', 'KBBvKP', 'KNNvKP',
        'KQPvKP', 'KRPvKP', 'KBPvKP', 'KPPvKR', 'KPPvKQ', 'KNPvKP', 'KPPvKN', 'KPPvKB',
        'KPPvKP'
    )
}
if ($Only) { $materials = $materials | Where-Object { $Only -contains $_ } }

if ($List) {
    foreach ($m in $materials) {
        $cbt = if (Complete (Join-Path $tables "$m.cbt")) { 'mate done' } else { 'mate to do' }
        $cbz = if (Test-Path (Join-Path $tables "$m.cbz")) { 'rule done' } else { 'rule to do' }
        $packed = @('cbt', 'cbz' | Where-Object { Compressed (Join-Path $tables "$m.$_") })
        $note = if ($packed.Count -gt 0) { "(compressed: $($packed -join ', '))" } else { '' }
        '{0,-8} {1,-11} {2,-11} {3}' -f $m, $cbt, $cbz, $note
    }
    "$($materials.Count) tables"
    return
}

Log "=== start: $($materials.Count) tables into $tables$(if ($Pawns) { ' (with pawns)' })"
$output = dotnet build -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) {
    $output | Where-Object { $_ -match 'error' } | Select-Object -Unique -First 10 | ForEach-Object { Log "build: $_" }
    Log 'build failed: stopping'
    exit 1
}
# Run from a copy of the build: Windows locks a running program's files, and this can take days.
$run = Join-Path $tables '.cli'
Remove-Item -Recurse -Force $run -ErrorAction SilentlyContinue
Copy-Item -Recurse $build $run
$cli = Join-Path $run 'ChessBruteforcer.Cli.dll'
# Older files (identical pieces in every order, DTZ at two bytes) into the current format.
dotnet $cli upgrade $tables | ForEach-Object { Log "upgrade: $_" }

$begin = Get-Date
$done = 0
$stop = $false
foreach ($m in $materials) {
    $cbt = Join-Path $tables "$m.cbt"
    $cbz = Join-Path $tables "$m.cbz"
    if ((Complete $cbt) -and (Test-Path $cbz)) { Compress-Table $m; $done++; continue }
    $drive = (Get-Item $tables).PSDrive
    $free = (Get-PSDrive $drive.Name).Free / 1GB
    if ($free -lt $MinFreeGB) { Log ('stopping: only {0:N1} GB free on {1}:' -f $free, $drive.Name); break }

    # Mate distances first, then the rule: the second also prints the cursed wins.  With pawns,
    # then a sample of each checked against its moves.
    $steps = @(@('solve', $m), @('dtz', $m))
    if ($Pawns) { $steps += @(, @('verify', $m, $VerifyStride)) ; $steps += @(, @('dtz', $m, 'verify', $VerifyStride)) }
    $failed = $false
    foreach ($step in $steps) {
        $name = $step -join ' '
        $watch = [Diagnostics.Stopwatch]::StartNew()
        Log ("${name}: starting ({0:N1} GB free)" -f $free)
        $output = & dotnet $cli @step              # progress goes to the window; results to the log
        $code = $LASTEXITCODE
        Log ("${name}: exit $code after {0:N1} min" -f $watch.Elapsed.TotalMinutes)
        $output | ForEach-Object { Add-Content -Path $log -Value "    $_" }
        if ($code -ne 0) { $failed = $true; break }
    }
    if ($failed -and $Pawns) {
        # The tables after this one are built on it: nothing more until it's looked at.
        Log "${m}: failed or a mismatch (see above): stopping, left plain"
        $stop = $true
        break
    }
    if ($failed) { Log "${m}: failed, going on to the next table"; continue }
    if ((Complete $cbt) -and (Test-Path $cbz)) { Compress-Table $m }
    $done++
    Log ("{0} of {1} tables done, {2:N1} h so far" -f $done, $materials.Count, ((Get-Date) - $begin).TotalHours)
}
Log "=== finished: $done of $($materials.Count) tables on disk$(if ($stop) { ' (stopped on a failure)' })"
