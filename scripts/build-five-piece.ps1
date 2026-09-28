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
# time (~1-2 GB of memory each).
param(
    [int] $MinFreeGB = 10,
    [string[]] $Only,
    [switch] $List
)
$ErrorActionPreference = 'Continue'
Set-Location (Split-Path $PSScriptRoot -Parent)
$tables = if ($env:CHESS_TABLES) { $env:CHESS_TABLES } else { 'tables' }
New-Item -ItemType Directory -Force $tables | Out-Null
$log = Join-Path $tables 'five-piece.log'
$cli = 'src\ChessBruteforcer.Cli\bin\Release\net8.0\ChessBruteforcer.Cli.dll'

function Log([string] $text) {
    $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $text
    Write-Host $line
    Add-Content -Path $log -Value $line
}

# A mate-distance table that is finished: "CBT2" (a capped one is "CBT3").
function Complete([string] $path) {
    if (-not (Test-Path $path)) { return $false }
    $stream = [IO.File]::OpenRead((Resolve-Path $path))
    try {
        $magic = New-Object byte[] 4
        [void] $stream.Read($magic, 0, 4)
        return [Text.Encoding]::ASCII.GetString($magic) -eq 'CBT2'
    } finally { $stream.Dispose() }
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
if ($Only) { $materials = $materials | Where-Object { $Only -contains $_ } }

if ($List) {
    foreach ($m in $materials) {
        $cbt = if (Complete (Join-Path $tables "$m.cbt")) { 'mate done' } else { 'mate to do' }
        $cbz = if (Test-Path (Join-Path $tables "$m.cbz")) { 'rule done' } else { 'rule to do' }
        '{0,-8} {1,-11} {2}' -f $m, $cbt, $cbz
    }
    "$($materials.Count) tables"
    return
}

Log "=== start: $($materials.Count) tables into $tables"
dotnet build -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { Log 'build failed: stopping'; exit 1 }
# Older files (identical pieces in every order, DTZ at two bytes) into the current format.
dotnet $cli upgrade $tables | ForEach-Object { Log "upgrade: $_" }

$begin = Get-Date
$done = 0
foreach ($m in $materials) {
    $cbt = Join-Path $tables "$m.cbt"
    $cbz = Join-Path $tables "$m.cbz"
    if ((Complete $cbt) -and (Test-Path $cbz)) { $done++; continue }
    $drive = (Get-Item $tables).PSDrive
    $free = (Get-PSDrive $drive.Name).Free / 1GB
    if ($free -lt $MinFreeGB) { Log ('stopping: only {0:N1} GB free on {1}:' -f $free, $drive.Name); break }

    # Mate distances first, then the rule: the second also prints the cursed wins.
    foreach ($step in 'solve', 'dtz') {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        Log ("$m ${step}: starting ({0:N1} GB free)" -f $free)
        $output = & dotnet $cli $step $m          # progress goes to the window; results to the log
        $code = $LASTEXITCODE
        Log ("$m ${step}: exit $code after {0:N1} min" -f $watch.Elapsed.TotalMinutes)
        $output | ForEach-Object { Add-Content -Path $log -Value "    $_" }
        if ($code -ne 0) { Log "$m ${step}: failed, going on to the next table"; break }
    }
    $done++
    Log ("{0} of {1} tables done, {2:N1} h so far" -f $done, $materials.Count, ((Get-Date) - $begin).TotalHours)
}
Log "=== finished: $done of $($materials.Count) tables on disk"
