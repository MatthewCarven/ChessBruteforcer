# The engine's `bench` with each evaluation in turn, alternated round by round (this laptop runs at two
# speeds minute to minute, so arms must take turns), one process per run, below-normal priority.
#   pwsh bench.ps1 -Engine <ChessBruteforcer.Engine.dll> -Depth 9 -Rounds 6 -Arms sf1=<eval.txt>,net256=<x.nnue>
# An arm can also name its own build and environment, to compare two builds or two settings on one net:
#   -Arms "old=<x.nnue>;engine=<old dll>,new=<x.nnue>;engine=<new dll>,new256=<x.nnue>;engine=<new dll>;env:CB_NNUE_SIMD=256"
# Prints each run, then per arm the median and best speed and the change against the first arm: by the
# engine's own clock, and by the process's CPU time less its start-up (measured per arm with `bench 1`),
# which leaves out the time a below-normal process spends waiting for a CPU beside the bot.
param(
    [Parameter(Mandatory)][string]$Engine,
    [int]$Depth = 9,
    [int]$Rounds = 6,
    [Parameter(Mandatory)][string[]]$Arms,
    [string]$Tsv
)

# (pwsh -File hands a list over as one string, so split it here; and $armList, since PowerShell's
# names ignore case, so $arms would be the [string[]] parameter itself)
$armList = foreach ($arm in ($Arms | ForEach-Object { $_ -split ',' })) {
    $name, $rest = $arm -split '=', 2
    $parts = $rest -split ';'
    $spec = [pscustomobject]@{ Name = $name; File = $parts[0]; Engine = $Engine; Env = @{}; StartCpu = 0.0 }
    foreach ($part in $parts | Select-Object -Skip 1 | Where-Object { $_ }) {   # (not $parts[1..n]: 1..0 counts down)
        if ($part -like 'engine=*') { $spec.Engine = $part.Substring(7) }
        elseif ($part -like 'env:*') { $key, $value = $part.Substring(4) -split '=', 2; $spec.Env[$key] = $value }
        else { throw "arm $name`: '$part' is neither engine=<dll> nor env:NAME=VALUE" }
    }
    $spec
}

function Invoke-Bench($arm, [int]$depth) {
    $start = New-Object System.Diagnostics.ProcessStartInfo 'dotnet', "`"$($arm.Engine)`" bench $depth --eval `"$($arm.File)`""
    $start.RedirectStandardOutput = $true
    $start.UseShellExecute = $false
    foreach ($key in $arm.Env.Keys) { $start.Environment[$key] = $arm.Env[$key] }
    $process = [System.Diagnostics.Process]::Start($start)
    $process.PriorityClass = 'BelowNormal'
    $text = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    if ($text -notmatch 'bench depth \d+: ([\d,]+) nodes in ([\d.]+)s = (\d+) knps') { throw "no bench line from $($arm.Name):`n$text" }
    [pscustomobject]@{ Nodes = [long]($Matches[1] -replace ',', ''); Seconds = [double]$Matches[2]; Knps = [int]$Matches[3];
                       Cpu = $process.TotalProcessorTime.TotalSeconds }
}

function Median($values) { $s = @($values | Sort-Object); if ($s.Count % 2) { $s[[int][math]::Floor($s.Count / 2)] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 } }

# Start-up (the runtime, the JIT, loading and checking the net): `bench 1`'s CPU time, the median of three.
foreach ($arm in $armList) {
    $arm.StartCpu = Median (1..3 | ForEach-Object { (Invoke-Bench $arm 1).Cpu })
    "{0,-10} start-up {1:0.000} s of CPU" -f $arm.Name, $arm.StartCpu
}

$runs = @()
for ($round = 1; $round -le $Rounds; $round++) {
    # Rotate the order each round, so no arm always runs first or after the same neighbour.
    $order = @(0..($armList.Count - 1) | ForEach-Object { $armList[($_ + $round - 1) % $armList.Count] })
    foreach ($arm in $order) {
        $result = Invoke-Bench $arm $Depth
        $cpuKnps = [int]($result.Nodes / [math]::Max(0.001, $result.Cpu - $arm.StartCpu) / 1000)
        $run = [pscustomobject]@{ Round = $round; Arm = $arm.Name; Nodes = $result.Nodes; Seconds = $result.Seconds;
                                   Knps = $result.Knps; Cpu = $result.Cpu; CpuKnps = $cpuKnps }
        $runs += $run
        "{0}  round {1}  {2,-10} {3,12:N0} nodes {4,7:0.00} s {5,6} knps   cpu {6,6:0.00} s {7,6} knps" -f (Get-Date -Format 'HH:mm:ss'), $round, $run.Arm, $run.Nodes, $run.Seconds, $run.Knps, $run.Cpu, $run.CpuKnps
    }
}
if ($Tsv) { $runs | Export-Csv -Path $Tsv -Delimiter "`t" -NoTypeInformation }

$baseline = Median ($runs | Where-Object Arm -eq $armList[0].Name | ForEach-Object Knps)
$cpuBaseline = Median ($runs | Where-Object Arm -eq $armList[0].Name | ForEach-Object CpuKnps)
""
"arm          nodes (tree)   median knps   best knps   vs $($armList[0].Name)    by CPU: median knps   vs $($armList[0].Name)"
foreach ($arm in $armList) {
    $mine = $runs | Where-Object Arm -eq $arm.Name
    $median = Median ($mine | ForEach-Object Knps)
    $cpuMedian = Median ($mine | ForEach-Object CpuKnps)
    "{0,-10} {1,14:N0} {2,13:N0} {3,11:N0}   {4,6:+0.0;-0.0}%   {5,19:N0}   {6,6:+0.0;-0.0}%" -f $arm.Name, $mine[0].Nodes, $median, ($mine | Measure-Object Knps -Maximum).Maximum, (100.0 * $median / $baseline - 100), $cpuMedian, (100.0 * $cpuMedian / $cpuBaseline - 100)
}
