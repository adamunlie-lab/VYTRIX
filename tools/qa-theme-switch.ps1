# Switches themes live through the Settings page and captures each (isolated data folder).
param([string]$OutDir = "$env:TEMP\vytrix-qa")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
. (Join-Path $PSScriptRoot 'qa-common.ps1')
New-QaDataDir -TermsAccepted | Out-Null
$root = Split-Path $PSScriptRoot -Parent
$exe = Get-ChildItem (Join-Path $root 'src\Vytrix\bin') -Recurse -Filter VYTRIX.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Process VYTRIX -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$p = Start-Process $exe.FullName -ArgumentList '--page', 'settings' -PassThru
function Win { [Windows.Automation.AutomationElement]::RootElement.FindFirst('Children', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'VYTRIX'))) }
for ($i = 0; $i -lt 40 -and -not (Win); $i++) { Start-Sleep -Milliseconds 250 }
Start-Sleep -Seconds 3
New-Item -ItemType Directory -Force $OutDir | Out-Null
function Pick($name) {
    $t = (Win).FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)))
    $rb = $t; while ($rb -and $rb.Current.ControlType -ne [Windows.Automation.ControlType]::RadioButton) { $rb = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($rb) }
    $rb.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Seconds 1.5
}
Pick 'Graphite'; & (Join-Path $PSScriptRoot 'capture-window.ps1') -Out (Join-Path $OutDir 'theme-graphite.png') | Out-Null
$s = Get-Content (Join-Path $env:VYTRIX_DATA_DIR 'settings.json') -Raw
"Saved after Graphite: " + ([regex]::Match($s, '"theme": "[^"]+"').Value) + " " + ([regex]::Match($s, '"accent": "[^"]+"').Value)
Pick 'Cherry Blossom'; & (Join-Path $PSScriptRoot 'capture-window.ps1') -Out (Join-Path $OutDir 'theme-cherry.png') | Out-Null
$s = Get-Content (Join-Path $env:VYTRIX_DATA_DIR 'settings.json') -Raw
"Saved after Cherry Blossom: " + ([regex]::Match($s, '"theme": "[^"]+"').Value) + " " + ([regex]::Match($s, '"accent": "[^"]+"').Value)
$p.CloseMainWindow() | Out-Null; "Closed cleanly: " + $p.WaitForExit(5000)
