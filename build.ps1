<#
  VYTRIX build script.
    .\build.ps1                 Build native core + app (Release), run tests, publish to dist\VYTRIX
    .\build.ps1 -SkipTests      Same without the test suite
    .\build.ps1 -Installer      Also compile installer\vytrix.iss with Inno Setup 6 (if installed)
    .\build.ps1 -Version 1.2.0  Stamp a version into the app, installer and zip (default: 1.0.0)
#>
param([switch]$SkipTests, [switch]$Installer, [string]$Version = '1.0.0')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3" }
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Write-Host "`n[1/4] Native core (C++)" -ForegroundColor Magenta
cmd /c "`"$root\native\build-native.cmd`""
if ($LASTEXITCODE -ne 0) { throw "Native build failed." }

if (-not $SkipTests) {
    Write-Host "`n[2/4] Tests" -ForegroundColor Magenta
    dotnet test "$root\tests\Vytrix.Tests\Vytrix.Tests.csproj" -c Release -nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
} else { Write-Host "`n[2/4] Tests skipped" -ForegroundColor DarkGray }

Write-Host "`n[3/4] Publish (self-contained, win-x64)" -ForegroundColor Magenta
$out = Join-Path $root 'dist\VYTRIX'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish "$root\src\Vytrix\Vytrix.csproj" -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:Version=$Version -o $out -nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
if (-not (Test-Path "$out\VytrixCore.dll")) { throw "VytrixCore.dll missing from publish output." }

Write-Host "`n[4/4] Package" -ForegroundColor Magenta
$zip = Join-Path $root "dist\VYTRIX-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Portable build: $zip"

if ($Installer) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($iscc) {
        & $iscc /Qp "/DAppVersion=$Version" "$root\installer\vytrix.iss"
        if ($LASTEXITCODE -ne 0) { throw "Installer compile failed." }
        Write-Host "Installer: $root\dist\VYTRIX-Setup-$Version.exe"
    }
    else { Write-Warning "Inno Setup 6 not found. Install it from jrsoftware.org (or: winget install JRSoftware.InnoSetup) and re-run with -Installer." }
}
Write-Host "`nDone." -ForegroundColor Green
