<#
  Publishes a VYTRIX release to GitHub so installed copies auto-update.
    .\tools\publish-release.ps1 -Version 1.0.1 -Notes "What changed"
  Steps: build + test + installer at that version -> SHA-256 -> latest.json ->
  GitHub release v<Version> with the installer, portable zip and latest.json.
  Installed VYTRIX reads releases/latest/download/latest.json, so the newest
  release is what everyone is offered.
#>
param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$Notes, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$gh = @("$env:ProgramFiles\GitHub CLI\gh.exe", (Get-Command gh -ErrorAction SilentlyContinue).Source) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $gh) { throw "GitHub CLI (gh) not found." }

# Repository the app was built for (must match Updater.Repository in the code).
$repo = (Select-String -Path "$root\src\Vytrix\Services\ExtraServices.cs" -Pattern 'Repository = "([^"]+)"').Matches[0].Groups[1].Value
if ($repo -like 'OWNER/*') { throw "Set Updater.Repository in ExtraServices.cs to your GitHub owner/repo first." }

if (-not $SkipBuild) {
    & powershell -ExecutionPolicy Bypass -File "$root\build.ps1" -Installer -Version $Version
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}
$setup = Join-Path $root "dist\VYTRIX-Setup-$Version.exe"
$zip = Join-Path $root "dist\VYTRIX-$Version-win-x64.zip"
if (-not (Test-Path $setup)) { throw "Missing $setup" }

$sha = (Get-FileHash $setup -Algorithm SHA256).Hash
$manifest = [ordered]@{
    version = $Version
    url     = "https://github.com/$repo/releases/download/v$Version/VYTRIX-Setup-$Version.exe"
    sha256  = $sha
    notes   = $Notes
}
$latest = Join-Path $root 'dist\latest.json'
($manifest | ConvertTo-Json) | Set-Content $latest -Encoding ascii
Write-Host "latest.json: version $Version, sha256 $sha"

$notesFile = Join-Path $root 'dist\release-notes.md'
@"
$Notes

**Install:** download ``VYTRIX-Setup-$Version.exe`` below. Existing installs update themselves from inside the app.

SHA-256 of the installer: ``$sha``
"@ | Set-Content $notesFile -Encoding utf8

& $gh release create "v$Version" $setup $zip $latest --repo $repo --title "VYTRIX $Version" --notes-file $notesFile
if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
Write-Host "Published https://github.com/$repo/releases/tag/v$Version" -ForegroundColor Green
