# Delete the files the project no longer needs, after showing them and asking.  Nothing else is touched.
#
#   pwsh -File scripts\clean-up.ps1
#
# Offered:
#   - games\*.pgn that have already been imported into a .cbg of the same name
#     (2026-09-tables.pgn, 2026-09-part-tables.pgn), and the unpacked 2013 PGNs whose .zst is kept;
#   - a month's download, games\lichess_db_standard_rated_<month>.pgn.zst, but only once
#     bot\books\counts\<month>.counts exists (the count writes it last, so it is finished).
# Kept: every .cbg, the 2013 .zst downloads (small), the tables, 2026-09-part.cbg (the only copy of all
# of a 2026 day's games once September's download is gone).
Set-Location (Split-Path $PSScriptRoot -Parent)
$offer = @()
foreach ($pgn in Get-ChildItem games -Filter *.pgn -File) {
    $cbg = Join-Path games ($pgn.BaseName + '.cbg')
    $zst = Join-Path games ($pgn.Name + '.zst')
    if (Test-Path $cbg) { $offer += [pscustomobject]@{ File = $pgn.FullName; GB = $pgn.Length / 1GB; Why = "imported into $(Split-Path $cbg -Leaf)" } }
    elseif (Test-Path $zst) { $offer += [pscustomobject]@{ File = $pgn.FullName; GB = $pgn.Length / 1GB; Why = "unpacked copy; $(Split-Path $zst -Leaf) is kept" } }
}
foreach ($zst in Get-ChildItem games -Filter 'lichess_db_standard_rated_*.pgn.zst' -File) {
    $month = $zst.Name -replace '^lichess_db_standard_rated_(.+)\.pgn\.zst$', '$1'
    if (Test-Path "bot\books\counts\$month.counts") {
        $offer += [pscustomobject]@{ File = $zst.FullName; GB = $zst.Length / 1GB; Why = "counted into bot\books\counts\$month.counts" }
    }
}
if ($offer.Count -eq 0) { "Nothing to delete right now."; return }
$offer | ForEach-Object { '{0,7:N2} GB  {1}  ({2})' -f $_.GB, (Split-Path $_.File -Leaf), $_.Why }
'{0,7:N2} GB in all; C: has {1:N1} GB free now.' -f ($offer | Measure-Object GB -Sum).Sum, ((Get-PSDrive C).Free / 1GB)
$answer = Read-Host 'Type YES to delete these'
if ($answer -ne 'YES') { 'Nothing deleted.'; return }
foreach ($item in $offer) { Remove-Item -LiteralPath $item.File; "deleted $(Split-Path $item.File -Leaf)" }
'C: has {0:N1} GB free now.' -f ((Get-PSDrive C).Free / 1GB)
