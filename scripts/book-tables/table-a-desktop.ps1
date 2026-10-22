<#
BT1 (a)'s leaf searches on the desktop's CPU (BOOK-TABLES.md; table-a.py makes the leaves and backs them up), driven
from the laptop over SSH (key login as Admin@id10terror-desktop), as scripts\nnue-desktop.ps1 drives the trainer. The
share folder booktable\ (D:\-=Chess Bruteforcer=-\booktable there) holds the engine, the net, the leaves and the
answers; the laptop's copies live in the main checkout's games\book-table-a\ (not in git).

  table-a-desktop.ps1 publish                      the engine, self-contained (win-x64, one file: the desktop needs no
                                                   .NET), from this checkout into games\book-table-a\engine\
  table-a-desktop.ps1 sync                         the engine, bot\eval\h1536-3m.nnue, table-a-search.py,
                                                   table-a-desktop.cmd and games\book-table-a\leaves.tsv onto the share,
                                                   through capped-copy.py (Matthew's 8 MB/s rule)
  table-a-desktop.ps1 bench                        `bench 9` there, one thread, with the CPU's % Processor Performance
  table-a-desktop.ps1 start <depth> <engines> [<limit>]
                                                   table-a-desktop.cmd as a scheduled task (a program started over SSH
                                                   dies with the connection): <engines> one-thread engines at below
                                                   normal; <limit>: only the first <limit> leaves (a sizing sample)
  table-a-desktop.ps1 status [<depth>]             the task, the CPU, memory, and the run's last log lines
  table-a-desktop.ps1 stop                         end the task and its engines; started again, it carries on
  table-a-desktop.ps1 fetch <depth>                d<depth>\'s answers into games\book-table-a\d<depth>\ (capped)

The task runs as Admin without a stored password (S4U: local files only, no window on Matthew's screen), at the task
scheduler's below-normal priority, with no trigger: after a reboot, `start` it again.
#>
param(
    [Parameter(Mandatory, Position = 0)][ValidateSet('publish', 'sync', 'bench', 'start', 'status', 'stop', 'fetch')][string]$Command,
    [Parameter(Position = 1)][string]$Depth,
    [Parameter(Position = 2)][string]$Engines,
    [Parameter(Position = 3)][string]$Limit
)
$ErrorActionPreference = 'Stop'
$Desktop = 'Admin@id10terror-desktop'
$Share = '\\id10terror-desktop\chessbruteforcer\booktable'
$Local = 'D:\-=Chess Bruteforcer=-\booktable'   # the same folder, as the desktop sees it
$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Main = Split-Path -Parent (git -C $Repo rev-parse --path-format=absolute --git-common-dir)
$Data = Join-Path $Main 'games\book-table-a'
$TaskPath = '\ChessBruteforcer\'
$TaskName = 'booktable'
$Rate = 7500000

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
    Get-Content $log -Tail 3
}

function Need-Depth { if ($Depth -notmatch '^\d+$') { throw "$Command needs a depth" } }

switch ($Command) {
    'publish' {
        New-Item -ItemType Directory -Force $Data | Out-Null
        dotnet publish "$Repo\src\ChessBruteforcer.Engine" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
            -o "$Data\engine" --nologo -v q --disable-build-servers
        if ($LASTEXITCODE) { throw "the publish failed" }
        "built from $(git -C $Repo rev-parse --short HEAD)$(if (git -C $Repo status --porcelain -- src) { ' (with changes in src)' })"
        Get-ChildItem "$Data\engine" -File | Format-Table Name, Length, LastWriteTime -AutoSize
    }
    'sync' {
        if (-not (Test-Path "$Data\leaves.tsv")) { throw "no $Data\leaves.tsv: table-a.py leaves first" }
        if (-not (Test-Path "$Data\engine\ChessBruteforcer.Engine.exe")) { throw "no engine: publish first" }
        Copy-Capped "$Share\engine" @("$Data\engine\ChessBruteforcer.Engine.exe")
        Copy-Capped $Share @("$Main\bot\eval\h1536-3m.nnue", "$PSScriptRoot\table-a-search.py", "$PSScriptRoot\table-a-desktop.cmd", "$Data\leaves.tsv")
        # The far disk's own hashes, to check against copy.log.sha256 (read there, not over the link).
        Invoke-Desktop @"
Get-ChildItem '$Local', '$Local\engine' -File | ForEach-Object { "`$((Get-FileHash `$_.FullName).Hash.ToLower()) *`$(`$_.Name)" }
"@
        "against the laptop's:"
        Get-Content "$Data\copy.log.sha256" -Tail 5
    }
    'bench' {
        Invoke-Desktop @"
`$out = '$Local\bench.txt'
`$p = Start-Process -FilePath '$Local\engine\ChessBruteforcer.Engine.exe' -ArgumentList 'bench', '9', '--eval', '$Local\h1536-3m.nnue' -WorkingDirectory '$Local\engine' -RedirectStandardOutput `$out -NoNewWindow -PassThru
`$h = `$p.Handle
`$perf = @()
while (-not `$p.HasExited) {
    `$perf += [int](Get-Counter '\Processor Information(_Total)\% Processor Performance' -SampleInterval 2 -MaxSamples 1).CounterSamples[0].CookedValue
}
Get-Content `$out
"% Processor Performance while it ran: `$(`$perf -join ', ')"
"@
    }
    'start' {
        Need-Depth
        if ($Engines -notmatch '^\d+$') { throw "start needs a number of engines" }
        if ($Limit -and $Limit -notmatch '^\d+$') { throw "the limit is a number of leaves" }
        Invoke-Desktop @"
if ((Get-ScheduledTask -TaskPath '$TaskPath' -TaskName '$TaskName' -ErrorAction SilentlyContinue).State -eq 'Running') { throw "$TaskName is running: stop it first" }
`$os = Get-CimInstance Win32_OperatingSystem
"free: `$([int](`$os.FreeVirtualMemory / 1024)) MB of commit, `$([int](`$os.FreePhysicalMemory / 1024)) MB physical"
if (`$os.FreeVirtualMemory / 1024 -lt 2048 + 200 * $Engines) { throw "too little memory free for $Engines engines" }
`$a = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c call "$Local\table-a-desktop.cmd" $Depth $Engines $Limit' -WorkingDirectory '$Local'
`$p = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType S4U -RunLevel Limited
`$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Days 7) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskPath '$TaskPath' -TaskName '$TaskName' -Action `$a -Principal `$p -Settings `$s -Force | Out-Null
Start-ScheduledTask -TaskPath '$TaskPath' -TaskName '$TaskName'
Start-Sleep 5
Get-ScheduledTask -TaskPath '$TaskPath' -TaskName '$TaskName' | Select-Object TaskName, State | Format-Table -AutoSize
"@
        "Follow it: $Share\d$Depth.log, or scripts\book-tables\table-a-desktop.ps1 status $Depth"
    }
    'status' {
        $tail = if ($Depth) { "Tail '$Local\d$Depth.log' 6; `"answers: `$((Get-Content '$Local\d$Depth\results-*.tsv' -ErrorAction SilentlyContinue | Measure-Object -Line).Lines)`"" } else { '' }
        Invoke-Desktop @"
function Tail(`$p, `$n) {
    try { `$r = New-Object IO.StreamReader([IO.File]::Open(`$p, 'Open', 'Read', 'ReadWrite')); `$t = `$r.ReadToEnd(); `$r.Close() } catch { return }
    Write-Output "--- `$p"
    `$t -split "``r?``n" | Where-Object { `$_ } | Select-Object -Last `$n
}
Get-ScheduledTask -TaskPath '$TaskPath' -TaskName '$TaskName' -ErrorAction SilentlyContinue | ForEach-Object { `$i = `$_ | Get-ScheduledTaskInfo; [pscustomobject]@{ Task = `$_.TaskName; State = `$_.State; LastRun = `$i.LastRunTime; LastResult = `$i.LastTaskResult } } | Format-Table -AutoSize
`$os = Get-CimInstance Win32_OperatingSystem
`$engines = @(Get-Process ChessBruteforcer.Engine -ErrorAction SilentlyContinue | Where-Object { `$_.Path -like '$Local\*' })
"engines: `$(`$engines.Count), `$([int](`$engines | Measure-Object PrivateMemorySize64 -Sum).Sum / 1MB) MB; free: `$([int](`$os.FreeVirtualMemory / 1024)) MB of commit"
"CPU: `$([int](Get-Counter '\Processor Information(_Total)\% Processor Performance').CounterSamples[0].CookedValue)% of base clock, `$([int](Get-Counter '\Processor(_Total)\% Processor Time').CounterSamples[0].CookedValue)% busy"
try { Get-CimInstance -Namespace root/wmi MSAcpi_ThermalZoneTemperature -ErrorAction Stop | ForEach-Object { "thermal zone `$(`$_.InstanceName): `$([math]::Round(`$_.CurrentTemperature / 10 - 273.15)) C" } } catch { "(no temperature reading)" }
$tail
"@
    }
    'stop' {
        Invoke-Desktop @"
Stop-ScheduledTask -TaskPath '$TaskPath' -TaskName '$TaskName' -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process | Where-Object { (`$_.Name -eq 'python.exe' -and `$_.CommandLine -like '*booktable\table-a-search.py*') -or (`$_.Name -eq 'ChessBruteforcer.Engine.exe' -and `$_.ExecutablePath -like '$Local\*') } | ForEach-Object { Stop-Process -Id `$_.ProcessId -Force; "stopped `$(`$_.Name) `$(`$_.ProcessId)" }
Get-ChildItem '$Local\d*.log' | ForEach-Object { Add-Content `$_.FullName "`$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') stopped from the laptop" }
"@
    }
    'fetch' {
        Need-Depth
        Copy-Capped "$Data\d$Depth" @("$Share\d$Depth")
        Copy-Item "$Share\d$Depth.log" "$Data\" -Force
        Get-ChildItem "$Data\d$Depth" -File | Format-Table Name, Length, LastWriteTime -AutoSize
    }
}
