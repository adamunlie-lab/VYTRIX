# Captures the Tweaks page filtered to each component chip (read-only).
param([string]$OutDir = "$env:TEMP\vytrix-qa", [string[]]$Chips = @('Memory (0)', 'GPU (4)', 'Storage (2)'))
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
. (Join-Path $PSScriptRoot 'qa-common.ps1')
New-QaDataDir -TermsAccepted | Out-Null
$root = Split-Path $PSScriptRoot -Parent
$exe = Get-ChildItem (Join-Path $root 'src\Vytrix\bin') -Recurse -Filter VYTRIX.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Process VYTRIX -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$p = Start-Process $exe.FullName -ArgumentList '--page', 'tweaks' -PassThru
function Win { [Windows.Automation.AutomationElement]::RootElement.FindFirst('Children', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'VYTRIX'))) }
for ($i = 0; $i -lt 40 -and -not (Win); $i++) { Start-Sleep -Milliseconds 250 }
Start-Sleep -Seconds 3
New-Item -ItemType Directory -Force $OutDir | Out-Null
foreach ($c in $Chips) {
    $t = (Win).FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $c)))
    $rb = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($t)
    while ($rb -and $rb.Current.ControlType -ne [Windows.Automation.ControlType]::RadioButton) { $rb = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($rb) }
    if ($rb) { $rb.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Seconds 1 }
    $name = ($c -replace '[^A-Za-z]', '').ToLower()
    & (Join-Path $PSScriptRoot 'capture-window.ps1') -Out (Join-Path $OutDir "tweaks-$name.png") | Out-Null
    "$c -> $(if ($rb) { 'selected' } else { 'chip not found' })"
}
$p.CloseMainWindow() | Out-Null
