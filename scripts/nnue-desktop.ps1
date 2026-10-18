<#
The desktop's GPU as a second trainer (NNUE.md, session 2b), driven from the laptop over SSH (key login as
Admin@id10terror-desktop). The desktop's share folder nnue\ (D:\-=Chess Bruteforcer=-\nnue there) holds the venv, the
records, the scripts and the nets; train-desktop.cmd (scripts\nnue\) runs there.

  scripts\nnue-desktop.ps1 sync                    copy the trainer's scripts, train-desktop.cmd and setup-desktop.cmd
                                                   onto the share (~100 KB)
  scripts\nnue-desktop.ps1 train <run> [options]   start train-desktop.cmd <run> [train.py train options] there as a
                                                   scheduled task (a program started over SSH dies with the connection);
                                                   runs separated by a lone + go one after another in one task:
                                                   train a --hidden 512 --mates + b --hidden 512 --result-blend 0.25
  scripts\nnue-desktop.ps1 data <month> [+ ...]    the month's training records, made there from the month file on its
                                                   own disk (data-desktop.cmd: analysed games, import, nnue data, check),
                                                   into nnue\<month>\; months separated by + go one after another
  scripts\nnue-desktop.ps1 status [<run>]          the task, the GPU, and the run's (or month's) last log lines
  scripts\nnue-desktop.ps1 stop <run>              end the run, and the task it is in (with any runs queued after it); its
                                                   last checkpoint stays: train ... --resume carries on
  scripts\nnue-desktop.ps1 fetch <run>             <run>.nnue and <run>.log into games\nnue\nets\ of the main checkout

The task runs as Admin without a stored password (S4U: local files only, no window on Matthew's screen), at the task
scheduler's below-normal priority. Big copies onto the share go through capped_copy.py (Matthew's 8 MB/s rule), not this.
#>
param(
    [Parameter(Mandatory, Position = 0)][ValidateSet('sync', 'train', 'data', 'status', 'stop', 'fetch')][string]$Command,
    [Parameter(Position = 1)][string]$Run,
    [Parameter(ValueFromRemainingArguments)][string[]]$Options
)
$ErrorActionPreference = 'Stop'
$Desktop = 'Admin@id10terror-desktop'
$Share = '\\id10terror-desktop\chessbruteforcer\nnue'
$Local = 'D:\-=Chess Bruteforcer=-\nnue'   # the same folder, as the desktop sees it
$Repo = Split-Path -Parent $PSScriptRoot
$TaskPath = '\ChessBruteforcer\'

function Invoke-Desktop([string]$script) {
    # PowerShell on the desktop, the script base64-encoded so no quoting survives three shells. Without the
    # progress line, an encoded command's progress records come back as CLIXML noise. (In an SSH session
    # %USERDOMAIN% doesn't name the account, so the task's user is the current Windows identity.)
    $script = "`$ProgressPreference = 'SilentlyContinue'`n" + $script
    $b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    ssh -o BatchMode=yes -o ConnectTimeout=15 -o LogLevel=ERROR $Desktop "powershell -NoProfile -NonInteractive -EncodedCommand $b64"
    if ($LASTEXITCODE) { throw "ssh: exit $LASTEXITCODE" }
}

function Need-Run { if (-not $Run) { throw "$Command needs a run name" } }

switch ($Command) {
    'sync' {
        foreach ($d in 'scripts\nnue', 'scripts\nnue-reference', 'cli') { New-Item -ItemType Directory -Force "$Share\$d" | Out-Null }
        Copy-Item "$Repo\scripts\nnue\*.py" "$Share\scripts\nnue\"
        Copy-Item "$Repo\scripts\nnue-reference\nnue_reference.py" "$Share\scripts\nnue-reference\"
        Copy-Item "$Repo\scripts\eval-games.py" "$Share\scripts\"
        Copy-Item "$Repo\scripts\nnue\train-desktop.cmd", "$Repo\scripts\nnue\setup-desktop.cmd", "$Repo\scripts\nnue\data-desktop.cmd" "$Share\"
        # The command line tool (game import, nnue data) for data-desktop.cmd: a Release build, framework-dependent
        # (the desktop has .NET 8), under 1 MB.
        dotnet build "$Repo\src\ChessBruteforcer.Cli" -c Release --nologo -v q
        if ($LASTEXITCODE) { throw "the build of the Cli failed" }
        Copy-Item "$Repo\src\ChessBruteforcer.Cli\bin\Release\net8.0\*" "$Share\cli\" -Recurse -Force
        Get-ChildItem "$Share\*.cmd", "$Share\scripts", "$Share\scripts\nnue", "$Share\scripts\nnue-reference" -File |
            Format-Table Name, Length, LastWriteTime -AutoSize
        "cli: $((Get-ChildItem "$Share\cli" -File).Count) files"
    }
    { $_ -in 'train', 'data' } {
        Need-Run
        # Runs (or months) separated by a lone + go one after another in one task: train a --hidden 512 + b --mates,
        # data 2026-07 + 2026-08
        $cmd = if ($Command -eq 'train') { 'train-desktop.cmd' } else { 'data-desktop.cmd' }
        $runs = [System.Collections.Generic.List[string[]]]::new()
        $current = [System.Collections.Generic.List[string]]::new()
        foreach ($word in @($Run) + $Options) {
            if ($word -eq '+') { $runs.Add($current.ToArray()); $current.Clear() } else { $current.Add($word) }
        }
        $runs.Add($current.ToArray())
        if ($runs | Where-Object { $_.Count -eq 0 }) { throw "a + with no run after it" }
        $line = ($runs | ForEach-Object { "call `"$Local\$cmd`" $($_ -join ' ')" }) -join ' & '
        if (($runs | ForEach-Object { $_ }) -join ' ' -match "['""]") { throw "no quotes in the options, please" }
        $name = $(if ($Command -eq 'train') { 'nnue ' } else { 'nnue data ' }) + (($runs | ForEach-Object { $_[0] }) -join ' then ')
        Invoke-Desktop @"
`$name = '$name'
if ((Get-ScheduledTask -TaskPath '$TaskPath' -TaskName `$name -ErrorAction SilentlyContinue).State -eq 'Running') { throw "`$name is running" }
`$a = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c $($line -replace "'", "''")' -WorkingDirectory '$Local'
`$p = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType S4U -RunLevel Limited
`$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 24) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskPath '$TaskPath' -TaskName `$name -Action `$a -Principal `$p -Settings `$s -Force | Out-Null
Start-ScheduledTask -TaskPath '$TaskPath' -TaskName `$name
Start-Sleep 3
Get-ScheduledTask -TaskPath '$TaskPath' -TaskName `$name | Select-Object TaskName, State | Format-Table -AutoSize
"@
        foreach ($r in $runs) {
            $log = if ($Command -eq 'train') { "$Share\nets\$($r[0]).log (and .err)" } else { "$Share\data-$($r[0]).log" }
            "Follow it: $log, or scripts\nnue-desktop.ps1 status $($r[0])"
        }
    }
    'status' {
        # The .err is held open by cmd's redirection, so it is read with ReadWrite sharing (Claude-Notebook, 2026-10-14).
        $tail = if ($Run) { "Tail '$Local\nets\$Run.log' 6; Tail '$Local\nets\$Run.err' 4; Tail '$Local\data-$Run.log' 8" } else { '' }
        Invoke-Desktop @"
function Tail(`$p, `$n) {
    try { `$r = New-Object IO.StreamReader([IO.File]::Open(`$p, 'Open', 'Read', 'ReadWrite')); `$t = `$r.ReadToEnd(); `$r.Close() } catch { return }
    Write-Output "--- `$p"
    `$t -split "``r?``n" | Where-Object { `$_ } | Select-Object -Last `$n
}
Get-ScheduledTask -TaskPath '$TaskPath' -ErrorAction SilentlyContinue | ForEach-Object { `$i = `$_ | Get-ScheduledTaskInfo; [pscustomobject]@{ Task = `$_.TaskName; State = `$_.State; LastRun = `$i.LastRunTime; LastResult = `$i.LastTaskResult } } | Format-Table -AutoSize
nvidia-smi --query-gpu=utilization.gpu,memory.used,memory.total,power.draw,temperature.gpu --format=csv,noheader
$tail
"@
    }
    'stop' {
        Need-Run
        Invoke-Desktop @"
`$run = [regex]::Escape('$Run')
Get-ScheduledTask -TaskPath '$TaskPath' -ErrorAction SilentlyContinue | Where-Object { `$_.TaskName -match "^nnue (data )?(.* then )?`$run( then |`$)" } | Stop-ScheduledTask
# A training run's python, or a month's steps (eval-games.py, the Cli's import or nnue data, the check).
Get-CimInstance Win32_Process | Where-Object { `$_.Name -in 'python.exe', 'dotnet.exe' -and (`$_.CommandLine -like '*\runs\$Run\nnue\train.py*' -or `$_.CommandLine -like '*lichess_db_standard_rated_$Run.pgn.zst*' -or `$_.CommandLine -like '*eval-games-$Run.*' -or `$_.CommandLine -like '*records.py*check*nnue\$Run*') } | ForEach-Object { Stop-Process -Id `$_.ProcessId -Force; "stopped `$(`$_.Name) `$(`$_.ProcessId)" }
`$err = if (Test-Path '$Local\data-$Run.log') { '$Local\data-$Run.log' } else { '$Local\nets\$Run.err' }
Add-Content `$err "`$(Get-Date -Format 'ddd dd/MM/yyyy HH:mm:ss'): stopped from the laptop"
"@
    }
    'fetch' {
        Need-Run
        $main = Split-Path -Parent (git -C $Repo rev-parse --path-format=absolute --git-common-dir)
        $nets = Join-Path $main 'games\nnue\nets'
        foreach ($f in "$Run.nnue", "$Run.log") {
            if (Test-Path "$nets\$f") { throw "$nets\$f is already there" }
        }
        foreach ($f in "$Run.nnue", "$Run.log") { Copy-Item "$Share\nets\$f" "$nets\" }
        Get-ChildItem "$nets\$Run.nnue", "$nets\$Run.log" | Format-Table Name, Length, LastWriteTime -AutoSize
        "sha256 of the file: $((Get-FileHash "$nets\$Run.nnue").Hash.ToLower())"
    }
}
