# UI smoke test through Windows UI Automation. Only performs read-only actions
# (scan, open dialogs, cancel) so it never changes the machine.
param([string]$Shots = "$env:TEMP\vytrix-qa")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = Split-Path $PSScriptRoot -Parent
$exe = Get-ChildItem (Join-Path $root 'src\Vytrix\bin') -Recurse -Filter VYTRIX.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
New-Item -ItemType Directory -Force $Shots | Out-Null
$results = @()
function Check($name, [bool]$ok) { $script:results += [pscustomobject]@{ Test = $name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }) } }

# Only ever looks at (and closes) the copy this script started, so a VYTRIX the user has open is left alone.
$script:qaPid = 0
function Win {
    if (-not $script:qaPid) { return $null }
    $c = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'VYTRIX')),
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$script:qaPid)))
    [Windows.Automation.AutomationElement]::RootElement.FindFirst('Children', $c)
}
function Launch([string[]]$a) {
    $pp = if ($a) { Start-Process $exe.FullName -ArgumentList $a -PassThru } else { Start-Process $exe.FullName -PassThru }
    $script:qaPid = $pp.Id
    $pp
}
function FindByName($el, $name) { $el.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name))) }
function FindButtonContaining($el, $text) {
    $all = $el.FindAll('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)))
    foreach ($b in $all) { $t = $b.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text))); if ($b.Current.Name -like "*$text*" -or ($t -and $t.Current.Name -like "*$text*")) { return $b } }
    $null
}
function Invoke($el) { $el.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Shot($n) { & (Join-Path $PSScriptRoot 'capture-window.ps1') -ProcessId $script:qaPid -Out (Join-Path $Shots "$n.png") | Out-Null }

. (Join-Path $PSScriptRoot 'qa-common.ps1')
Get-Process VYTRIX -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe.FullName } | Stop-Process -Force
Start-Sleep -Milliseconds 500

# 0. Terms of Agreement on first run (isolated, fresh data folder)
function WaitWin { $w = $null; for ($i = 0; $i -lt 40 -and -not $w; $i++) { Start-Sleep -Milliseconds 250; $w = Win }; Start-Sleep -Seconds 2; $w }
$fresh = New-QaDataDir
$p = Launch
$w = WaitWin
Check 'First run shows Terms of Agreement' ($null -ne (FindByName $w 'VYTRIX Terms of Agreement (v1.6)'))
$accept = FindButtonContaining $w 'Accept'
Check 'Accept disabled until box is ticked' ($accept -and -not $accept.Current.IsEnabled)
Shot 'terms-first-run'
Invoke (FindButtonContaining $w 'Decline')
Check 'Decline exits the app' ($p.WaitForExit(5000))
Check 'Declining does not record acceptance' (-not ((Get-Content (Join-Path $fresh 'settings.json') -ErrorAction SilentlyContinue) -match '"acceptedTermsVersion": "1.6"'))
$p = Launch
$w = WaitWin
$box = FindByName $w 'I have read and agree to the VYTRIX Terms of Agreement'
$box.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
Start-Sleep -Milliseconds 300
$accept = FindButtonContaining $w 'Accept'
Check 'Ticking the box enables Accept' ($accept.Current.IsEnabled)
Invoke $accept
Start-Sleep -Milliseconds 800
Check 'Accepting records version 1.6' ((Get-Content (Join-Path $fresh 'settings.json') -Raw) -match '"acceptedTermsVersion": "1.6"')
Check 'App stays open after accepting' (-not $p.HasExited)
$p.CloseMainWindow() | Out-Null; $p.WaitForExit(5000) | Out-Null
$p = Launch
$w = WaitWin
Check 'Terms not shown again once accepted' ($null -eq (FindByName $w 'VYTRIX Terms of Agreement (v1.6)'))
$p.CloseMainWindow() | Out-Null; $p.WaitForExit(5000) | Out-Null

# 0b. Sign-in screen: with no saved session VYTRIX opens its separate sign-in window, not the main one.
function SignInWin {
    $c = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Sign in - VYTRIX')),
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$script:qaPid)))
    [Windows.Automation.AutomationElement]::RootElement.FindFirst('Children', $c)
}
New-QaDataDir -TermsAccepted -SignedOut | Out-Null
$env:VYTRIX_ACCOUNTS_URL = 'http://127.0.0.1:9'   # unreachable: the screen must say so rather than hang
$p = Launch
$s = $null; for ($i = 0; $i -lt 40 -and -not $s; $i++) { Start-Sleep -Milliseconds 250; $s = SignInWin }
Start-Sleep -Seconds 3
Check 'Without a sign-in VYTRIX opens its sign-in window' ($null -ne $s)
Check 'Main window stays closed until signed in' ($null -eq (Win))
Check 'Unreachable account server offers Try again' ($s -and $null -ne (FindButtonContaining $s 'Try again'))
Shot 'sign-in-offline'
$p.CloseMainWindow() | Out-Null
Check 'Closing the sign-in window exits VYTRIX' ($p.WaitForExit(5000))

# Remaining checks run against an isolated folder with the terms pre-accepted.
New-QaDataDir -TermsAccepted | Out-Null
$start = Get-Date
$p = Launch @('--page','storage')
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
foreach ($n in 'Overview','Assistant','Optimization','Gaming','Network','Memory','Storage','Tweaks','Startup','Restore Center','Hardware','Installed Apps','Services','Benchmark','Settings','Account','Help') {
    $item = FindByName (Win) $n
    if ($item) { $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 900 }
    $title = FindByName (Win) $(if ($n -eq 'Tweaks') { 'Tweak Library' } else { $n })
    Check "Navigate: $n" ($null -ne $title)
}

# 3a. Assistant without a key shows setup (no network, nothing is sent)
(FindByName (Win) 'Assistant').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Seconds 1
# It checks whether the local AI is set up, and may need to start Ollama first (up to ~15 s).
$setup = $false; $chat = $false
for ($i = 0; $i -lt 40 -and -not ($setup -or $chat); $i++) {
    Start-Sleep -Milliseconds 500
    $setup = $null -ne (FindByName (Win) 'Free AI on your PC') -and $null -ne (FindByName (Win) 'Optional: Claude')
    $chat = $null -ne (FindByName (Win) 'How can I help your PC today?')
}
Check ("Assistant: " + $(if ($chat) { 'free local chat ready (Ollama + model present)' } else { 'free setup shown first' })) ($setup -or $chat)
Shot 'assistant-setup'

# 3b. Help page: quick start, topics, and help topics in global search
(FindByName (Win) 'Help').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Seconds 1
Check 'Help: quick start and topics shown' ($null -ne (FindByName (Win) 'Pick a profile') -and $null -ne (FindByName (Win) 'Common questions'))
Shot 'help'
$gs = (Win).FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, 'GlobalSearch')))
if ($gs) { $gs.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('uninstall'); Start-Sleep -Milliseconds 800 }
# The results list is a popup (its own top-level window), so look in every window of this process.
$pidCond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$script:qaPid)
$hit = $null
foreach ($top in [Windows.Automation.AutomationElement]::RootElement.FindAll('Children', $pidCond)) { if (-not $hit) { $hit = FindByName $top 'Help topic' } }
Check 'Global search lists help topics' ($null -ne $hit)
if ($gs) { $gs.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('') }

# 4. Review a profile (dialog only, then cancel)
(FindByName (Win) 'Optimization').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Seconds 1
$review = FindButtonContaining (Win) 'Review & apply'
if ($review) { Invoke $review; Start-Sleep -Seconds 2; Shot 'profile-review'; $c = FindByName (Win) 'Cancel'; Check 'Profile review dialog lists changes' ($null -ne (FindButtonContaining (Win) 'Apply profile')); if ($c) { Invoke $c } }

# 5. Idle CPU usage over 10 s on the Overview page
(FindByName (Win) 'Overview').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
$p = Get-Process -Id $script:qaPid; $c0 = $p.TotalProcessorTime; $m0 = $p.WorkingSet64
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

# Leave nothing behind in %TEMP%.
Start-Sleep -Milliseconds 500
Remove-QaDataDirs
