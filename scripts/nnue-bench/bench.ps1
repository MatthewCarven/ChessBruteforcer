# The engine's `bench` with each evaluation in turn, alternated round by round (this laptop runs at two
# speeds minute to minute, so arms must take turns), one process per run, below-normal priority.
#   pwsh bench.ps1 -Engine <ChessBruteforcer.Engine.dll> -Depth 9 -Rounds 6 -Arms sf1=<eval.txt>,net256=<x.nnue>
# Prints each run, then per arm the median and best speed and the change against the first arm.
param(
    [Parameter(Mandatory)][string]$Engine,
    [int]$Depth = 9,
    [int]$Rounds = 6,
    [Parameter(Mandatory)][string[]]$Arms,
    [string]$Tsv
)

$arms = foreach ($arm in $Arms) { $name, $file = $arm -split '=', 2; [pscustomobject]@{ Name = $name; File = $file } }
$runs = @()
for ($round = 1; $round -le $Rounds; $round++) {
    # Rotate the order each round, so no arm always runs first or after the same neighbour.
    $order = @(0..($arms.Count - 1) | ForEach-Object { $arms[($_ + $round - 1) % $arms.Count] })
    foreach ($arm in $order) {
        $start = New-Object System.Diagnostics.ProcessStartInfo 'dotnet', "`"$Engine`" bench $Depth --eval `"$($arm.File)`""
        $start.RedirectStandardOutput = $true
        $start.UseShellExecute = $false
        $process = [System.Diagnostics.Process]::Start($start)
        $process.PriorityClass = 'BelowNormal'
        $text = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        if ($text -notmatch 'bench depth \d+: ([\d,]+) nodes in ([\d.]+)s = (\d+) knps') { throw "no bench line from $($arm.Name):`n$text" }
        $run = [pscustomobject]@{ Round = $round; Arm = $arm.Name; Nodes = [long]($Matches[1] -replace ',', '');
                                   Seconds = [double]$Matches[2]; Knps = [int]$Matches[3] }
        $runs += $run
        "{0}  round {1}  {2,-10} {3,12:N0} nodes {4,7:0.00} s {5,6} knps" -f (Get-Date -Format 'HH:mm:ss'), $round, $run.Arm, $run.Nodes, $run.Seconds, $run.Knps
    }
}
if ($Tsv) { $runs | Export-Csv -Path $Tsv -Delimiter "`t" -NoTypeInformation }

function Median($values) { $s = @($values | Sort-Object); if ($s.Count % 2) { $s[[int][math]::Floor($s.Count / 2)] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 } }
$baseline = Median ($runs | Where-Object Arm -eq $arms[0].Name | ForEach-Object Knps)
""
"arm          nodes (tree)   median knps   best knps   vs $($arms[0].Name)"
foreach ($arm in $arms) {
    $mine = $runs | Where-Object Arm -eq $arm.Name
    $median = Median ($mine | ForEach-Object Knps)
    "{0,-10} {1,14:N0} {2,13:N0} {3,11:N0}   {4,6:+0.0;-0.0}%" -f $arm.Name, $mine[0].Nodes, $median, ($mine | Measure-Object Knps -Maximum).Maximum, (100.0 * $median / $baseline - 100)
}
