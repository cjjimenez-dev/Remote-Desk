# RemoteDesk Host Service Installation Script
# Requires Administrator privileges

param(
    [string]$InstallDir = "C:\Program Files\RemoteDesk",
    [string]$ServiceName = "RemoteDeskHostService",
    [string]$DisplayName = "RemoteDesk Host Service",
    [string]$ServerUrl = "http://localhost:5000/signaling",
    [string]$FleetUrl = "http://localhost:5001"
)

$ErrorActionPreference = "Stop"

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "   RemoteDesk - Windows Host Service Installer" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# Check for Administrator privileges
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be executed with elevated Administrator privileges."
    exit 1
}

$binPath = Join-Path $InstallDir "RemoteDesk.Host.Service.exe"
if (-not (Test-Path $binPath)) {
    # If not in target folder, check current script directory
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $candidate = Join-Path $scriptDir "RemoteDesk.Host.Service.exe"
    if (Test-Path $candidate) {
        $binPath = $candidate
    } else {
        Write-Warning "RemoteDesk.Host.Service.exe not found at '$binPath'. Proceeding with default path definition."
    }
}

# Stop existing service if running
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing service '$ServiceName'..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    Write-Host "Removing existing service registration..." -ForegroundColor Yellow
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Register Windows Service running as LocalSystem
Write-Host "Creating Windows Service '$ServiceName'..." -ForegroundColor Green
$createArgs = "create `"$ServiceName`" binPath= `"`"$binPath`" --server `"$ServerUrl`" --fleet-url `"$FleetUrl`"`" start= auto DisplayName= `"$DisplayName`""
$res = Start-Process -FilePath "sc.exe" -ArgumentList $createArgs -Wait -NoNewWindow -PassThru
if ($res.ExitCode -ne 0) {
    Write-Error "sc.exe create failed with code $($res.ExitCode)"
    exit $res.ExitCode
}

# Set service description
sc.exe description $ServiceName "RemoteDesk unattended host service supervising interactive desktop agents across session boundaries." | Out-Null

# Configure automatic recovery on failure (restart after 5s, 10s, 60s)
Write-Host "Configuring automatic service recovery actions..." -ForegroundColor Green
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/60000 | Out-Null

# Start the service
Write-Host "Starting '$ServiceName'..." -ForegroundColor Green
Start-Service -Name $ServiceName
Start-Sleep -Seconds 2

$status = Get-Service -Name $ServiceName
Write-Host "==================================================" -ForegroundColor Green
Write-Host " Service Status: $($status.Status)" -ForegroundColor Green
Write-Host " Installation completed successfully." -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
