# Builds artifacts\installer\LiveSlides-Setup-<version>.exe
# Requires: .NET 9 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup).
# Usage: powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Version 0.2.2]
param(
    [string]$Version = "0.2.1"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
# Published to a short temp path, as for LiveCue: keeps every file well under the 260-character
# limit Inno Setup can read, whatever the checkout path.
$publishDir = Join-Path ([IO.Path]::GetTempPath()) "LiveSlides-publish"

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 non trovato. Installalo con: winget install JRSoftware.InnoSetup" }

Write-Host "Pubblicazione LiveSlides $Version (self-contained, win-x64)..."
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $root "src\LiveSlides.App") -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:Version=$Version -o $publishDir -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish non riuscito" }

Write-Host "Creazione installer..."
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" (Join-Path $PSScriptRoot "LiveSlides.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup non riuscito" }

Get-Item (Join-Path $root "artifacts\installer\LiveSlides-Setup-$Version.exe")
