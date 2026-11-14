# Visual QA: opens every page in turn and saves a screenshot of each.
param([string]$OutDir = "$env:TEMP\vytrix-qa", [string[]]$Pages = @('overview','optimization','gaming','network','memory','storage','tweaks','startup','restore','hardware','apps','services','benchmark','settings'), [int]$Wait = 6)
$root = Split-Path $PSScriptRoot -Parent
$exe = Get-ChildItem (Join-Path $root 'src\Vytrix\bin') -Recurse -Filter VYTRIX.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
New-Item -ItemType Directory -Force $OutDir | Out-Null
. (Join-Path $PSScriptRoot 'qa-common.ps1')
New-QaDataDir -TermsAccepted | Out-Null   # isolated data; terms pre-accepted so pages are visible
foreach ($p in $Pages) {
    Get-Process VYTRIX -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 600
    Start-Process $exe.FullName -ArgumentList '--page', $p
    Start-Sleep -Seconds $Wait
    & (Join-Path $PSScriptRoot 'capture-window.ps1') -Out (Join-Path $OutDir "$p.png") | Out-Null
    $proc = Get-Process VYTRIX -ErrorAction SilentlyContinue
    "{0,-14} ws={1}MB cpu={2:0.0}s" -f $p, [int]($proc.WorkingSet64 / 1MB), $proc.CPU
}
Get-Process VYTRIX -ErrorAction SilentlyContinue | Stop-Process -Force
