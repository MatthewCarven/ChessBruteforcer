# Download (or finish downloading) one month of Lichess's standard rated games into games\, or -Dir.
#
#   scripts\download-lichess-month.cmd 2026-09
#   pwsh -File scripts\download-lichess-month.ps1 2026-07 -Dir \\id10terror-desktop\chessbruteforcer\lichess
#   pwsh -File scripts\download-lichess-month.ps1 2026-07 -LimitRate 500k   (at most ~500 KB/s; 0 = no cap)
#
# Resumes from whatever is already there: <Dir>\lichess_db_standard_rated_<month>.pgn.zst.part,
# or the first-1GB file of 2026-10-12 (bytes 0 .. 1,073,741,823 of the same file), which it
# continues, so nothing is downloaded twice.  Retries when the connection drops.  When the
# size matches the server's, the file gets its proper name.  If Lichess has replaced the file
# since the part was started (its Last-Modified changed), it stops rather than splice two files.
# Lichess's SHA-256 for the month goes beside it as <name>.sha256 (make-book.py count checks it).
#
# -Dir can be a network share (the months live on Matthew's desktop from 2026-10-13).  While it
# can't be reached the script waits, up to 6 hours, without using up attempts; a write cut off by
# a drop is picked up by the next attempt from the bytes that reached the share (curl -C - only
# ever appends), and anything that went wrong in the middle shows up in the checksum.  The log
# stays on this machine: games\download-<month>.log.
#
# -LimitRate caps the speed (curl's --limit-rate: 500k, 1m, ...; k = 1,024 bytes), so the download
# leaves room on the line for everyone else (2026-10-15: at full speed YouTube at home waited on it).
# No cap unless asked; the month queue asks for one.
param([Parameter(Mandatory)] [string] $Month, [string] $Dir = 'games', [string] $LimitRate = '')
$ErrorActionPreference = 'Continue'
Set-Location (Split-Path $PSScriptRoot -Parent)
$name = "lichess_db_standard_rated_$Month.pgn.zst"
$url = "https://database.lichess.org/standard/$name"
$final = Join-Path $Dir $name
$part = "$final.part"
$first = Join-Path $Dir "lichess_db_standard_rated_$Month.first-1GB.pgn.zst"
$stamp = "$part.last-modified"
$sums = "$final.sha256"
$log = "games\download-$Month.log"

function Log([string] $text) {
    $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $text
    Write-Host $line
    Add-Content -Path $log -Value $line
}

function Wait-Dir {
    # Until $Dir can be reached again (a share dropping out), or 6 hours.
    $since = $null
    while (-not (Test-Path -LiteralPath $Dir)) {
        if (-not $since) { $since = Get-Date; Log "can't reach $Dir; waiting for it (up to 6 hours)" }
        if (((Get-Date) - $since).TotalHours -ge 6) { Log "STOPPING: $Dir unreachable for 6 hours (run this again to carry on)"; exit 1 }
        Start-Sleep -Seconds 30
    }
    if ($since) { Log ('{0} is back after {1:N0} min' -f $Dir, ((Get-Date) - $since).TotalMinutes) }
}

if ($LimitRate -in '0', 'none') { $LimitRate = '' }
if ($LimitRate -and $LimitRate -notmatch '^\d+[kKmMgG]?$') { Log "STOPPING: -LimitRate $LimitRate isn't a speed curl knows (500k, 1m, or 0 for none)"; exit 1 }
$rateArgs = if ($LimitRate) { @('--limit-rate', $LimitRate) } else { @() }

Wait-Dir
if (Test-Path -LiteralPath $final) { Log "$final is already here: nothing to do"; return }
if (-not (Test-Path $part) -and (Test-Path $first)) {
    Rename-Item $first (Split-Path $part -Leaf)
    Set-Content $stamp 'Sat, 03 Oct 2026 05:06:59 GMT'   # the server's date when the first 1 GB was fetched
    Log "carrying on from the first 1 GB ($first)"
}

$head = curl.exe -sI $url
$expected = [long](($head | Select-String 'Content-Length: (\d+)').Matches[0].Groups[1].Value)
$modified = ($head | Select-String 'Last-Modified: (.+)').Matches[0].Groups[1].Value.Trim()
if (Test-Path $stamp) {
    $was = (Get-Content $stamp).Trim()
    if ($was -ne $modified) { Log "STOPPING: the server's file changed ($was, now $modified); the part can't be continued"; exit 1 }
} else {
    Set-Content $stamp $modified
}
Log "=== $name`: $expected bytes on the server ($modified)"
Log $(if ($LimitRate) { "speed capped at $LimitRate a second" } else { 'no speed cap' })
if (-not (Test-Path -LiteralPath $sums)) {
    $line = curl.exe -sS https://database.lichess.org/standard/sha256sums.txt |
        Where-Object { $_ -match "^[0-9a-f]{64}  $([regex]::Escape($name))$" } | Select-Object -First 1
    if ($line) { Set-Content -LiteralPath $sums -Value $line -Encoding ascii; Log "Lichess's SHA-256: $($line.Substring(0, 64))" }
    else { Log "no SHA-256 for $name on Lichess's list (yet?); the count will say it couldn't check" }
}

for ($try = 1; $try -le 100; $try++) {
    Wait-Dir
    $have = if (Test-Path -LiteralPath $part) { (Get-Item -LiteralPath $part).Length } else { 0 }
    if ($have -ge $expected) { break }
    Log ('attempt {0}: {1:N2} of {2:N2} GB' -f $try, ($have / 1GB), ($expected / 1GB))
    curl.exe -sS -C - @rateArgs -o $part $url
    if ($LASTEXITCODE -ne 0) { Log "curl exit $LASTEXITCODE; again in a minute"; Start-Sleep -Seconds 60 }
}

Wait-Dir
$have = (Get-Item -LiteralPath $part).Length
if ($have -eq $expected) {
    Rename-Item -LiteralPath $part $name
    Remove-Item -LiteralPath $stamp
    Log "=== finished: $final, $have bytes"
} else {
    Log "=== not finished: $have of $expected bytes (run it again to carry on)"
    exit 1
}
