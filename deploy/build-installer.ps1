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
    cmd /c "rmdir /s /q `"$publishRoot`""
    if (Test-Path $publishRoot) { Remove-Item -Recurse -Force $publishRoot }
}
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

$projects = @(
    @{ Name = "RemoteDesk.Viewer"; Path = "src/RemoteDesk.Viewer/RemoteDesk.Viewer.csproj"; Runtime = "win-x64" },
    @{ Name = "RemoteDesk.Host"; Path = "src/RemoteDesk.Host/RemoteDesk.Host.csproj"; Runtime = "win-x64" },
    @{ Name = "RemoteDesk.Host.Service"; Path = "src/RemoteDesk.Host.Service/RemoteDesk.Host.Service.csproj"; Runtime = "win-x64" },
    @{ Name = "RemoteDesk.Host.Agent"; Path = "src/RemoteDesk.Host.Agent/RemoteDesk.Host.Agent.csproj"; Runtime = "win-x64" },
    @{ Name = "RemoteDesk.Updater"; Path = "src/RemoteDesk.Updater/RemoteDesk.Updater.csproj"; Runtime = "win-x64" },
    @{ Name = "RemoteDesk.Server.Api"; Path = "src/RemoteDesk.Server.Api/RemoteDesk.Server.Api.csproj"; Runtime = "win-x64" },
    @{ Name = "RemoteDesk.Server.Signaling"; Path = "src/RemoteDesk.Server.Signaling/RemoteDesk.Server.Signaling.csproj"; Runtime = "win-x64" }
)

foreach ($proj in $projects) {
    $out = Join-Path $publishRoot $proj.Name
    Write-Host "Publishing $($proj.Name) (Self-Contained win-x64)..." -ForegroundColor Yellow

    $projPath = Join-Path $rootDir $proj.Path
    dotnet publish $projPath -c $Configuration -r $proj.Runtime --self-contained true -p:Version=$Version -o $out
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

# Copy or download bundled cloudflared for built-in Cloudflare Tunnel
$cfTarget = Join-Path (Join-Path $publishRoot "RemoteDesk.Viewer") "cloudflared.exe"
$cfLocalBin = Join-Path (Join-Path $rootDir "deploy") "bin\cloudflared.exe"
$cfAppData = Join-Path $env:LOCALAPPDATA "RemoteDesk\bin\cloudflared.exe"

if (Test-Path $cfLocalBin) {
    Copy-Item $cfLocalBin -Destination $cfTarget -Force
} elseif (Test-Path $cfAppData) {
    Copy-Item $cfAppData -Destination $cfTarget -Force
} else {
    try {
        Write-Host "Downloading bundled cloudflared.exe..." -ForegroundColor Yellow
        Invoke-WebRequest -Uri "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe" -OutFile $cfTarget -UseBasicParsing
        Write-Host "Bundled cloudflared.exe successfully." -ForegroundColor Green
    } catch {
        Write-Warning "Could not download cloudflared.exe. The application will download it on demand."
    }
}

# Compile Native Win32 Launcher Executable
$launcherSource = @"
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace RemoteDesk.Launcher
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var candidates = new[]
                {
                    Path.Combine(baseDir, "RemoteDesk.Viewer", "RemoteDesk.Viewer.exe"),
                    Path.Combine(baseDir, "dist", "RemoteDesk.Viewer", "RemoteDesk.Viewer.exe"),
                    Path.Combine(baseDir, "RemoteDesk.Viewer.exe")
                };

                string target = null;
                foreach (var c in candidates)
                {
                    if (File.Exists(c))
                    {
                        target = c;
                        break;
                    }
                }

                if (target != null)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = target,
                        WorkingDirectory = Path.GetDirectoryName(target),
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                else
                {
                    MessageBox.Show(
                        "RemoteDesk could not find the application binaries.\n\nPlease ensure RemoteDesk.Viewer folder exists next to this executable.",
                        "RemoteDesk Launcher",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Error launching RemoteDesk:\n" + ex.Message,
                    "RemoteDesk",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}
"@

$cscExe = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (Test-Path $cscExe) {
    Write-Host "Compiling native RemoteDesk.exe launcher..." -ForegroundColor Yellow
    $tempCs = Join-Path $env:TEMP "RemoteDeskLauncher.cs"
    Set-Content -Path $tempCs -Value $launcherSource -Encoding UTF8
    $iconPath = Join-Path $rootDir "src\RemoteDesk.Viewer\assets\icon.ico"
    $launcherPublishTarget = Join-Path $publishRoot "RemoteDesk.exe"
    $launcherArtifactTarget = Join-Path (Join-Path $rootDir "artifacts") "RemoteDesk.exe"
    if (Test-Path $iconPath) {
        & $cscExe /target:winexe /win32icon:$iconPath /out:$launcherPublishTarget $tempCs
    } else {
        & $cscExe /target:winexe /out:$launcherPublishTarget $tempCs
    }
    if (Test-Path $launcherPublishTarget) {
        Copy-Item $launcherPublishTarget $launcherArtifactTarget -Force
        Write-Host "Compiled RemoteDesk.exe successfully." -ForegroundColor Green
    } else {
        Write-Warning "Failed to compile RemoteDesk.exe"
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

$allowFirewallBat = @"
@echo off
title RemoteDesk - Configure Windows Firewall for LAN Access
cd /d "%~dp0"

echo =======================================================
echo    RemoteDesk - Windows Firewall Configuration
echo =======================================================
echo.
echo This script opens Windows Firewall ports so other computers
echo on your Wi-Fi/LAN can connect to this desktop.
echo.

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [ERROR] Must be run as Administrator!
    echo Please right-click ALLOW-FIREWALL.bat and select 'Run as administrator'.
    echo.
    pause
    exit /b 1
)

echo Adding Windows Firewall rules for RemoteDesk...
netsh advfirewall firewall add rule name="RemoteDesk Application" dir=in action=allow program="%~dp0RemoteDesk.Viewer\RemoteDesk.Viewer.exe" enable=yes profile=any
netsh advfirewall firewall add rule name="RemoteDesk Signaling (Port 5000)" dir=in action=allow protocol=TCP localport=5000 profile=any
netsh advfirewall firewall add rule name="RemoteDesk Fleet API (Port 5001)" dir=in action=allow protocol=TCP localport=5001 profile=any
netsh advfirewall firewall add rule name="RemoteDesk LAN Discovery (Port 5004)" dir=in action=allow protocol=UDP localport=5004 profile=any

echo.
echo [SUCCESS] Windows Firewall rules added successfully!
echo Other computers on your Wi-Fi/LAN can now connect seamlessly.
echo.
pause
"@
[System.IO.File]::WriteAllText((Join-Path $publishRoot "ALLOW-FIREWALL.bat"), $allowFirewallBat)

# Generate SHA-256 Checksums for primary published binaries and scripts
$checksumFile = Join-Path $publishRoot "SHA256SUMS.txt"
$files = Get-ChildItem -Path $publishRoot -Recurse -File | Where-Object { 
    $_.Extension -in @(".bat", ".ps1") -or ($_.Extension -in @(".exe", ".dll") -and $_.Name -like "RemoteDesk*")
}
$sb = [System.Text.StringBuilder]::new()

foreach ($f in $files) {
    $hash = (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $rel = $f.FullName.Substring($publishRoot.Length).TrimStart("\", "/")
    $sb.AppendLine("$hash  $rel") | Out-Null
}

[System.IO.File]::WriteAllText($checksumFile, $sb.ToString())
Write-Host "Generated SHA-256 checksums at: $checksumFile" -ForegroundColor Green

# Create Distribution Zip & RAR via local temp staging (fast and atomic so partial archives are never exposed)
$artifactsDir = Join-Path $rootDir "artifacts"
$zipPath = Join-Path $artifactsDir "RemoteDesk-v1.0-Windows.zip"
$rarPath = Join-Path $artifactsDir "RemoteDesk-v1.0-Windows.rar"

if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
if (Test-Path $rarPath) { Remove-Item -Force $rarPath }

$stageDist = Join-Path $env:TEMP "RemoteDesk-Stage-Dist"
$tempZip = Join-Path $env:TEMP "RemoteDesk-v1.0-Windows.zip"
$tempRar = Join-Path $env:TEMP "RemoteDesk-v1.0-Windows.rar"

if (Test-Path $stageDist) { cmd /c "rmdir /s /q `"$stageDist`"" }
if (Test-Path $tempZip) { Remove-Item -Force $tempZip }
if (Test-Path $tempRar) { Remove-Item -Force $tempRar }

Write-Host "Staging distribution files to local temp..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $stageDist -Force | Out-Null
& robocopy $publishRoot $stageDist /E /MT:16 /NFL /NDL /NJH /NJS /NC /NS | Out-Null

Write-Host "Creating standard Windows-compatible zip at: $zipPath..." -ForegroundColor Yellow
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stageDist, $tempZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$tempDestZip = "$zipPath.tmp"
Copy-Item -Path $tempZip -Destination $tempDestZip -Force
Move-Item -Path $tempDestZip -Destination $zipPath -Force
Remove-Item -Force $tempZip
Write-Host "Distribution zip created successfully!" -ForegroundColor Green

# Create Distribution RAR if WinRAR is installed
$winRarExe = "C:\Program Files\WinRAR\Rar.exe"
if (Test-Path $winRarExe) {
    Write-Host "Creating distribution RAR at: $rarPath..." -ForegroundColor Yellow
    & $winRarExe a -r -ep1 -m3 -mt8 "$tempRar" "$stageDist\*" | Out-Null
    if (Test-Path $tempRar) {
        $tempDestRar = "$rarPath.tmp"
        Copy-Item -Path $tempRar -Destination $tempDestRar -Force
        Move-Item -Path $tempDestRar -Destination $rarPath -Force
        Remove-Item -Force $tempRar
        Write-Host "Distribution RAR created successfully!" -ForegroundColor Green
    }
}

if (Test-Path $stageDist) { cmd /c "rmdir /s /q `"$stageDist`"" }

Write-Host "==================================================" -ForegroundColor Green
Write-Host " Packaging completed: $publishRoot" -ForegroundColor Green
Write-Host " Output Zip: $zipPath" -ForegroundColor Green
if (Test-Path $rarPath) {
    Write-Host " Output RAR: $rarPath" -ForegroundColor Green
}
Write-Host "==================================================" -ForegroundColor Green
