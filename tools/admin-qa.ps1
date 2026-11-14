# Administrator QA for VYTRIX. Must run elevated (relaunches itself through UAC if not).
# 1) Runs the full xUnit suite elevated, including the Admin category, which changes
#    real settings and verifies rollback restores them exactly.
# 2) Elevated UI checks (read-only): admin badge, admin-only controls enabled.
# Results: <repo>\qa-results\admin-report.txt
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'qa-results'))
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Out', "`"$Out`""
    return
}
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Force $Out | Out-Null
$report = Join-Path $Out 'admin-report.txt'
Remove-Item (Join-Path $Out 'done.flag') -ErrorAction SilentlyContinue
"VYTRIX admin QA - $(Get-Date)  (elevated: $isAdmin)" | Set-Content $report

Get-Process VYTRIX -ErrorAction SilentlyContinue | Stop-Process -Force

# -- 1. Test suite (elevated)
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$trx = Join-Path $Out 'admin.trx'
Remove-Item $trx -ErrorAction SilentlyContinue
$testOut = & dotnet test "$root\tests\Vytrix.Tests\Vytrix.Tests.csproj" -nologo --logger "trx;LogFileName=$trx" 2>&1
"`n== Test suite ==" | Add-Content $report
($testOut | Select-String 'Passed!|Failed!|error') | ForEach-Object { $_.Line } | Add-Content $report
if (Test-Path $trx) {
    [xml]$x = Get-Content $trx
    $x.TestRun.Results.UnitTestResult | Where-Object { $_.testName -like '*AdminTests*' -or $_.outcome -ne 'Passed' } | ForEach-Object {
        $line = "{0,-8} {1}" -f $_.outcome, ($_.testName -replace '^Vytrix\.Tests\.', '')
        if ($_.outcome -ne 'Passed') { $line += "`n         " + ($_.Output.ErrorInfo.Message -replace "`r?`n", ' ') }
        $line
    } | Add-Content $report
}

# -- 2. Elevated UI checks (read-only)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
function Win { [Windows.Automation.AutomationElement]::RootElement.FindFirst('Children', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'VYTRIX'))) }
function ByName($n) { (Win).FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $n))) }
function ButtonWith($text) {
    $all = (Win).FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)))
    foreach ($b in $all) { $t = $b.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $text))); if ($b.Current.Name -eq $text -or $t) { return $b } }
}
function Go($n) { (ByName $n).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 1500 }
$ui = @()
function Check($name, $ok) { $script:ui += ("{0,-8} {1}" -f $(if ($ok) { 'Passed' } else { 'FAILED' }), $name) }

$exe = Get-ChildItem "$root\src\Vytrix\bin" -Recurse -Filter VYTRIX.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
. (Join-Path $PSScriptRoot 'qa-common.ps1')
New-QaDataDir -TermsAccepted | Out-Null
Start-Process $exe.FullName
for ($i = 0; $i -lt 40 -and -not (Win); $i++) { Start-Sleep -Milliseconds 250 }
Start-Sleep -Seconds 2
Check 'Sidebar shows "Administrator mode"' ($null -ne (ByName 'Administrator mode'))
Check 'Relaunch button hidden when elevated' ($null -eq (ButtonWith 'Relaunch as administrator'))
Go 'Restore Center'
$rp = ButtonWith 'Create restore point'
Check 'Create restore point enabled' ($rp -and $rp.Current.IsEnabled)
Check 'Restore points listed (no admin notice)' ($null -eq (ByName 'Listing and creating restore points requires administrator mode.'))
Go 'Services'
$combos = (Win).FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ComboBox)))
Check "Services: startup-type selectors available ($($combos.Count))" ($combos.Count -gt 0)
Go 'Network'
$use = ButtonWith 'Use'
Check 'Network: DNS preset buttons enabled' ($use -and $use.Current.IsEnabled)
Go 'Benchmark'
$run = ButtonWith 'Run'
Check 'Benchmark: Run enabled' ($run -and $run.Current.IsEnabled)
& "$PSScriptRoot\capture-window.ps1" -Out (Join-Path $Out 'admin-benchmark.png') | Out-Null
Go 'Restore Center'
& "$PSScriptRoot\capture-window.ps1" -Out (Join-Path $Out 'admin-restore.png') | Out-Null
$p = Get-Process VYTRIX
$p.CloseMainWindow() | Out-Null
Check 'Closes cleanly' ($p.WaitForExit(5000))
"`n== Elevated UI ==" | Add-Content $report
$ui | Add-Content $report

# -- 3. Leftovers
"`n== Leftovers ==" | Add-Content $report
"HKLM scratch key present: $(Test-Path 'HKLM:\SOFTWARE\VYTRIX-Tests')" | Add-Content $report
"HKCU scratch key present: $(Test-Path 'HKCU:\Software\VYTRIX-Tests')" | Add-Content $report
'ok' | Set-Content (Join-Path $Out 'done.flag')
