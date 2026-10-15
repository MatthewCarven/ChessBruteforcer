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
# Lichess's SHA-256 for the month goes beside it as <name>.sha256 (make-book.py count checks it), and its
# line of standard/counts.txt ("<name> <games>") as <name>.count (index-month.py rows checks the game count
# against it: the only check that sees a game lost, as July 2026's first index lost 13).  Run on a month
# that's already there, it downloads nothing and saves whichever of the two is missing.
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
$countFile = "$final.count"
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

function Get-List([string] $file) {
    # One of Lichess's lists (sha256sums.txt, counts.txt), or $null when the server didn't send it.
    $list = curl.exe -sS --max-time 120 "https://database.lichess.org/standard/$file"
    if ($LASTEXITCODE -ne 0 -or -not $list) { Log "couldn't fetch Lichess's $file (curl exit $LASTEXITCODE); run this again later to save its line"; return $null }
    return $list
}

function Save-Sha256 {
    if (Test-Path -LiteralPath $sums) { return }
    $list = Get-List 'sha256sums.txt'
    if (-not $list) { return }
    $line = $list | Where-Object { $_ -match "^[0-9a-f]{64}  $([regex]::Escape($name))$" } | Select-Object -First 1
    if ($line) { Set-Content -LiteralPath $sums -Value $line -Encoding ascii; Log "Lichess's SHA-256: $($line.Substring(0, 64))" }
    else { Log "no SHA-256 for $name on Lichess's list (yet?); the count will say it couldn't check" }
}

function Save-Count {
    if (Test-Path -LiteralPath $countFile) { return }
    $list = Get-List 'counts.txt'
    if (-not $list) { return }
    $line = $list | Where-Object { $_ -match "^$([regex]::Escape($name)) \d+$" } | Select-Object -First 1
    if ($line) { Set-Content -LiteralPath $countFile -Value $line -Encoding ascii; Log ("Lichess's game count: {0:N0}" -f [long]($line -split ' ')[1]) }
    else { Log "no game count for $name on Lichess's counts.txt (yet?); the index will say it couldn't check" }
}

function Get-ServerFile {
    # The file's size and date from a HEAD request, asked again while the server doesn't give both. On
    # 2026-10-16 a slow server answered nothing: the size became 0 and a blank date went into the stamp,
    # which would have stopped every later resume as "the server's file changed".
    for ($i = 1; $i -le 30; $i++) {
        $head = curl.exe -sSI --max-time 60 $url
        $length = ($head | Select-String '^Content-Length: (\d+)').Matches | Select-Object -First 1
        $date = ($head | Select-String '^Last-Modified: (.+)').Matches | Select-Object -First 1
        if ($length -and $date -and [long]$length.Groups[1].Value -gt 0) {
            return [long]$length.Groups[1].Value, $date.Groups[1].Value.Trim()
        }
        Log "the server didn't give the file's size and date (try $i of 30); again in a minute"
        Start-Sleep -Seconds 60
    }
    Log "STOPPING: no size and date from the server in 30 tries (run this again to carry on)"
    exit 1
}

if ($LimitRate -in '0', 'none') { $LimitRate = '' }
if ($LimitRate -and $LimitRate -notmatch '^\d+[kKmMgG]?$') { Log "STOPPING: -LimitRate $LimitRate isn't a speed curl knows (500k, 1m, or 0 for none)"; exit 1 }
$rateArgs = if ($LimitRate) { @('--limit-rate', $LimitRate) } else { @() }

Wait-Dir
if (Test-Path -LiteralPath $final) { Log "$final is already here: nothing to download"; Save-Sha256; Save-Count; return }
if (-not (Test-Path $part) -and (Test-Path $first)) {
    Rename-Item $first (Split-Path $part -Leaf)
    Set-Content $stamp 'Sat, 03 Oct 2026 05:06:59 GMT'   # the server's date when the first 1 GB was fetched
    Log "carrying on from the first 1 GB ($first)"
}

$expected, $modified = Get-ServerFile
if ((Test-Path $stamp) -and -not "$(Get-Content $stamp)".Trim()) {
    Remove-Item $stamp                                   # a blank one, from before the HEAD was asked again
    Log "the part's date stamp was blank; taking the server's ($modified)"
}
if (Test-Path $stamp) {
    $was = (Get-Content $stamp).Trim()
    if ($was -ne $modified) { Log "STOPPING: the server's file changed ($was, now $modified); the part can't be continued"; exit 1 }
} else {
    Set-Content $stamp $modified
}
Log "=== $name`: $expected bytes on the server ($modified)"
Log $(if ($LimitRate) { "speed capped at $LimitRate a second" } else { 'no speed cap' })
Save-Sha256
Save-Count

for ($try = 1; $try -le 100; $try++) {
    Wait-Dir
    $have = if (Test-Path -LiteralPath $part) { (Get-Item -LiteralPath $part).Length } else { 0 }
    if ($have -ge $expected) { break }
    Log ('attempt {0}: {1:N2} of {2:N2} GB' -f $try, ($have / 1GB), ($expected / 1GB))
    # Under 1 KB/s for 2 minutes ends the try (curl exit 28), and the loop resumes from the last byte: on
    # 2026-10-16 May's connection froze at 241,664 bytes and curl waited on it, while a fresh one got 787 KB/s.
    curl.exe -sS -C - --speed-limit 1024 --speed-time 120 @rateArgs -o $part $url
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
