# Verifies the Cherry Blossom scene really animates, and measures its CPU cost
# when active vs minimised (where it must pause). Read-only; isolated data folder.
param([string]$OutDir = "$env:TEMP\vytrix-qa")
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class WinCtl { [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }
"@
. (Join-Path $PSScriptRoot 'qa-common.ps1')
New-QaDataDir -TermsAccepted | Out-Null
$root = Split-Path $PSScriptRoot -Parent
$exe = Get-ChildItem (Join-Path $root 'src\Vytrix\bin') -Recurse -Filter VYTRIX.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-Process VYTRIX -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$p = Start-Process $exe.FullName -ArgumentList '--page', 'overview' -PassThru
Start-Sleep -Seconds 6
$p.Refresh(); [WinCtl]::SetForegroundWindow($p.MainWindowHandle) | Out-Null; Start-Sleep -Seconds 1

function Shot($n) { & (Join-Path $PSScriptRoot 'capture-window.ps1') -Out (Join-Path $OutDir $n) | Out-Null; Join-Path $OutDir $n }
function PixelDiff($a, $b, $x0, $y0, $x1, $y1) {
    $A = [System.Drawing.Bitmap]::FromFile($a); $B = [System.Drawing.Bitmap]::FromFile($b); $n = 0; $tot = 0
    for ($y = $y0; $y -lt $y1; $y += 3) { for ($x = $x0; $x -lt $x1; $x += 3) { $tot++; $c1 = $A.GetPixel($x, $y); $c2 = $B.GetPixel($x, $y); if ([Math]::Abs($c1.R - $c2.R) + [Math]::Abs($c1.G - $c2.G) + [Math]::Abs($c1.B - $c2.B) -gt 24) { $n++ } } }
    $A.Dispose(); $B.Dispose(); [Math]::Round(100.0 * $n / $tot, 2)
}
# Hero tree region of the Overview banner (right side of the banner).
$a = Shot 'motion-a.png'; Start-Sleep -Milliseconds 1500; $b = Shot 'motion-b.png'
"Tree region pixels changed in 1.5 s: $(PixelDiff $a $b 930 150 1270 370)%"

function CpuOver($sec) { $p.Refresh(); $c0 = $p.TotalProcessorTime; Start-Sleep -Seconds $sec; $p.Refresh(); ($p.TotalProcessorTime - $c0).TotalMilliseconds / ($sec * 1000) / [Environment]::ProcessorCount * 100 }
"CPU while active & animating: {0:0.00}% of machine" -f (CpuOver 10)
[WinCtl]::ShowWindow($p.MainWindowHandle, 6) | Out-Null   # SW_MINIMIZE
Start-Sleep -Seconds 2
"CPU while minimised (scene paused): {0:0.00}% of machine" -f (CpuOver 10)
$p.Refresh(); "Working set: $([int]($p.WorkingSet64 / 1MB)) MB"
$p.CloseMainWindow() | Out-Null; if (-not $p.WaitForExit(5000)) { $p.Kill() }
