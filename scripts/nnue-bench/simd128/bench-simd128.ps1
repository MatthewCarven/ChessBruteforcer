# The 128-bit path on the desktop (NNUE.md, "128 bits for CPUs without AVX2"), run there over SSH from its own folder
# on the share (nnue\bench-simd128, beside nnue\bench-engine; booktable\engine is bt-a's and isn't touched):
#   powershell -File D:\-=Chess Bruteforcer=-\nnue\bench-simd128\bench-simd128.ps1 -Label quiet -Depth 9 -Rounds 4
# bench <Depth>, one thread, the arms alternated after a warm-up round: bench-engine's build (the scalar pass there, the
# "before"), this folder's build at its own width (128 on an i7-3770) and held to 0 by CB_NNUE_SIMD, each with h1024-3m;
# then both builds with h1536-3m. A build's nodes must be the same at every width (the arithmetic is exact). Lines also
# go to bench-<Label>.txt beside this script.
param([string]$Label = 'test', [int]$Depth = 9, [int]$Rounds = 4)
$ProgressPreference = 'SilentlyContinue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$old = Join-Path (Split-Path -Parent $here) 'bench-engine'
$log = Join-Path $here "bench-$Label.txt"
function Say([string]$t) { $line = "$(Get-Date -Format 'HH:mm:ss') $t"; Write-Output $line; Add-Content $log $line }
$arms = @(
    @{ Name = 'old-h1024'; Dll = "$old\ChessBruteforcer.Engine.dll"; Net = "$old\h1024-3m.nnue"; Simd = $null },
    @{ Name = 'new-h1024'; Dll = "$here\ChessBruteforcer.Engine.dll"; Net = "$old\h1024-3m.nnue"; Simd = $null },
    @{ Name = 'new0-h1024'; Dll = "$here\ChessBruteforcer.Engine.dll"; Net = "$old\h1024-3m.nnue"; Simd = '0' },
    @{ Name = 'old-h1536'; Dll = "$old\ChessBruteforcer.Engine.dll"; Net = "$old\h1536-3m.nnue"; Simd = $null },
    @{ Name = 'new-h1536'; Dll = "$here\ChessBruteforcer.Engine.dll"; Net = "$old\h1536-3m.nnue"; Simd = $null }
)
$cpu = Get-CimInstance Win32_Processor
$busy = @(Get-Process -Name ChessBruteforcer.Engine, dotnet -ErrorAction SilentlyContinue).Count
Say "$Label on ${env:COMPUTERNAME}: $($cpu.Name), load $($cpu.LoadPercentage)%, clock $($cpu.CurrentClockSpeed) MHz of $($cpu.MaxClockSpeed), $busy engine or dotnet processes before the start"
foreach ($r in 0..$Rounds) {
    foreach ($a in $arms) {
        $env:CB_NNUE_SIMD = $a.Simd
        Push-Location (Split-Path -Parent $a.Dll)
        $out = & dotnet $a.Dll bench $Depth --eval $a.Net
        Pop-Location
        $env:CB_NNUE_SIMD = $null
        $simd = ($out | Select-String -Pattern '^network simd: (\d+)' | ForEach-Object { $_.Matches[0].Groups[1].Value }) -join ''
        if (-not $simd) { $simd = '?' }
        if ($r -gt 0) { Say "round $r $($a.Name) simd $simd $($out | Select-Object -Last 1)" }
    }
}
Say "clock now $((Get-CimInstance Win32_Processor).CurrentClockSpeed) MHz"
