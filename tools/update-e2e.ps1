# End-to-end auto-update test against the live GitHub releases.
# Precondition: an older VYTRIX is installed in Program Files and a newer release is published.
# Launches the installed app (isolated data, update checks ON), waits for its update offer,
# clicks Install now, and verifies the new version is installed and running.
param([string]$OutDir = "$env:TEMP\vytrix-qa", [int]$TimeoutSec = 240)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
. (Join-Path $PSScriptRoot 'qa-common.ps1')
$data = New-QaDataDir -TermsAccepted
'{ "acceptedTermsVersion": "1.1", "acceptedTermsAt": "2026-01-01T00:00:00", "checkUpdatesOnStart": true, "autoDownloadUpdates": true, "gitHubFeedIntroduced": true }' | Set-Content (Join-Path $data 'settings.json') -Encoding ascii
New-Item -ItemType Directory -Force $OutDir | Out-Null
$exe = Join-Path $env:ProgramFiles 'VYTRIX\VYTRIX.exe'
$key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6E2B7C1A-5D4F-4C2B-9A7E-3F1D5B8C0A21}_is1'
$before = (Get-ItemProperty $key).DisplayVersion
"Installed before: $before"
function Win { [Windows.Automation.AutomationElement]::RootElement.FindFirst('Children', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'VYTRIX'))) }
function Find($n) { $w = Win; if ($w) { $w.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $n))) } }

Get-Process VYTRIX -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$t0 = Get-Date
$p = Start-Process $exe -PassThru
# Wait for the update offer (check + background download + verify).
$offer = $null
while (-not $offer -and ((Get-Date) - $t0).TotalSeconds -lt $TimeoutSec) { Start-Sleep -Seconds 2; $offer = Get-ChildItem function: | Out-Null; $offer = (Win) | ForEach-Object { $_.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Install now'))) } }
if (-not $offer) { "FAIL: no update offer within $TimeoutSec s"; Get-Content (Join-Path $data 'logs\*.log') | Select-String update; return }
"Update offered after $([int]((Get-Date) - $t0).TotalSeconds) s"
& (Join-Path $PSScriptRoot 'capture-window.ps1') -Out (Join-Path $OutDir 'update-offer.png') | Out-Null
Get-Content (Join-Path $data 'logs\*.log') | Select-String '"update"' | ForEach-Object { ($_ | ConvertFrom-Json).msg }
$b = $offer; while ($b -and $b.Current.ControlType -ne [Windows.Automation.ControlType]::Button) { $b = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($b) }
$b.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
"Clicked Install now; waiting for the administrator prompt and the install..."
# Wait for the installed version to change.
$t1 = Get-Date
while (((Get-ItemProperty $key).DisplayVersion -eq $before) -and ((Get-Date) - $t1).TotalSeconds -lt 180) { Start-Sleep -Seconds 3 }
$after = (Get-ItemProperty $key).DisplayVersion
"Installed after: $after"
Start-Sleep -Seconds 8
$running = Get-Process VYTRIX -ErrorAction SilentlyContinue | Select-Object -First 1
"Relaunched: " + ($null -ne $running) + $(if ($running) { "  (" + $running.MainModule.FileVersionInfo.FileVersion + ")" })
if ($running) { & (Join-Path $PSScriptRoot 'capture-window.ps1') -Out (Join-Path $OutDir 'update-after.png') | Out-Null }
if ($after -ne $before) { "PASS: updated $before -> $after" } else { "FAIL: version unchanged" }
