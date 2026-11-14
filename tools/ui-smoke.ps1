# UI smoke test through Windows UI Automation. Only performs read-only actions
# (scan, open dialogs, cancel) so it never changes the machine.
param([string]$Shots = "$env:TEMP\vytrix-qa")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = Split-Path $PSScriptRoot -Parent
$exe = Get-ChildItem (Join-Path $root 'src\Vytrix\bin') -Recurse -Filter VYTRIX.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
New-Item -ItemType Directory -Force $Shots | Out-Null
$results = @()
function Check($name, [bool]$ok) { $script:results += [pscustomobject]@{ Test = $name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }) } }

function Win { [Windows.Automation.AutomationElement]::RootElement.FindFirst('Children', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'VYTRIX'))) }
function FindByName($el, $name) { $el.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name))) }
function FindButtonContaining($el, $text) {
    $all = $el.FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)))
    foreach ($b in $all) { $t = $b.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text))); if ($b.Current.Name -like "*$text*" -or ($t -and $t.Current.Name -like "*$text*")) { return $b } }
    $null
}
function Invoke($el) { $el.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Shot($n) { & (Join-Path $PSScriptRoot 'capture-window.ps1') -Out (Join-Path $Shots "$n.png") | Out-Null }

. (Join-Path $PSScriptRoot 'qa-common.ps1')
Get-Process VYTRIX -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# 0. Terms of Agreement on first run (isolated, fresh data folder)
function WaitWin { $w = $null; for ($i = 0; $i -lt 40 -and -not $w; $i++) { Start-Sleep -Milliseconds 250; $w = Win }; Start-Sleep -Seconds 2; $w }
$fresh = New-QaDataDir
$p = Start-Process $exe.FullName -PassThru
$w = WaitWin
Check 'First run shows Terms of Agreement' ($null -ne (FindByName $w 'VYTRIX Terms of Agreement (v1.1)'))
$accept = FindButtonContaining $w 'Accept'
Check 'Accept disabled until box is ticked' ($accept -and -not $accept.Current.IsEnabled)
Shot 'terms-first-run'
Invoke (FindButtonContaining $w 'Decline')
Check 'Decline exits the app' ($p.WaitForExit(5000))
Check 'Declining does not record acceptance' (-not ((Get-Content (Join-Path $fresh 'settings.json') -ErrorAction SilentlyContinue) -match '"acceptedTermsVersion": "1.1"'))
$p = Start-Process $exe.FullName -PassThru
$w = WaitWin
$box = FindByName $w 'I have read and agree to the VYTRIX Terms of Agreement'
$box.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
Start-Sleep -Milliseconds 300
$accept = FindButtonContaining $w 'Accept'
Check 'Ticking the box enables Accept' ($accept.Current.IsEnabled)
Invoke $accept
Start-Sleep -Milliseconds 800
Check 'Accepting records version 1.1' ((Get-Content (Join-Path $fresh 'settings.json') -Raw) -match '"acceptedTermsVersion": "1.1"')
Check 'App stays open after accepting' (-not $p.HasExited)
$p.CloseMainWindow() | Out-Null; $p.WaitForExit(5000) | Out-Null
$p = Start-Process $exe.FullName -PassThru
$w = WaitWin
Check 'Terms not shown again once accepted' ($null -eq (FindByName $w 'VYTRIX Terms of Agreement (v1.1)'))
$p.CloseMainWindow() | Out-Null; $p.WaitForExit(5000) | Out-Null

# Remaining checks run against an isolated folder with the terms pre-accepted.
New-QaDataDir -TermsAccepted | Out-Null
$start = Get-Date
Start-Process $exe.FullName -ArgumentList '--page','storage'
$w = $null; for ($i = 0; $i -lt 40 -and -not $w; $i++) { Start-Sleep -Milliseconds 250; $w = Win }
Check "App window appears (took $([int]((Get-Date) - $start).TotalMilliseconds) ms)" ($null -ne $w)
Start-Sleep -Seconds 2

# 1. Storage scan (read-only)
$scan = FindButtonContaining $w 'Scan'
Check 'Scan button present' ($null -ne $scan)
if ($scan) { Invoke $scan; Start-Sleep -Seconds 8; Shot 'storage-scanned'; Check 'Scan produced results' ($null -ne (FindButtonContaining (Win) 'Clean selected')) }

# 2. Cleanup confirmation dialog opens and Cancel aborts
$clean = FindButtonContaining (Win) 'Clean selected'
if ($clean) {
    Invoke $clean; Start-Sleep -Milliseconds 800; Shot 'storage-confirm'
    $cancel = FindByName (Win) 'Cancel'
    $del = FindButtonContaining (Win) 'Delete permanently'
    Check 'Delete confirmation dialog shown with Cancel + Delete' ($null -ne $cancel -and $null -ne $del)
    if ($cancel) { Invoke $cancel; Start-Sleep -Milliseconds 500; Check 'Cancel closes dialog' ($null -eq (FindButtonContaining (Win) 'Delete permanently')) }
}

# 3. Navigation through every sidebar item
foreach ($n in 'Overview','Optimization','Gaming','Network','Memory','Storage','Tweaks','Startup','Restore Center','Hardware','Installed Apps','Services','Benchmark','Settings') {
    $item = FindByName (Win) $n
    if ($item) { $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 900 }
    $title = FindByName (Win) $(if ($n -eq 'Tweaks') { 'Tweak Library' } else { $n })
    Check "Navigate: $n" ($null -ne $title)
}

# 4. Review a profile (dialog only, then cancel)
(FindByName (Win) 'Optimization').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Seconds 1
$review = FindButtonContaining (Win) 'Review & apply'
if ($review) { Invoke $review; Start-Sleep -Seconds 2; Shot 'profile-review'; $c = FindByName (Win) 'Cancel'; Check 'Profile review dialog lists changes' ($null -ne (FindButtonContaining (Win) 'Apply profile')); if ($c) { Invoke $c } }

# 5. Idle CPU usage over 10 s on the Overview page
(FindByName (Win) 'Overview').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
$p = Get-Process VYTRIX; $c0 = $p.TotalProcessorTime; $m0 = $p.WorkingSet64
Start-Sleep -Seconds 10
$p.Refresh(); $cpu = ($p.TotalProcessorTime - $c0).TotalMilliseconds / 10000 / [Environment]::ProcessorCount * 100
Check ("Idle CPU {0:0.00}% of machine (live monitoring on)" -f $cpu) ($cpu -lt 2)
Check ("Working set {0} MB" -f [int]($p.WorkingSet64 / 1MB)) ($p.WorkingSet64 -lt 400MB)
Shot 'overview-live'

# 6. Clean shutdown
$p.CloseMainWindow() | Out-Null
$exited = $p.WaitForExit(5000)
Check 'Closes cleanly within 5 s' $exited
if (-not $exited) { $p.Kill() }
$results | Format-Table -AutoSize | Out-String -Width 200
