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
  scripts\nnue-desktop.ps1 status [<run>]          the task, the GPU, and the run's last log lines
  scripts\nnue-desktop.ps1 stop <run>              end the run, and the task it is in (with any runs queued after it); its
                                                   last checkpoint stays: train ... --resume carries on
  scripts\nnue-desktop.ps1 fetch <run>             <run>.nnue and <run>.log into games\nnue\nets\ of the main checkout

The task runs as Admin without a stored password (S4U: local files only, no window on Matthew's screen), at the task
scheduler's below-normal priority. Big copies onto the share go through capped_copy.py (Matthew's 8 MB/s rule), not this.
#>
param(
    [Parameter(Mandatory, Position = 0)][ValidateSet('sync', 'train', 'status', 'stop', 'fetch')][string]$Command,
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
        foreach ($d in 'scripts\nnue', 'scripts\nnue-reference') { New-Item -ItemType Directory -Force "$Share\$d" | Out-Null }
        Copy-Item "$Repo\scripts\nnue\*.py" "$Share\scripts\nnue\"
        Copy-Item "$Repo\scripts\nnue-reference\nnue_reference.py" "$Share\scripts\nnue-reference\"
        Copy-Item "$Repo\scripts\nnue\train-desktop.cmd", "$Repo\scripts\nnue\setup-desktop.cmd" "$Share\"
        Get-ChildItem "$Share\*.cmd", "$Share\scripts\nnue", "$Share\scripts\nnue-reference" -File |
            Format-Table Name, Length, LastWriteTime -AutoSize
    }
    'train' {
        Need-Run
        # Runs separated by a lone + go one after another in one task: train a --hidden 512 + b --mates
        $runs = [System.Collections.Generic.List[string[]]]::new()
        $current = [System.Collections.Generic.List[string]]::new()
        foreach ($word in @($Run) + $Options) {
            if ($word -eq '+') { $runs.Add($current.ToArray()); $current.Clear() } else { $current.Add($word) }
        }
        $runs.Add($current.ToArray())
        if ($runs | Where-Object { $_.Count -eq 0 }) { throw "a + with no run after it" }
        $line = ($runs | ForEach-Object { "call `"$Local\train-desktop.cmd`" $($_ -join ' ')" }) -join ' & '
        if (($runs | ForEach-Object { $_ }) -join ' ' -match "['""]") { throw "no quotes in the options, please" }
        $name = 'nnue ' + (($runs | ForEach-Object { $_[0] }) -join ' then ')
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
        foreach ($r in $runs) { "Follow it: $Share\nets\$($r[0]).log (and .err), or scripts\nnue-desktop.ps1 status $($r[0])" }
    }
    'status' {
        # The .err is held open by cmd's redirection, so it is read with ReadWrite sharing (Claude-Notebook, 2026-10-14).
        $tail = if ($Run) { "Tail '$Local\nets\$Run.log' 6; Tail '$Local\nets\$Run.err' 4" } else { '' }
        Invoke-Desktop @"
function Tail(`$p, `$n) {
    Write-Output "--- `$p"
    try { `$r = New-Object IO.StreamReader([IO.File]::Open(`$p, 'Open', 'Read', 'ReadWrite')); `$t = `$r.ReadToEnd(); `$r.Close() } catch { return }
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
Get-ScheduledTask -TaskPath '$TaskPath' -ErrorAction SilentlyContinue | Where-Object { `$_.TaskName -eq 'nnue $Run' -or `$_.TaskName -like 'nnue $Run then *' -or `$_.TaskName -like '* then $Run' -or `$_.TaskName -like '* then $Run then *' } | Stop-ScheduledTask
Get-CimInstance Win32_Process -Filter "Name = 'python.exe'" | Where-Object { `$_.CommandLine -like '*\runs\$Run\nnue\train.py*' } | ForEach-Object { Stop-Process -Id `$_.ProcessId -Force; "stopped python `$(`$_.ProcessId)" }
Add-Content '$Local\nets\$Run.err' "`$(Get-Date -Format 'ddd dd/MM/yyyy HH:mm:ss'): stopped from the laptop"
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
