# Idle CPU A/B for the collapsed notch (dev tool). Runs the app once per NOTCH_VIS_FPS value and samples
# process CPU for 15 s after an 8 s settle. Prints the current media playback state too (the collapsed
# visualiser only animates while something plays).
param([string]$Exe = "$PSScriptRoot\..\..\src\DynamicNotch.WinUI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\DynamicNotch.exe",
      [string]$Fps = "0,30,60", [int]$Seconds = 15)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager,Windows.Media.Control,ContentType=WindowsRuntime]
$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | ? { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Media { $t = $asTask.MakeGenericMethod([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]).Invoke($null, @([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()))
  $s = $t.Result.GetCurrentSession(); if ($s) { "$($s.SourceAppUserModelId) $($s.GetPlaybackInfo().PlaybackStatus)" } else { "none" } }
foreach ($f in $Fps.Split(",")) {
  Get-Process DynamicNotch -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 600
  $env:NOTCH_VIS_FPS = $f
  $p = Start-Process $Exe -PassThru; Start-Sleep 8
  $p.Refresh(); $c0 = $p.TotalProcessorTime; $t0 = @{}; foreach ($x in $p.Threads) { try { $t0[$x.Id] = $x.TotalProcessorTime.TotalMilliseconds } catch {} }; Start-Sleep $Seconds; $p.Refresh()
  $cpu = ($p.TotalProcessorTime - $c0).TotalSeconds / $Seconds / [Environment]::ProcessorCount * 100
  $thr = ($p.Threads | ForEach-Object { try { [math]::Round($_.TotalProcessorTime.TotalMilliseconds - $t0[$_.Id]) } catch { 0 } } | Where-Object { $_ -gt 20 } | Sort-Object -Descending) -join "/"
  "visualiser {0,3} fps: idle CPU {1,5:0.00}%  WS {2,6:0.0} MB  busy threads (ms/{4}s): {5}  media: {3}" -f $f, $cpu, ($p.WorkingSet64 / 1MB), (Media), $Seconds, $thr
  Stop-Process -Id $p.Id -Force
}
Remove-Item Env:NOTCH_VIS_FPS
