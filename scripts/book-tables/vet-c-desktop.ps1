<#
BT1 (c)'s searches on the desktop's CPU (vet-c-search.py), driven from the laptop over SSH (key login as
Admin@id10terror-desktop), on table-a-desktop.ps1's model. The share folder book-vet-c\ (D:\-=Chess Bruteforcer=-\book-vet-c
there) holds a copy of this checkout's layout as far as the run needs it (scripts\book-tables\vet-c-search.py and
results\, scripts\ponder-scope\uci.py, bot\eval\h1536-3m.nnue) and the engine: the desktop has no AVX2, so it's a copy
of bt-a's SSE build (booktable\engine, 9603e7b's 128-bit path, the same trees as the laptop's AVX build).

  vet-c-desktop.ps1 sync            the scripts, the net, vet-c-desktop.cmd and the position list onto the share
                                    (capped-copy.py, Matthew's 8 MB/s rule); the laptop's results so far and a check
                                    list only if the share has none yet (never over the desktop's own); the engine
                                    copied there from booktable\engine; the desktop's hashes of all of it
  vet-c-desktop.ps1 check <engines> 5 positions the laptop has done (results\vet-c-screen.csv), searched again there
                                    in the foreground into check\check.csv: every move, score and node count must match
  vet-c-desktop.ps1 start <engines> vet-c-desktop.cmd as a scheduled task (S4U, below normal, no window): the screen at
                                    depth 16, then the confirm at depth 18
  vet-c-desktop.ps1 status          the task, the engines, the CPU, memory, rows done, the last log lines
  vet-c-desktop.ps1 stop            end the task and its engines; started again, it carries on
  vet-c-desktop.ps1 fetch           the results back (capped) into games\book-vet-c\fetched\ (main checkout), then over
                                    this checkout's results\vet-c-screen.csv and vet-c-confirm.csv
#>
param(
    [Parameter(Mandatory, Position = 0)][ValidateSet('sync', 'check', 'start', 'status', 'stop', 'fetch')][string]$Command,
    [Parameter(Position = 1)][string]$Engines
)
$ErrorActionPreference = 'Stop'
$Desktop = 'Admin@id10terror-desktop'
$Share = '\\id10terror-desktop\chessbruteforcer\book-vet-c'
$Local = 'D:\-=Chess Bruteforcer=-\book-vet-c'   # the same folder, as the desktop sees it
$BookTable = 'D:\-=Chess Bruteforcer=-\booktable'
$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Main = Split-Path -Parent (git -C $Repo rev-parse --path-format=absolute --git-common-dir)
$Data = Join-Path $Main 'games\book-vet-c'
$Results = Join-Path $PSScriptRoot 'results'
$TaskPath = '\ChessBruteforcer\'
$TaskName = 'book-vet-c'
$Rate = 7500000
$CheckRanks = '135', '202', '270', '404', '471'   # done on the laptop (the timing), 1-3 moves each
New-Item -ItemType Directory -Force $Data | Out-Null

function Invoke-Desktop([string]$script) {
    # PowerShell on the desktop, base64-encoded so no quoting survives three shells (nnue-desktop.ps1's way).
    $script = "`$ProgressPreference = 'SilentlyContinue'`n" + $script
    $b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    ssh -o BatchMode=yes -o ConnectTimeout=15 -o LogLevel=ERROR $Desktop "powershell -NoProfile -NonInteractive -EncodedCommand $b64"
    if ($LASTEXITCODE) { throw "ssh: exit $LASTEXITCODE" }
}

function Copy-Capped([string]$dest, [string[]]$sources) {
    $log = Join-Path $Data 'copy.log'
    python "$Repo\scripts\capped-copy.py" $dest $log $Rate $sources
    if ($LASTEXITCODE) { throw "capped-copy.py: exit $LASTEXITCODE" }
    Get-Content $log -Tail 2
}

function Need-Engines { if ($Engines -notmatch '^\d+$') { throw "$Command needs a number of engines" } }

switch ($Command) {
    'sync' {
        Copy-Capped "$Share\scripts\book-tables" @("$PSScriptRoot\vet-c-search.py")
        Copy-Capped "$Share\scripts\ponder-scope" @("$Repo\scripts\ponder-scope\uci.py")
        Copy-Capped "$Share\bot\eval" @("$Repo\bot\eval\h1536-3m.nnue")
        Copy-Capped $Share @("$PSScriptRoot\vet-c-desktop.cmd")
        Copy-Capped "$Share\scripts\book-tables\results" @("$Results\vet-c-positions.csv")
        $check = Join-Path $Data 'check-positions.csv'
        Import-Csv "$Results\vet-c-positions.csv" | Where-Object { $CheckRanks -contains $_.rank } |
            Export-Csv $check -NoTypeInformation -Encoding utf8NoBOM
        foreach ($f in @("$Results\vet-c-screen.csv", "$Results\vet-c-confirm.csv", $check)) {
            if (Test-Path "$Share\scripts\book-tables\results\$(Split-Path -Leaf $f)") { "kept the share's own $(Split-Path -Leaf $f)" }
            else { Copy-Capped "$Share\scripts\book-tables\results" @($f) }
        }
        # The engine: bt-a's SSE build, copied on the desktop's own disk; then the far disk's hashes of everything.
        Invoke-Desktop @"
if (-not (Test-Path '$Local\engine\ChessBruteforcer.Engine.exe')) { Copy-Item '$BookTable\engine' '$Local\engine' -Recurse }
Get-ChildItem '$Local' -Recurse -File | Where-Object { `$_.Name -notlike '*.log' } | ForEach-Object { "`$((Get-FileHash `$_.FullName).Hash.ToLower()) *`$(`$_.FullName.Substring('$Local'.Length + 1))" }
"booktable's engine: `$((Get-FileHash '$BookTable\engine\ChessBruteforcer.Engine.exe').Hash.ToLower())"
"@
        "against the laptop's:"
        Get-Content "$Data\copy.log.sha256" -Tail 9
    }
    'check' {
        Need-Engines
        # In the foreground: a program started over SSH lives as long as the connection.
        Invoke-Desktop @"
if ((Get-ScheduledTask -TaskPath '$TaskPath' -TaskName '$TaskName' -ErrorAction SilentlyContinue).State -eq 'Running') { throw "$TaskName is running: stop it first" }
New-Item -ItemType Directory -Force '$Local\check' | Out-Null
& 'C:\Program Files\Python314\python.exe' -I -u '$Local\scripts\book-tables\vet-c-search.py' '$Local\engine\ChessBruteforcer.Engine.exe' '$Local\check\check.csv' --depth 16 --tiers 1,2 --workers $Engines --positions '$Local\scripts\book-tables\results\check-positions.csv' 2>&1
"@
        $laptop = @{}
        Import-Csv "$Results\vet-c-screen.csv" | ForEach-Object { $laptop["$($_.kind) $($_.key) $($_.move)"] = $_ }
        $bad = 0; $n = 0
        foreach ($r in Import-Csv "$Share\check\check.csv") {
            $l = $laptop["$($r.kind) $($r.key) $($r.move)"]
            $n++
            if (-not $l) { "not on the laptop: $($r.kind) #$($r.rank) $($r.move)"; $bad++; continue }
            $same = $l.best -eq $r.best -and $l.best_cp -eq $r.best_cp -and $l.move_cp -eq $r.move_cp -and $l.nodes -eq $r.nodes
            "{0} {1} #{2} {3}: laptop {4} {5} {6} nodes {7} s; desktop {8} {9} {10} nodes {11} s" -f $(if ($same) { 'same' } else { 'DIFF' }),
                $r.kind, $r.rank, $r.move, $l.best_cp, $l.move_cp, $l.nodes, $l.seconds, $r.best_cp, $r.move_cp, $r.nodes, $r.seconds
            if (-not $same) { $bad++ }
        }
        if ($bad -or -not $n) { throw "$bad of $n rows differ: don't start" }
        "all $n rows match"
    }
    'start' {
        Need-Engines
        Invoke-Desktop @"
if ((Get-ScheduledTask -TaskPath '$TaskPath' -TaskName '$TaskName' -ErrorAction SilentlyContinue).State -eq 'Running') { throw "$TaskName is running: stop it first" }
`$os = Get-CimInstance Win32_OperatingSystem
"free: `$([int](`$os.FreeVirtualMemory / 1024)) MB of commit, `$([int](`$os.FreePhysicalMemory / 1024)) MB physical"
if (`$os.FreeVirtualMemory / 1024 -lt 2048 + 200 * $Engines) { throw "too little memory free for $Engines engines" }
`$a = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c call "$Local\vet-c-desktop.cmd" $Engines' -WorkingDirectory '$Local'
`$p = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType S4U -RunLevel Limited
`$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Days 7) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskPath '$TaskPath' -TaskName '$TaskName' -Action `$a -Principal `$p -Settings `$s -Force | Out-Null
Start-ScheduledTask -TaskPath '$TaskPath' -TaskName '$TaskName'
Start-Sleep 5
Get-ScheduledTask -TaskPath '$TaskPath' -TaskName '$TaskName' | Select-Object TaskName, State | Format-Table -AutoSize
"@
        "Follow it: scripts\book-tables\vet-c-desktop.ps1 status"
    }
    'status' {
        Invoke-Desktop @"
function Tail(`$p, `$n) {
    try { `$r = New-Object IO.StreamReader([IO.File]::Open(`$p, 'Open', 'Read', 'ReadWrite')); `$t = `$r.ReadToEnd(); `$r.Close() } catch { return }
    Write-Output "--- `$(Split-Path -Leaf `$p)"
    `$t -split "``r?``n" | Where-Object { `$_ } | Select-Object -Last `$n
}
Get-ScheduledTask -TaskPath '$TaskPath' -TaskName '$TaskName' -ErrorAction SilentlyContinue | ForEach-Object { `$i = `$_ | Get-ScheduledTaskInfo; [pscustomobject]@{ Task = `$_.TaskName; State = `$_.State; LastRun = `$i.LastRunTime; LastResult = `$i.LastTaskResult } } | Format-Table -AutoSize
`$os = Get-CimInstance Win32_OperatingSystem
`$engines = @(Get-Process ChessBruteforcer.Engine -ErrorAction SilentlyContinue | Where-Object { `$_.Path -like '$Local\*' })
"engines: `$(`$engines.Count), `$([int](`$engines | Measure-Object PrivateMemorySize64 -Sum).Sum / 1MB) MB; free: `$([int](`$os.FreeVirtualMemory / 1024)) MB of commit"
"CPU: `$([int](Get-Counter '\Processor(_Total)\% Processor Time').CounterSamples[0].CookedValue)% busy"
foreach (`$f in 'vet-c-screen', 'vet-c-confirm') {
    `$csv = '$Local\scripts\book-tables\results\' + `$f + '.csv'
    if (Test-Path `$csv) { "`$f rows: `$((Get-Content `$csv | Measure-Object -Line).Lines - 1)" }
}
Tail '$Local\scripts\book-tables\results\vet-c-screen.log' 4
Tail '$Local\scripts\book-tables\results\vet-c-confirm.log' 4
Tail '$Local\run.log' 3
"@
    }
    'stop' {
        Invoke-Desktop @"
Stop-ScheduledTask -TaskPath '$TaskPath' -TaskName '$TaskName' -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process | Where-Object { (`$_.Name -eq 'python.exe' -and `$_.CommandLine -like '*book-vet-c\scripts\book-tables\vet-c-search.py*') -or (`$_.Name -eq 'ChessBruteforcer.Engine.exe' -and `$_.ExecutablePath -like '$Local\*') } | ForEach-Object { Stop-Process -Id `$_.ProcessId -Force; "stopped `$(`$_.Name) `$(`$_.ProcessId)" }
Add-Content '$Local\run.log' "`$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') stopped from the laptop"
"@
    }
    'fetch' {
        $sources = 'vet-c-screen.csv', 'vet-c-confirm.csv', 'vet-c-screen.log', 'vet-c-confirm.log' |
            ForEach-Object { "$Share\scripts\book-tables\results\$_" } | Where-Object { Test-Path $_ }
        Copy-Capped "$Data\fetched" @($sources)
        Copy-Item "$Share\run.log" "$Data\fetched\" -Force
        foreach ($f in 'vet-c-screen.csv', 'vet-c-confirm.csv') {
            if (-not (Test-Path "$Data\fetched\$f")) { continue }
            $there = (Get-Content "$Data\fetched\$f" | Measure-Object -Line).Lines
            $here = if (Test-Path "$Results\$f") { (Get-Content "$Results\$f" | Measure-Object -Line).Lines } else { 0 }
            if ($there -lt $here) { throw "the desktop's $f has fewer lines ($there) than this checkout's ($here): not copied" }
            Copy-Item "$Data\fetched\$f" "$Results\$f" -Force
            "$f`: $here -> $there lines"
        }
    }
}
