# RemoteDesk Production Packaging & Installer Build Script
param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [string]$OutputDir = "artifacts/dist"
)

$ErrorActionPreference = "Stop"

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "   RemoteDesk - Packaging & Distribution Build" -ForegroundColor Cyan
Write-Host "   Version: $Version  |  Config: $Configuration" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

$rootDir = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $rootDir $OutputDir

if (Test-Path $publishRoot) {
    Remove-Item -Recurse -Force $publishRoot
}
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

$projects = @(
    @{ Name = "RemoteDesk.Viewer"; Path = "src/RemoteDesk.Viewer/RemoteDesk.Viewer.csproj"; Runtime = "win-x64" },
    @{ Name = "RemoteDesk.Host.Service"; Path = "src/RemoteDesk.Host.Service/RemoteDesk.Host.Service.csproj"; Runtime = "win-x64" },
    @{ Name = "RemoteDesk.Host.Agent"; Path = "src/RemoteDesk.Host.Agent/RemoteDesk.Host.Agent.csproj"; Runtime = "win-x64" },
    @{ Name = "RemoteDesk.Updater"; Path = "src/RemoteDesk.Updater/RemoteDesk.Updater.csproj"; Runtime = "win-x64" },
    @{ Name = "RemoteDesk.Server.Api"; Path = "src/RemoteDesk.Server.Api/RemoteDesk.Server.Api.csproj"; Runtime = "" },
    @{ Name = "RemoteDesk.Server.Signaling"; Path = "src/RemoteDesk.Server.Signaling/RemoteDesk.Server.Signaling.csproj"; Runtime = "" }
)

foreach ($proj in $projects) {
    $out = Join-Path $publishRoot $proj.Name
    Write-Host "Publishing $($proj.Name)..." -ForegroundColor Yellow

    $projPath = Join-Path $rootDir $proj.Path
    if ($proj.Runtime -ne "") {
        dotnet publish $projPath -c $Configuration -r $proj.Runtime --self-contained false -p:Version=$Version -o $out
    } else {
        dotnet publish $projPath -c $Configuration -p:Version=$Version -o $out
    }
}

# Copy installation scripts
$deployDir = Join-Path $publishRoot "deploy"
New-Item -ItemType Directory -Path $deployDir -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot "install-service.ps1") $deployDir
Copy-Item (Join-Path $PSScriptRoot "uninstall-service.ps1") $deployDir

# Generate SHA-256 Checksums for all published binaries
$checksumFile = Join-Path $publishRoot "SHA256SUMS.txt"
$files = Get-ChildItem -Path $publishRoot -Recurse -File | Where-Object { $_.Extension -in @(".exe", ".dll", ".ps1") }
$sb = [System.Text.StringBuilder]::new()

foreach ($f in $files) {
    $hash = (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $rel = $f.FullName.Substring($publishRoot.Length).TrimStart("\", "/")
    $sb.AppendLine("$hash  $rel") | Out-Null
}

[System.IO.File]::WriteAllText($checksumFile, $sb.ToString())
Write-Host "Generated SHA-256 checksums at: $checksumFile" -ForegroundColor Green

Write-Host "==================================================" -ForegroundColor Green
Write-Host " Packaging completed: $publishRoot" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
