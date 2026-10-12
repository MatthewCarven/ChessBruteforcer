# Download (or finish downloading) one month of Lichess's standard rated games into games\.
#
#   scripts\download-lichess-month.cmd 2026-09
#
# Resumes from whatever is already there: games\lichess_db_standard_rated_<month>.pgn.zst.part,
# or the first-1GB file of 2026-10-12 (bytes 0 .. 1,073,741,823 of the same file), which it
# continues, so nothing is downloaded twice.  Retries when the connection drops.  When the
# size matches the server's, the file gets its proper name.  If Lichess has replaced the file
# since the part was started (its Last-Modified changed), it stops rather than splice two files.
# Everything goes to games\download-<month>.log.
param([Parameter(Mandatory)] [string] $Month)
$ErrorActionPreference = 'Continue'
Set-Location (Split-Path $PSScriptRoot -Parent)
$name = "lichess_db_standard_rated_$Month.pgn.zst"
$url = "https://database.lichess.org/standard/$name"
$final = "games\$name"
$part = "$final.part"
$first = "games\lichess_db_standard_rated_$Month.first-1GB.pgn.zst"
$stamp = "$part.last-modified"
$log = "games\download-$Month.log"

function Log([string] $text) {
    $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $text
    Write-Host $line
    Add-Content -Path $log -Value $line
}

if (Test-Path $final) { Log "$final is already here: nothing to do"; return }
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

for ($try = 1; $try -le 100; $try++) {
    $have = if (Test-Path $part) { (Get-Item $part).Length } else { 0 }
    if ($have -ge $expected) { break }
    Log ('attempt {0}: {1:N2} of {2:N2} GB' -f $try, ($have / 1GB), ($expected / 1GB))
    curl.exe -sS -C - -o $part $url
    if ($LASTEXITCODE -ne 0) { Log "curl exit $LASTEXITCODE; again in a minute"; Start-Sleep -Seconds 60 }
}

$have = (Get-Item $part).Length
if ($have -eq $expected) {
    Rename-Item $part $name
    Remove-Item $stamp
    Log "=== finished: $final, $have bytes"
} else {
    Log "=== not finished: $have of $expected bytes (run it again to carry on)"
    exit 1
}
