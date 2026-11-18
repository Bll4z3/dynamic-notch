# Dynamic Notch performance run (dev tool).
# Starts the app with --perf-all, samples process CPU / working set / GPU during the idle and music windows,
# runs dotnet-counters during idle, then prints the in-app perf.log (startup, frame stats per animation).
# Usage: powershell -File tools\perf\perf-run.ps1 [-Exe path] [-Out dir]
param(
    [string]$Exe = "$PSScriptRoot\..\..\src\DynamicNotch.WinUI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\DynamicNotch.exe",
    [string]$Out = "$PSScriptRoot\results"
)
$ErrorActionPreference = "Continue"
New-Item -ItemType Directory -Force $Out | Out-Null
$log = "$env:LOCALAPPDATA\DynamicNotch\perf.log"
if (Test-Path $log) { Remove-Item $log }
Get-Process DynamicNotch -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

function Sample-Window($p, [double]$seconds) {
    $p.Refresh(); $c0 = $p.TotalProcessorTime; $t0 = Get-Date
    $gpu = @()
    $end = $t0.AddSeconds($seconds)
    while ((Get-Date) -lt $end) {
        try {
            $s = (Get-Counter "\GPU Engine(pid_$($p.Id)_*)\Utilization Percentage" -ErrorAction Stop).CounterSamples
            $gpu += ($s | Measure-Object CookedValue -Sum).Sum
        } catch { Start-Sleep -Milliseconds 900 }
    }
    $p.Refresh()
    $cpu = ($p.TotalProcessorTime - $c0).TotalSeconds / ((Get-Date) - $t0).TotalSeconds / [Environment]::ProcessorCount * 100
    [pscustomobject]@{ CpuPct = [math]::Round($cpu, 2); WorkingSetMB = [math]::Round($p.WorkingSet64 / 1MB, 1);
                       PrivateMB = [math]::Round($p.PrivateMemorySize64 / 1MB, 1);
                       GpuPctAvg = if ($gpu.Count) { [math]::Round(($gpu | Measure-Object -Average).Average, 2) } else { "n/a" } }
}

$p = Start-Process $Exe -ArgumentList "--perf-all" -PassThru
Start-Sleep -Seconds 6          # startup settles; idle window is ~1.6 s .. 28.8 s
$counters = Join-Path $Out "idle-counters.csv"
if (Test-Path $counters) { Remove-Item $counters }
$job = Start-Job { param($id, $file) & dotnet-counters collect -p $id --duration 00:00:15 --format csv -o $file --counters System.Runtime 2>&1 | Out-Null } -ArgumentList $p.Id, $counters
$idle = Sample-Window $p 20
Wait-Job $job -Timeout 25 | Out-Null
Start-Sleep -Seconds 4          # music window: ~32 s .. 41.6 s
$music = Sample-Window $p 8
Start-Sleep -Seconds 26         # the animation traces
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }

"=== Idle (collapsed) ==="; $idle | Format-List
"=== Expanded music page ==="; $music | Format-List
if (Test-Path $counters) {
    "=== dotnet-counters (idle) ==="
    $rows = Import-Csv $counters
    $rows | Group-Object "Counter Name" | ForEach-Object {
        $v = $_.Group | ForEach-Object { [double]$_."Mean/Increment" }
        "{0,-55} mean {1,10:0.###}  max {2,10:0.###}" -f $_.Name, ($v | Measure-Object -Average).Average, ($v | Measure-Object -Maximum).Maximum
    } | Where-Object { $_ -match "CPU|Working|Allocat|GC Count|Heap Size|Committed|Timer|ThreadPool Completed" }
}
"=== perf.log ==="
if (Test-Path $log) { Get-Content $log; Copy-Item $log (Join-Path $Out "perf.log") -Force }
