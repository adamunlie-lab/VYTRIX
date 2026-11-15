# Visual QA: opens every page in turn and saves a screenshot of each.
param([string]$OutDir = "$env:TEMP\vytrix-qa", [string[]]$Pages = @('overview','assistant','optimization','gaming','network','memory','storage','tweaks','startup','restore','hardware','apps','services','benchmark','settings','help'), [int]$Wait = 6)
$root = Split-Path $PSScriptRoot -Parent
$exe = Get-ChildItem (Join-Path $root 'src\Vytrix\bin') -Recurse -Filter VYTRIX.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
New-Item -ItemType Directory -Force $OutDir | Out-Null
. (Join-Path $PSScriptRoot 'qa-common.ps1')
New-QaDataDir -TermsAccepted | Out-Null   # isolated data; terms pre-accepted so pages are visible
# Only the copy started here is captured and closed; a VYTRIX the user has open is left alone.
foreach ($p in $Pages) {
    $proc = Start-Process $exe.FullName -ArgumentList '--page', $p -PassThru
    Start-Sleep -Seconds $Wait
    & (Join-Path $PSScriptRoot 'capture-window.ps1') -ProcessId $proc.Id -Out (Join-Path $OutDir "$p.png") | Out-Null
    $proc.Refresh()
    "{0,-14} ws={1}MB cpu={2:0.0}s" -f $p, [int]($proc.WorkingSet64 / 1MB), $proc.CPU
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    $proc.WaitForExit(5000) | Out-Null
}
