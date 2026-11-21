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
    @{ Name = "RemoteDesk.Host"; Path = "src/RemoteDesk.Host/RemoteDesk.Host.csproj"; Runtime = "win-x64" },
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

# Copy FFmpeg shared libraries to required binaries
$ffmpegSource = "C:\Users\mis\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg.Shared_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0.2-full_build-shared\bin"
if (Test-Path $ffmpegSource) {
    Write-Host "Copying native FFmpeg shared libraries from $ffmpegSource..." -ForegroundColor Yellow
    $ffmpegTargets = @("RemoteDesk.Viewer", "RemoteDesk.Host", "RemoteDesk.Host.Agent", "RemoteDesk.Host.Service")
    foreach ($target in $ffmpegTargets) {
        $targetFfmpegDir = Join-Path (Join-Path $publishRoot $target) "ffmpeg"
        New-Item -ItemType Directory -Path $targetFfmpegDir -Force | Out-Null
        Copy-Item (Join-Path $ffmpegSource "*.dll") -Destination $targetFfmpegDir -Force
    }
}

# Generate Root Launcher Batch Scripts
$startRemoteDeskBat = @"
@echo off
title RemoteDesk
cd /d "%~dp0\RemoteDesk.Viewer"
start "" "RemoteDesk.Viewer.exe"
"@
[System.IO.File]::WriteAllText((Join-Path $publishRoot "START-REMOTEDESK.bat"), $startRemoteDeskBat)

$startViewerBat = @"
@echo off
title RemoteDesk - Viewer
cd /d "%~dp0\RemoteDesk.Viewer"
start "" "RemoteDesk.Viewer.exe"
"@
[System.IO.File]::WriteAllText((Join-Path $publishRoot "START-VIEWER.bat"), $startViewerBat)

$startHostBat = @"
@echo off
title RemoteDesk - Standalone Host Agent
cd /d "%~dp0\RemoteDesk.Host"

echo =======================================================
echo          RemoteDesk - Host Agent Launcher
echo =======================================================
echo.
set /p SERVER_IP="Enter Controller/Viewer IP address [Press Enter for localhost]: "
if "%SERVER_IP%"=="" set SERVER_IP=localhost

echo.
echo Connecting to Signaling Server at: http://%SERVER_IP%:5000/signaling
echo.
"RemoteDesk.Host.exe" --server "http://%SERVER_IP%:5000/signaling" --auto-accept
pause
"@
[System.IO.File]::WriteAllText((Join-Path $publishRoot "START-HOST.bat"), $startHostBat)

$installServiceBat = @"
@echo off
title RemoteDesk - Install Windows Service
cd /d "%~dp0"

echo =======================================================
echo     RemoteDesk - Windows Host Service Installer
echo =======================================================
echo.
echo NOTE: Must be run with Administrator privileges.
echo.
set /p SERVER_IP="Enter Controller/Viewer IP address [Press Enter for localhost]: "
if "%SERVER_IP%"=="" set SERVER_IP=localhost

echo.
echo Installing background service pointing to http://%SERVER_IP%:5000/signaling ...
powershell -ExecutionPolicy Bypass -File "deploy\install-service.ps1" -ServerUrl "http://%SERVER_IP%:5000/signaling" -FleetUrl "http://%SERVER_IP%:5001"
echo.
pause
"@
[System.IO.File]::WriteAllText((Join-Path $publishRoot "INSTALL-SERVICE.bat"), $installServiceBat)

# Generate SHA-256 Checksums for all published binaries
$checksumFile = Join-Path $publishRoot "SHA256SUMS.txt"
$files = Get-ChildItem -Path $publishRoot -Recurse -File | Where-Object { $_.Extension -in @(".exe", ".dll", ".ps1", ".bat") }
$sb = [System.Text.StringBuilder]::new()

foreach ($f in $files) {
    $hash = (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $rel = $f.FullName.Substring($publishRoot.Length).TrimStart("\", "/")
    $sb.AppendLine("$hash  $rel") | Out-Null
}

[System.IO.File]::WriteAllText($checksumFile, $sb.ToString())
Write-Host "Generated SHA-256 checksums at: $checksumFile" -ForegroundColor Green

# Create Distribution Zip
$zipPath = Join-Path (Join-Path $rootDir "artifacts") "RemoteDesk-v1.0-Windows.zip"
if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}
Write-Host "Creating distribution zip at: $zipPath..." -ForegroundColor Yellow
Compress-Archive -Path "$publishRoot\*" -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Distribution zip created successfully!" -ForegroundColor Green

Write-Host "==================================================" -ForegroundColor Green
Write-Host " Packaging completed: $publishRoot" -ForegroundColor Green
Write-Host " Output Zip: $zipPath" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
