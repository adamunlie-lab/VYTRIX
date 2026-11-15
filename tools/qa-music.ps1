# Carol player QA: press "Play carols", confirm rendering + playback start, measure CPU, then pause.
# Plays briefly at 15% volume. Isolated data folder.
param([string]$OutDir = "$env:TEMP\vytrix-qa")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
. (Join-Path $PSScriptRoot 'qa-common.ps1')
$data = New-QaDataDir -TermsAccepted
'{ "acceptedTermsVersion": "1.6", "acceptedTermsAt": "2026-01-01T00:00:00", "checkUpdatesOnStart": false, "gitHubFeedIntroduced": true, "musicVolume": 0.15 }' | Set-Content (Join-Path $data 'settings.json') -Encoding ascii
$root = Split-Path $PSScriptRoot -Parent
$exe = Get-ChildItem (Join-Path $root 'src\Vytrix\bin') -Recurse -Filter VYTRIX.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Process VYTRIX -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$p = Start-Process $exe.FullName -ArgumentList '--page', 'overview' -PassThru
function Win { [Windows.Automation.AutomationElement]::RootElement.FindFirst('Children', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'VYTRIX'))) }
function Btn($text) { $all = (Win).FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button))); foreach ($b in $all) { if ($b.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $text)))) { return $b } } }
for ($i = 0; $i -lt 40 -and -not (Win); $i++) { Start-Sleep -Milliseconds 250 }
Start-Sleep -Seconds 4
$t0 = Get-Date
(Btn 'Play carols').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
$log = Join-Path $data "logs\vytrix-$(Get-Date -f yyyyMMdd).log"
while (-not (Select-String -Path $log -Pattern 'Playing' -Quiet -ErrorAction SilentlyContinue) -and ((Get-Date) - $t0).TotalSeconds -lt 60) { Start-Sleep -Milliseconds 500 }
"Playback started after {0:0.0} s (includes first-time rendering)" -f ((Get-Date) - $t0).TotalSeconds
Get-Content $log | Select-String 'music' | ForEach-Object { ($_ | ConvertFrom-Json).msg }
"Rendered files: " + ((Get-ChildItem (Join-Path $data 'music') -Filter *.wav | ForEach-Object { "{0} ({1:0.0} MB)" -f $_.Name, ($_.Length / 1MB) }) -join ', ')
Start-Sleep -Seconds 2
& (Join-Path $PSScriptRoot 'capture-window.ps1') -Out (Join-Path $OutDir 'music-playing.png') | Out-Null
$p.Refresh(); $c0 = $p.TotalProcessorTime; Start-Sleep -Seconds 10; $p.Refresh()
"CPU while playing + animating: {0:0.00}% of machine" -f (($p.TotalProcessorTime - $c0).TotalMilliseconds / 10000 / [Environment]::ProcessorCount * 100)
$pause = Btn 'Pause carols'
"Pause button present while playing: " + ($null -ne $pause)
if ($pause) { $pause.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
Start-Sleep -Seconds 1
"Play button back after pausing: " + ($null -ne (Btn 'Play carols'))
$p.CloseMainWindow() | Out-Null; "Closed cleanly: " + $p.WaitForExit(5000)
