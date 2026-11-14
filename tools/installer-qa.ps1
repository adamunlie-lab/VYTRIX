# Installer QA: silent install -> verify -> launch -> silent uninstall -> verify removal.
# Runs elevated (relaunches itself via UAC). Report: <repo>\qa-results\installer-report.txt
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'qa-results'))
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Out', "`"$Out`""
    return
}
$root = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Force $Out | Out-Null
$report = Join-Path $Out 'installer-report.txt'
Remove-Item (Join-Path $Out 'installer-done.flag') -ErrorAction SilentlyContinue
$lines = @("VYTRIX installer QA - $(Get-Date)")
function Check($name, $ok) { $script:lines += ("{0,-7} {1}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name) }

$setup = Join-Path $root 'dist\VYTRIX-Setup-1.0.0.exe'
$dir = Join-Path $env:ProgramFiles 'VYTRIX'
$uninstKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6E2B7C1A-5D4F-4C2B-9A7E-3F1D5B8C0A21}_is1'
$startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\VYTRIX.lnk'
Get-Process VYTRIX -ErrorAction SilentlyContinue | Stop-Process -Force

# Install (silent, no desktop shortcut)
$p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/MERGETASKS="!desktopicon"' -Wait -PassThru
Check "Installer exit code 0 (got $($p.ExitCode))" ($p.ExitCode -eq 0)
Check 'VYTRIX.exe installed' (Test-Path "$dir\VYTRIX.exe")
Check 'VytrixCore.dll installed' (Test-Path "$dir\VytrixCore.dll")
Check 'Registered in Apps & features' (Test-Path $uninstKey)
if (Test-Path $uninstKey) { $u = Get-ItemProperty $uninstKey; Check "Display name/version: $($u.DisplayName) / $($u.DisplayVersion)" ($u.DisplayVersion -eq '1.0.0') }
Check 'Start menu shortcut' (Test-Path $startMenu)
Check 'No desktop shortcut (task unchecked)' (-not (Test-Path (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'VYTRIX.lnk')))

# Launch installed build
. (Join-Path $PSScriptRoot 'qa-common.ps1')
New-QaDataDir -TermsAccepted | Out-Null
Check 'Terms of Agreement installed next to the app' (Test-Path "$dir\TERMS.txt")
$app = Start-Process "$dir\VYTRIX.exe" -PassThru
Start-Sleep -Seconds 6
$app.Refresh()
Check "Installed app runs (window '$($app.MainWindowTitle)', $([int]($app.WorkingSet64/1MB)) MB)" (-not $app.HasExited -and $app.MainWindowTitle -eq 'VYTRIX')
& (Join-Path $PSScriptRoot 'capture-window.ps1') -Out (Join-Path $Out 'installed-app.png') | Out-Null
$app.CloseMainWindow() | Out-Null
Check 'Installed app closes cleanly' ($app.WaitForExit(5000))

# Uninstall (silent)
$un = Join-Path $dir 'unins000.exe'
$p = Start-Process $un -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
Start-Sleep -Seconds 3   # uninstaller finishes removing itself from a temp copy
Check "Uninstaller exit code 0 (got $($p.ExitCode))" ($p.ExitCode -eq 0)
Check 'Program folder removed' (-not (Test-Path "$dir\VYTRIX.exe"))
Check 'Apps & features entry removed' (-not (Test-Path $uninstKey))
Check 'Start menu shortcut removed' (-not (Test-Path $startMenu))
Check 'User data kept (journal/backups/logs)' (Test-Path (Join-Path $env:LOCALAPPDATA 'VYTRIX'))

$lines | Set-Content $report
'ok' | Set-Content (Join-Path $Out 'installer-done.flag')
