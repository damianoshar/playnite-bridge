#Requires -Version 5.1
<#
  Builds the reworked PlayniteBridge plugin, runs its tests, and packs a
  .pext ready to drag onto Playnite.
  Requires: .NET SDK (https://aka.ms/dotnet/download)
  Usage:  .\build-release.ps1
#>
param(
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host "==> Restoring + building plugin..." -ForegroundColor Cyan
dotnet build src/PlayniteBridge.csproj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed" }

Write-Host "==> Running tests..." -ForegroundColor Cyan
dotnet test tests/PlayniteBridge.Tests.csproj -c $Configuration --no-build
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

$version = ((Select-String -Path src/extension.yaml -Pattern '^Version:').Line -split ':', 2)[1].Trim()
$outDir = "src/bin/$Configuration/net462"
$pext = "PlayniteBridge_v$version.pext"
if (Test-Path $pext) { Remove-Item $pext }

Write-Host "==> Packing $pext ..." -ForegroundColor Cyan
$zip = "$pext.zip"
if (Test-Path $zip) { Remove-Item $zip }
# A .pext ships ONLY the manifest + the plugin dll (like upstream releases).
# Never bundle Playnite.SDK.dll: Playnite provides the SDK at runtime and a
# local copy breaks extension validation/loading.
$packFiles = @("$outDir/extension.yaml", "$outDir/PlayniteBridge.dll")
# Compress-Archive only supports .zip: pack, then rename (a .pext is a zip).
Compress-Archive -Path $packFiles -DestinationPath $zip
if (Test-Path $pext) { Remove-Item $pext }
Rename-Item $zip $pext
Write-Host "Done: $pext" -ForegroundColor Green
Write-Host "Install: close Playnite, double-click the .pext (or drag it onto Playnite), restart Playnite."
